using DarkAges.Formats.Data;
using DarkAges.Formats.IO;

namespace DarkAges.Formats.Drawing;

public sealed class MpfFile
{
    public MpfFile(Stream stream) => Init(stream, int.MaxValue);

    public MpfFile(ArchiveEntry entry)
        : this(entry, int.MaxValue)
    {
    }

    public MpfFile(ArchiveEntry entry, int maxFrames)
    {
        using Stream stream = entry.Open();
        Init(stream, maxFrames);
    }

    public static int PeekFrameCount(ArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using BinaryReader reader = BinaryReaderExtensions.Create(stream);
        if (stream.Length < 8)
        {
            return 1;
        }

        int marker = reader.ReadInt32();
        if (marker == -1)
        {
            int something = reader.ReadInt32();
            if (something == 4)
            {
                reader.ReadBytes(8);
            }
        }
        else
        {
            stream.Seek(-4, SeekOrigin.Current);
        }

        int n = reader.ReadByte();
        return n > 0 ? n : 1;
    }

    public static int PeekPaletteNumber(ArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using BinaryReader reader = BinaryReaderExtensions.Create(stream);
        if (stream.Length < 8)
        {
            return 0;
        }

        int marker = reader.ReadInt32();
        if (marker == -1)
        {
            int something = reader.ReadInt32();
            if (something == 4)
            {
                reader.ReadBytes(8);
            }
        }
        else
        {
            stream.Seek(-4, SeekOrigin.Current);
        }

        int expected = reader.ReadByte();
        reader.ReadInt16();
        reader.ReadInt16();
        reader.ReadInt32();
        reader.ReadByte();
        reader.ReadByte();
        if (reader.ReadInt16() == -1)
        {
            reader.ReadBytes(10);
        }
        else
        {
            stream.Seek(-2, SeekOrigin.Current);
            reader.ReadBytes(6);
        }

        int palette = 0;
        for (int i = 0; i < expected; i++)
        {
            if (stream.Position + 16 > stream.Length)
            {
                break;
            }

            int left = reader.ReadInt16();
            int top = reader.ReadInt16();
            reader.ReadInt16();
            reader.ReadInt16();
            reader.ReadInt16();
            reader.ReadInt16();
            int startAddress = reader.ReadInt32();
            if (left == -1 && top == -1)
            {
                palette = startAddress;
            }
        }

        return palette;
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int StopFrameIndex { get; private set; }
    public int StopFrameCount { get; private set; }
    public int WalkFrameIndex { get; private set; }
    public int WalkFrameCount { get; private set; }
    public int AttackFrameIndex { get; private set; }
    public int AttackFrameCount { get; private set; }
    public int StopMotionFrameCount { get; private set; }
    public int StopMotionProbability { get; private set; }
    public int Attack2FrameIndex { get; private set; }
    public int Attack2FrameCount { get; private set; }
    public int Attack3FrameIndex { get; private set; }
    public int Attack3FrameCount { get; private set; }
    public int PaletteNumber { get; private set; }
    public IReadOnlyList<int> PaletteNumbers { get; private set; } = Array.Empty<int>();
    public IReadOnlyList<MpfFrame> Frames { get; private set; } = Array.Empty<MpfFrame>();

    private void Init(Stream stream, int maxFrames)
    {
        using BinaryReader reader = BinaryReaderExtensions.Create(stream);
        stream.Seek(0, SeekOrigin.Begin);
        if (stream.Length < 8)
        {
            return;
        }

        int marker = reader.ReadInt32();
        if (marker == -1)
        {
            int something = reader.ReadInt32();
            if (something == 4)
            {
                reader.ReadBytes(8);
            }
        }
        else
        {
            stream.Seek(-4, SeekOrigin.Current);
        }

        int expectedNumberOfFrames = reader.ReadByte();
        Width = reader.ReadInt16();
        Height = reader.ReadInt16();
        int expectedDataSize = reader.ReadInt32();
        WalkFrameIndex = reader.ReadByte();
        WalkFrameCount = reader.ReadByte();

        if (reader.ReadInt16() == -1)
        {
            StopFrameIndex = reader.ReadByte();
            StopFrameCount = reader.ReadByte();
            StopMotionFrameCount = reader.ReadByte();
            StopMotionProbability = reader.ReadByte();
            AttackFrameIndex = reader.ReadByte();
            AttackFrameCount = reader.ReadByte();
            Attack2FrameIndex = reader.ReadByte();
            Attack2FrameCount = reader.ReadByte();
            Attack3FrameIndex = reader.ReadByte();
            Attack3FrameCount = reader.ReadByte();
        }
        else
        {
            stream.Seek(-2, SeekOrigin.Current);
            AttackFrameIndex = reader.ReadByte();
            AttackFrameCount = reader.ReadByte();
            StopFrameIndex = reader.ReadByte();
            StopFrameCount = reader.ReadByte();
            StopMotionFrameCount = reader.ReadByte();
            StopMotionProbability = reader.ReadByte();
        }

        long dataStart = stream.Length - expectedDataSize;
        int pixelFrom = 0;
        int pixelUntil = expectedNumberOfFrames;
        if (maxFrames <= 1)
        {
            pixelFrom = StopFrameCount > 0
                ? Math.Clamp(StopFrameIndex, 0, Math.Max(0, expectedNumberOfFrames - 1))
                : 0;
            pixelUntil = pixelFrom + 1;
        }
        else if (maxFrames < expectedNumberOfFrames)
        {
            pixelUntil = Math.Max(1, maxFrames);
        }

        var frames = new List<MpfFrame>();
        var palettes = new List<int>();
        for (int i = 0; i < expectedNumberOfFrames; i++)
        {
            try
            {
                int left = reader.ReadInt16();
                int top = reader.ReadInt16();
                int right = reader.ReadInt16();
                int bottom = reader.ReadInt16();
                int xOffset = reader.ReadInt16(true);
                int yOffset = reader.ReadInt16(true);
                int startAddress = reader.ReadInt32();

                if (left == -1 && top == -1)
                {
                    palettes.Add(startAddress);
                    PaletteNumber = startAddress;
                    continue;
                }

                int frameWidth = right - left;
                int frameHeight = bottom - top;
                byte[] data;
                bool wantPixels = i >= pixelFrom && i < pixelUntil;
                if (wantPixels && frameWidth > 0 && frameHeight > 0 && dataStart + startAddress >= 0)
                {
                    long position = stream.Position;
                    stream.Seek(dataStart + startAddress, SeekOrigin.Begin);
                    data = reader.ReadBytes(frameWidth * frameHeight);
                    stream.Seek(position, SeekOrigin.Begin);
                }
                else
                {
                    data = Array.Empty<byte>();
                }

                frames.Add(new MpfFrame(top, left, bottom, right, xOffset, yOffset, data));
            }
            catch
            {
                break;
            }
        }

        Frames = frames;
        PaletteNumbers = palettes;
    }
}

public sealed class MpfFrame
{
    public MpfFrame(int top, int left, int bottom, int right, int xOffset, int yOffset, byte[] data)
    {
        Top = top;
        Left = left;
        Bottom = bottom;
        Right = right;
        XOffset = xOffset;
        YOffset = yOffset;
        Data = data;
    }

    public int Top { get; }
    public int Left { get; }
    public int Bottom { get; }
    public int Right { get; }
    public int XOffset { get; }
    public int YOffset { get; }
    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
    public byte[] Data { get; }

    public Imaging.PixelBuffer Render(Palette palette) => palette.Render(Data, Width, Height);
}
