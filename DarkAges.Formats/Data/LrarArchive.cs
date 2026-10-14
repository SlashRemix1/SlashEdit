using System.Collections;
using System.Collections.Concurrent;
using DarkAges.Formats.Catalog;
using DarkAges.Formats.Drawing;
using DarkAges.Formats.Maps;
using ZstdSharp;

namespace DarkAges.Formats.Data;

/// <summary>
/// Read-only Legends AoC / Legends Reborn LRAR (Godot working-format archive).
/// Local content.lrar only — no CDN fetch.
/// </summary>
public sealed class LrarArchive : IArchive
{
    public const string Magic = "LRAR";
    public const int HeaderSize = 256;
    public const int BlobRowSize = 48;
    public const int EntryRowSize = 20;

    private static readonly ConcurrentDictionary<string, LrarIndex> IndexCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] DirectionNames = ["", "N", "E", "S", "W"];

    private readonly LrarIndex _index;
    private readonly List<ArchiveEntry> _entries = new();
    private readonly Dictionary<string, ArchiveEntry> _byName = new(StringComparer.OrdinalIgnoreCase);

    private LrarArchive(string path, LrarIndex index, ArchiveRole role)
    {
        SourcePath = path;
        _index = index;
        DisplayName = RoleTitle(role);
        var pending = new List<LrarKey>();
        foreach (LrarKey key in index.Keys)
        {
            if (!MatchesRole(key.Kind, role))
            {
                continue;
            }

            if (key.Kind == LrarKind.AislingBody && key.Dye != 0)
            {
                continue;
            }

            pending.Add(key);
        }

        foreach (IGrouping<(byte Kind, ushort Id, ushort Direction, ushort Dye), LrarKey> group in pending
                     .GroupBy(k => (k.Kind, k.Id, k.Direction, k.Dye))
                     .OrderBy(g => g.Key.Kind)
                     .ThenBy(g => g.Key.Id)
                     .ThenBy(g => g.Key.Direction))
        {
            LrarKey[] frames = group.OrderBy(k => k.Frame).ToArray();
            string name = GroupName(frames[0]);
            if (_byName.ContainsKey(name))
            {
                continue;
            }

            LrarKey[] captured = frames;
            int size = captured.Sum(k => BlobSize(index, k.Blob));
            var entry = new ArchiveEntry(name, size, () => OpenFrames(captured));
            _entries.Add(entry);
            _byName[name] = entry;
        }
    }

    public string SourcePath { get; }
    public string DisplayName { get; }
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    public ArchiveEntry? GetEntry(string name)
    {
        _byName.TryGetValue(name, out ArchiveEntry? entry);
        return entry;
    }

    public IEnumerator<ArchiveEntry> GetEnumerator() => _entries.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() { }

    public static bool IsLrarPath(string path) =>
        path.EndsWith(".lrar", StringComparison.OrdinalIgnoreCase);

    public static bool IsLrar(string path)
    {
        if (!IsLrarPath(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> magic = stackalloc byte[4];
            return fs.Read(magic) == 4 && magic[0] == (byte)'L' && magic[1] == (byte)'R' && magic[2] == (byte)'A' && magic[3] == (byte)'R';
        }
        catch
        {
            return false;
        }
    }

    public static LrarArchive Open(string path, ArchiveRole role)
    {
        LrarIndex index = IndexCache.GetOrAdd(Path.GetFullPath(path), LoadIndex);
        return new LrarArchive(index.Path, index, role);
    }

    public static IReadOnlyList<CatalogEntry> Catalog(string path, string scanRoot)
    {
        if (!IsLrar(path))
        {
            return [];
        }

        LrarIndex index = IndexCache.GetOrAdd(Path.GetFullPath(path), LoadIndex);
        string variant = VariantName(path, scanRoot);
        var counts = new Dictionary<ArchiveRole, int>();
        var seenGroups = new HashSet<string>(StringComparer.Ordinal);
        foreach (LrarKey key in index.Keys)
        {
            ArchiveRole role = RoleFor(key.Kind);
            if (role == ArchiveRole.Unknown)
            {
                continue;
            }

            if (key.Kind == LrarKind.AislingBody && key.Dye != 0)
            {
                continue;
            }

            string group = $"{key.Kind}:{key.Id}:{key.Direction}:{key.Dye}";
            if (!seenGroups.Add(role.ToString() + group))
            {
                continue;
            }

            counts[role] = counts.GetValueOrDefault(role) + 1;
        }

        var results = new List<CatalogEntry>();
        foreach ((ArchiveRole role, int count) in counts.OrderBy(p => p.Key))
        {
            if (count <= 0)
            {
                continue;
            }

            results.Add(new CatalogEntry
            {
                SourcePath = index.Path,
                DisplayName = RoleTitle(role),
                Variant = variant,
                Role = role,
                IsFolder = false,
                EntryCount = count
            });
        }

        return results;
    }

    public static IEnumerable<string> CompanionRoots(string root)
    {
        string installed = InstalledRoot();
        if (string.IsNullOrWhiteSpace(installed) || !Directory.Exists(installed))
        {
            yield break;
        }

        string full;
        try
        {
            full = Path.GetFullPath(root);
        }
        catch
        {
            yield break;
        }

        if (PathsEqual(full, installed))
        {
            yield break;
        }

        if (LooksLikeLauncher(full))
        {
            yield return installed;
        }
    }

    public static bool LooksLikeLauncher(string dir)
    {
        try
        {
            if (Directory.EnumerateFiles(dir, "*.lrar").Any()
                || Directory.EnumerateFiles(dir, "*.pck").Any()
                || Directory.EnumerateFiles(dir, "LegendsReborn*.exe").Any()
                || Directory.EnumerateFiles(dir, "LegendsAgeOfChaos.exe").Any())
            {
                return true;
            }

            string local = InstalledRoot();
            return !string.IsNullOrWhiteSpace(local) && PathsEqual(dir, local);
        }
        catch
        {
            return false;
        }
    }

    public static string InstalledRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Legends");

    private Stream OpenFrames(LrarKey[] frames)
    {
        if (frames[0].Kind == LrarKind.Map || frames.Length == 1)
        {
            return OpenPayload(frames[0]);
        }

        var parts = new byte[frames.Length][];
        for (int i = 0; i < frames.Length; i++)
        {
            parts[i] = ReadBlob(_index, frames[i].Blob);
        }

        return new MemoryStream(RgbaImage.Pack(parts), writable: false);
    }

    private Stream OpenPayload(LrarKey key)
    {
        byte[] raw = ReadBlob(_index, key.Blob);
        if (key.Kind == LrarKind.Map)
        {
            return new MemoryStream(ToClassicMap(raw), writable: false);
        }

        if (key.Kind == LrarKind.Music && raw.Length >= 4)
        {
            int prefixed = BitConverter.ToInt32(raw, 0);
            if (prefixed > 0 && prefixed <= raw.Length - 4)
            {
                return new MemoryStream(raw, 4, prefixed, writable: false, publiclyVisible: true);
            }
        }

        return new MemoryStream(raw, writable: false);
    }

    private static byte[] ReadBlob(LrarIndex index, uint ordinal)
    {
        if (ordinal >= index.Blobs.Length)
        {
            return [];
        }

        LrarBlob blob = index.Blobs[ordinal];
        if (blob.CompressedLength == 0 || blob.UncompressedLength == 0)
        {
            return [];
        }

        var compressed = new byte[blob.CompressedLength];
        using var fs = new FileStream(index.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(index.PayloadOffset + blob.Offset, SeekOrigin.Begin);
        int read = 0;
        while (read < compressed.Length)
        {
            int n = fs.Read(compressed, read, compressed.Length - read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }

        if (compressed.Length >= 4 && compressed[0] == 0x28 && compressed[1] == 0xB5 && compressed[2] == 0x2F && compressed[3] == 0xFD)
        {
            using var decoder = new Decompressor();
            return decoder.Unwrap(compressed, blob.UncompressedLength).ToArray();
        }

        if (blob.CompressedLength == blob.UncompressedLength)
        {
            return compressed;
        }

        try
        {
            using var decoder = new Decompressor();
            return decoder.Unwrap(compressed, blob.UncompressedLength).ToArray();
        }
        catch
        {
            return compressed;
        }
    }

    private static int BlobSize(LrarIndex index, uint ordinal) =>
        ordinal < index.Blobs.Length ? index.Blobs[ordinal].UncompressedLength : 0;

    private static byte[] ToClassicMap(byte[] data)
    {
        if (data.Length < 10)
        {
            return data;
        }

        int width = BitConverter.ToUInt16(data, 2);
        int height = BitConverter.ToUInt16(data, 6);
        if (width < 1 || height < 1 || width > MapFile.MaxDimension || height > MapFile.MaxDimension)
        {
            return data;
        }

        int cells = width * height;
        int header = 10;
        int stride = 7;
        if (header + stride * cells > data.Length)
        {
            stride = 6;
        }

        if (header + stride * cells > data.Length)
        {
            return data;
        }

        var map = new MapFile(width, height, sizeHeader: MapSizeHeader.UInt16);
        for (int i = 0; i < cells; i++)
        {
            int o = header + i * stride;
            map.Tiles[i] = new MapTile(
                BitConverter.ToUInt16(data, o),
                BitConverter.ToUInt16(data, o + 2),
                BitConverter.ToUInt16(data, o + 4));
        }

        return map.ToBytes();
    }

    private static LrarIndex LoadIndex(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < HeaderSize + 8)
        {
            throw new InvalidDataException("LRAR file is too small.");
        }

        var header = new byte[HeaderSize];
        ReadExact(fs, header);
        if (header[0] != (byte)'L' || header[1] != (byte)'R' || header[2] != (byte)'A' || header[3] != (byte)'R')
        {
            throw new InvalidDataException("Not an LRAR archive.");
        }

        long indexLength = BitConverter.ToInt64(header, 20);
        long payloadOffset = BitConverter.ToInt64(header, 28);
        if (indexLength < 8 || payloadOffset < HeaderSize)
        {
            throw new InvalidDataException("LRAR header is corrupt.");
        }

        var indexBytes = new byte[indexLength];
        ReadExact(fs, indexBytes);
        int blobCount = BitConverter.ToInt32(indexBytes, 0);
        int entryCount = BitConverter.ToInt32(indexBytes, 4);
        if (blobCount < 0 || entryCount < 0)
        {
            throw new InvalidDataException("LRAR index counts are corrupt.");
        }

        int blobOff = 8;
        int entryOff = blobOff + blobCount * BlobRowSize;
        if (entryOff + entryCount * EntryRowSize > indexBytes.Length)
        {
            throw new InvalidDataException("LRAR index is truncated.");
        }

        var blobs = new LrarBlob[blobCount];
        for (int i = 0; i < blobCount; i++)
        {
            int o = blobOff + i * BlobRowSize;
            blobs[i] = new LrarBlob(
                BitConverter.ToInt64(indexBytes, o),
                BitConverter.ToInt32(indexBytes, o + 8),
                BitConverter.ToInt32(indexBytes, o + 12));
        }

        var keys = new LrarKey[entryCount];
        for (int i = 0; i < entryCount; i++)
        {
            int o = entryOff + i * EntryRowSize;
            ReadOnlySpan<byte> row = indexBytes.AsSpan(o, EntryRowSize);
            keys[i] = new LrarKey(
                row[0],
                Be16(row, 3),
                Le16(row, 5),
                Be16(row, 8),
                BitConverter.ToUInt32(indexBytes, o + 12),
                BitConverter.ToUInt32(indexBytes, o + 16));
        }

        return new LrarIndex(path, payloadOffset, blobs, keys);
    }

    private static void ReadExact(Stream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0)
            {
                throw new EndOfStreamException();
            }

            read += n;
        }
    }

    private static ushort Be16(ReadOnlySpan<byte> data, int offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);

    private static ushort Le16(ReadOnlySpan<byte> data, int offset) =>
        (ushort)(data[offset] | (data[offset + 1] << 8));

    private static string GroupName(LrarKey key)
    {
        int id = Math.Max(0, (int)key.Id);
        string dir = Dir(key.Direction);
        string facing = string.IsNullOrEmpty(dir) ? "" : $"_{dir}";
        return key.Kind switch
        {
            LrarKind.BackgroundTile => $"tile_{id:D5}.rgba",
            LrarKind.ForegroundTile => $"stc{id:D5}.rgba",
            LrarKind.Monster => $"body_{id:D5}{facing}.rgba",
            LrarKind.BodyAnim => $"banim_{id:D5}{facing}.rgba",
            LrarKind.AislingBody => $"aisling_{id:D5}{facing}.rgba",
            LrarKind.ItemSprite => $"item_{id:D5}.rgba",
            LrarKind.Map => $"map_{id:D5}.map",
            LrarKind.SkillIcon => $"skill_{id:D5}.rgba",
            LrarKind.SpellIcon => $"spell_{id:D5}.rgba",
            LrarKind.Music => LooksLikeWave(key) ? $"music_{id:D5}.wav" : $"music_{id:D5}.mp3",
            LrarKind.Effect => $"efct_{id:D5}.rgba",
            LrarKind.HudImage => $"hud_{id:D5}.rgba",
            LrarKind.SmallUi => $"ui_{id:D5}.rgba",
            _ => $"k{key.Kind}_{id:D5}{facing}.bin"
        };
    }

    private static bool LooksLikeWave(LrarKey key) => key.Id == 108;

    private static string Dir(ushort direction)
    {
        if (direction < DirectionNames.Length)
        {
            return DirectionNames[direction];
        }

        return direction.ToString();
    }

    private static bool MatchesRole(byte kind, ArchiveRole role) => RoleFor(kind) == role;

    private static ArchiveRole RoleFor(byte kind) => kind switch
    {
        LrarKind.BackgroundTile => ArchiveRole.Floors,
        LrarKind.ForegroundTile => ArchiveRole.Walls,
        LrarKind.Monster or LrarKind.BodyAnim => ArchiveRole.Mobs,
        LrarKind.AislingBody => ArchiveRole.PlayerSprites,
        LrarKind.ItemSprite => ArchiveRole.Items,
        LrarKind.Map => ArchiveRole.Maps,
        LrarKind.SkillIcon or LrarKind.SpellIcon or LrarKind.HudImage or LrarKind.SmallUi => ArchiveRole.Ui,
        LrarKind.Music => ArchiveRole.Misc,
        LrarKind.Effect => ArchiveRole.Effects,
        _ => ArchiveRole.Unknown
    };

    private static string RoleTitle(ArchiveRole role) => role switch
    {
        ArchiveRole.Floors => "AoC floors",
        ArchiveRole.Walls => "AoC walls",
        ArchiveRole.Mobs => "AoC monsters",
        ArchiveRole.PlayerSprites => "AoC bodies",
        ArchiveRole.Items => "AoC items",
        ArchiveRole.Maps => "AoC maps",
        ArchiveRole.Ui => "AoC HUD / icons",
        ArchiveRole.Misc => "AoC music",
        ArchiveRole.Effects => "AoC effects",
        _ => "AoC archive"
    };

    private static string VariantName(string path, string scanRoot)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if (full.Contains(Path.Combine("AppData", "Local", "Legends"), StringComparison.OrdinalIgnoreCase)
                || full.Contains($"{Path.DirectorySeparatorChar}Legends{Path.DirectorySeparatorChar}content-cache", StringComparison.OrdinalIgnoreCase))
            {
                return "Legends AoC";
            }

            return ArchiveClassifier.InferVariant(path, scanRoot) ?? "Legends AoC";
        }
        catch
        {
            return "Legends AoC";
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    private readonly record struct LrarBlob(long Offset, int UncompressedLength, int CompressedLength);

    private readonly record struct LrarKey(byte Kind, ushort Id, ushort Direction, ushort Dye, uint Frame, uint Blob);

    private sealed class LrarIndex
    {
        public LrarIndex(string path, long payloadOffset, LrarBlob[] blobs, LrarKey[] keys)
        {
            Path = path;
            PayloadOffset = payloadOffset;
            Blobs = blobs;
            Keys = keys;
        }

        public string Path { get; }
        public long PayloadOffset { get; }
        public LrarBlob[] Blobs { get; }
        public LrarKey[] Keys { get; }
    }
}

internal static class LrarKind
{
    public const byte BackgroundTile = 0;
    public const byte ForegroundTile = 1;
    public const byte Monster = 2;
    public const byte AislingBody = 3;
    public const byte ItemSprite = 4;
    public const byte Map = 6;
    public const byte SkillIcon = 8;
    public const byte SpellIcon = 9;
    public const byte Music = 12;
    public const byte Effect = 14;
    public const byte BodyAnim = 16;
    public const byte HudImage = 18;
    public const byte SmallUi = 20;
}
