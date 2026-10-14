using System.Collections;
using System.Text;
using DarkAges.Formats.IO;

namespace DarkAges.Formats.Data;

public sealed class DataArchive : IArchive
{
    public const int ClassicNameLength = 13;
    public const int WideNameLength = 32;

    private static readonly int[] NameLengthCandidates = [ClassicNameLength, WideNameLength];

    private readonly byte[] _data;
    private readonly Stream _stream;
    private readonly BinaryReader _reader;
    private readonly List<ArchiveEntry> _entries = new();
    private readonly Dictionary<string, ArchiveEntry> _byName = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public DataArchive(string path)
        : this(ReadFile(path), path)
    {
    }

    /// <summary>
    /// Reads the table of contents without pulling file payloads into memory.
    /// Enough to classify a DAT during workspace scan.
    /// </summary>
    public static DataArchive OpenIndex(string path)
    {
        using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            65_536,
            FileOptions.SequentialScan);

        if (fs.Length < 4)
        {
            return new DataArchive(Array.Empty<byte>(), path);
        }

        Span<byte> countBuf = stackalloc byte[4];
        fs.ReadExactly(countBuf);
        int entries = BitConverter.ToInt32(countBuf) - 1;
        if (entries < 0 || entries > 500_000)
        {
            entries = 0;
        }

        int tocLen = (int)Math.Min(fs.Length, 8L + (4L + WideNameLength) * Math.Max(0, entries));
        var buf = new byte[tocLen];
        fs.Position = 0;
        int got = fs.Read(buf, 0, buf.Length);
        if (got < buf.Length)
        {
            Array.Resize(ref buf, got);
        }

        return new DataArchive(buf, path);
    }

    public DataArchive(Stream stream, string sourcePath)
        : this(ReadFully(stream), sourcePath)
    {
    }

    public DataArchive(byte[] data, string sourcePath)
    {
        SourcePath = sourcePath;
        DisplayName = Path.GetFileName(sourcePath);
        _data = data;
        _stream = new MemoryStream(_data, writable: false);
        _reader = BinaryReaderExtensions.Create(_stream);
        ReadTableOfContents();
    }

    public string SourcePath { get; }
    public string DisplayName { get; }
    public int NameLength { get; private set; } = ClassicNameLength;
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    public ArchiveEntry? GetEntry(string name)
    {
        _byName.TryGetValue(name, out ArchiveEntry? entry);
        return entry;
    }

    public IEnumerator<ArchiveEntry> GetEnumerator() => _entries.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _reader.Dispose();
        _stream.Dispose();
        _disposed = true;
    }

    private static byte[] ReadFile(string path)
    {
        using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            256 * 1024,
            FileOptions.SequentialScan);
        return ReadFully(fs);
    }

    private static byte[] ReadFully(Stream stream)
    {
        using (stream)
        {
            if (stream is MemoryStream ms)
            {
                return ms.ToArray();
            }

            int len = stream.CanSeek ? (int)Math.Max(0, stream.Length - stream.Position) : 0;
            if (len > 0)
            {
                var data = new byte[len];
                int read = 0;
                while (read < data.Length)
                {
                    int n = stream.Read(data, read, data.Length - read);
                    if (n <= 0)
                    {
                        break;
                    }

                    read += n;
                }

                if (read < data.Length)
                {
                    Array.Resize(ref data, read);
                }

                return data;
            }

            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
    }

    private void ReadTableOfContents()
    {
        _stream.Seek(0, SeekOrigin.Begin);
        if (_stream.Length < 4)
        {
            return;
        }

        int expectedNumberOfEntries;
        try
        {
            expectedNumberOfEntries = _reader.ReadInt32() - 1;
        }
        catch
        {
            return;
        }

        if (expectedNumberOfEntries < 0 || expectedNumberOfEntries > 500_000)
        {
            return;
        }

        NameLength = DetectNameLength(_data, expectedNumberOfEntries);
        for (int i = 0; i < expectedNumberOfEntries; i++)
        {
            try
            {
                int startAddress = _reader.ReadInt32();
                byte[] nameBytes = _reader.ReadBytes(NameLength);
                if (nameBytes.Length < NameLength)
                {
                    break;
                }

                string name = DecodeName(nameBytes);
                int endAddress = _reader.ReadInt32();
                _stream.Seek(-4, SeekOrigin.Current);

                int size = endAddress - startAddress;
                if (string.IsNullOrWhiteSpace(name) || size < 0 || startAddress < 0)
                {
                    continue;
                }

                int capturedStart = startAddress;
                int capturedSize = size;
                var entry = new ArchiveEntry(name, capturedSize, () => OpenSlice(capturedStart, capturedSize));
                _entries.Add(entry);
                _byName[name] = entry;
            }
            catch
            {
                break;
            }
        }
    }

    /// <summary>
    /// Classic NA/3.x DATs store a 13-byte DOS name. Legend of Darkness (KR) stores a 32-byte name.
    /// </summary>
    public static int DetectNameLength(byte[] data, int entryCount)
    {
        int best = ClassicNameLength;
        int bestScore = -1;
        foreach (int nameLen in NameLengthCandidates)
        {
            int score = ScoreNameLength(data, entryCount, nameLen);
            if (score > bestScore)
            {
                bestScore = score;
                best = nameLen;
            }
        }

        return best;
    }

    private static int ScoreNameLength(byte[] data, int entryCount, int nameLen)
    {
        if (data.Length < 8 + nameLen)
        {
            return -1;
        }

        int pos = 4;
        int good = 0;
        int take = Math.Min(entryCount, 48);
        for (int i = 0; i < take; i++)
        {
            int endPos = pos + 4 + nameLen + 4;
            if (endPos > data.Length)
            {
                break;
            }

            int start = BitConverter.ToInt32(data, pos);
            if (!IsPlausibleName(data.AsSpan(pos + 4, nameLen)))
            {
                pos += 4 + nameLen;
                continue;
            }

            int end = BitConverter.ToInt32(data, pos + 4 + nameLen);
            if (start < 0 || end < start)
            {
                pos += 4 + nameLen;
                continue;
            }

            good++;
            pos += 4 + nameLen;
        }

        return good;
    }

    private static bool IsPlausibleName(ReadOnlySpan<byte> nameBytes)
    {
        string name = DecodeName(nameBytes);
        if (name.Length is < 1 or > 32)
        {
            return false;
        }

        foreach (char c in name)
        {
            if (c is < '!' or > '~' || c is '"' or '*' or '/' or ':' or '<' or '>' or '?' or '\\' or '|')
            {
                return false;
            }
        }

        return name.Contains('.');
    }

    private static string DecodeName(ReadOnlySpan<byte> nameBytes)
    {
        int n = nameBytes.Length;
        while (n > 0 && nameBytes[n - 1] == 0)
        {
            n--;
        }

        int zero = nameBytes[..n].IndexOf((byte)0);
        if (zero >= 0)
        {
            n = zero;
        }

        return n <= 0 ? "" : Encoding.ASCII.GetString(nameBytes[..n]);
    }

    private Stream OpenSlice(int address, int size)
    {
        if (address < 0 || size <= 0 || address >= _data.Length)
        {
            return new MemoryStream(Array.Empty<byte>(), writable: false);
        }

        int length = Math.Min(size, _data.Length - address);
        return new MemoryStream(_data, address, length, writable: false, publiclyVisible: true);
    }
}
