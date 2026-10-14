using DarkAges.Formats.Data;
using DarkAges.Formats.IO;

namespace DarkAges.Formats.Drawing;

public sealed class EpfFile
{
    public EpfFile(Stream stream) => Init(stream, 0, int.MaxValue);

    public EpfFile(ArchiveEntry entry)
        : this(entry, 0, int.MaxValue)
    {
    }

    public EpfFile(ArchiveEntry entry, int firstFrame, int maxFrames)
    {
        using Stream stream = entry.Open();
        Init(stream, firstFrame, maxFrames);
    }

    public static int PeekFrameCount(ArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        if (stream.Length < 2)
        {
            return 1;
        }

        using BinaryReader reader = BinaryReaderExtensions.Create(stream);
        int n = reader.ReadInt16();
        return n is > 0 and <= 10_000 ? n : 1;
    }

    public static IReadOnlyList<EpfFrameHeader> PeekFrames(ArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        if (stream.Length < 12)
        {
            return [];
        }

        using BinaryReader reader = BinaryReaderExtensions.Create(stream);
        int expected = reader.ReadInt16();
        reader.ReadInt16();
        reader.ReadInt16();
        reader.ReadBytes(2);
        int tocAddress = reader.ReadInt32() + 12;
        if (expected is < 1 or > 10_000)
        {
            return [];
        }

        var frames = new List<EpfFrameHeader>(expected);
        for (int i = 0; i < expected; i++)
        {
            long pos = tocAddress + (long)i * 16;
            if (pos + 16 > stream.Length)
            {
                break;
            }

            stream.Seek(pos, SeekOrigin.Begin);
            int top = reader.ReadInt16();
            int left = reader.ReadInt16();
            int bottom = reader.ReadInt16();
            int right = reader.ReadInt16();
            reader.ReadInt32();
            reader.ReadInt32();
            frames.Add(new EpfFrameHeader(top, left, bottom, right));
        }

        return frames;
    }

    public int ExpectedNumberOfFrames { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public IReadOnlyList<EpfFrame> Frames { get; private set; } = Array.Empty<EpfFrame>();

    private void Init(Stream stream, int firstFrame, int maxFrames)
    {
        using BinaryReader reader = BinaryReaderExtensions.Create(stream);
        stream.Seek(0, SeekOrigin.Begin);
        if (stream.Length < 12)
        {
            return;
        }

        ExpectedNumberOfFrames = reader.ReadInt16();
        Width = reader.ReadInt16();
        Height = reader.ReadInt16();
        reader.ReadBytes(2);
        int tocAddress = reader.ReadInt32() + 12;
        if (ExpectedNumberOfFrames < 0 || ExpectedNumberOfFrames > 10_000)
        {
            Frames = Array.Empty<EpfFrame>();
            return;
        }

        int start = Math.Clamp(firstFrame, 0, ExpectedNumberOfFrames);
        int last = maxFrames == int.MaxValue
            ? ExpectedNumberOfFrames
            : Math.Min(ExpectedNumberOfFrames, start + Math.Max(1, maxFrames));
        var frames = new List<EpfFrame>(Math.Max(0, last - start));
        for (int i = start; i < last; i++)
        {
            try
            {
                stream.Seek(tocAddress + i * 16, SeekOrigin.Begin);
                int top = reader.ReadInt16();
                int left = reader.ReadInt16();
                int bottom = reader.ReadInt16();
                int right = reader.ReadInt16();
                int width = Math.Max(0, right - left);
                int height = Math.Max(0, bottom - top);
                int startAddress = reader.ReadInt32() + 12;
                int endAddress = reader.ReadInt32() + 12;
                stream.Seek(startAddress, SeekOrigin.Begin);

                int expected = width * height;
                int span = endAddress - startAddress;
                byte[] data = span == expected
                    ? reader.ReadBytes(Math.Max(0, span))
                    : reader.ReadBytes(Math.Max(0, (int)Math.Min(int.MaxValue, tocAddress - startAddress)));

                frames.Add(new EpfFrame(top, left, bottom, right, data));
            }
            catch
            {
                break;
            }
        }

        Frames = frames;
    }
}

public readonly struct EpfFrameHeader
{
    public EpfFrameHeader(int top, int left, int bottom, int right)
    {
        Top = top;
        Left = left;
        Bottom = bottom;
        Right = right;
    }

    public int Top { get; }
    public int Left { get; }
    public int Bottom { get; }
    public int Right { get; }
    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
    public bool IsEmpty => Width <= 1 && Height <= 1;
}

public sealed class EpfFrame
{
    public EpfFrame(int top, int left, int bottom, int right, byte[] data)
    {
        Top = top;
        Left = left;
        Bottom = bottom;
        Right = right;
        Data = data;
    }

    public int Top { get; }
    public int Left { get; }
    public int Bottom { get; }
    public int Right { get; }
    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
    public byte[] Data { get; }

    public Imaging.PixelBuffer Render(Palette palette, bool additive = false) =>
        palette.Render(Data, Width, Height, additive);

    public Imaging.PixelBuffer Render(Palette palette, PaletteBlit blit) =>
        palette.Render(Data, Width, Height, blit);
}
