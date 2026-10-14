using DarkAges.Formats.Data;
using DarkAges.Formats.Imaging;
using DarkAges.Formats.IO;

namespace DarkAges.Formats.Drawing;

public enum SpfFormat
{
    Palettized = 0,
    Colorized = 2
}

public sealed class SpfFile
{
    public SpfFile(Stream stream) => Init(stream, int.MaxValue);

    public SpfFile(ArchiveEntry entry)
        : this(entry, int.MaxValue)
    {
    }

    public SpfFile(ArchiveEntry entry, int maxFrames)
    {
        using Stream stream = entry.Open();
        Init(stream, maxFrames);
    }

    public static int PeekFrameCount(ArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using BinaryReader reader = BinaryReaderExtensions.Create(stream);
        if (stream.Length < 16)
        {
            return 1;
        }

        reader.ReadUInt32();
        reader.ReadUInt32();
        uint formatRaw = reader.ReadUInt32();
        if (formatRaw != 2)
        {
            if (stream.Position + 1024 > stream.Length)
            {
                return 1;
            }

            reader.ReadBytes(1024);
        }

        uint frameCount = reader.ReadUInt32();
        return frameCount is > 0 and <= 10_000 ? (int)frameCount : 1;
    }

    public SpfFormat Format { get; private set; }
    public IReadOnlyList<(byte R, byte G, byte B)> EmbeddedPalette { get; private set; } = Array.Empty<(byte, byte, byte)>();
    public IReadOnlyList<SpfFrame> Frames { get; private set; } = Array.Empty<SpfFrame>();

    public PixelBuffer Render(int frameIndex, Palette? archivePalette = null)
    {
        if (Frames.Count == 0)
        {
            return new PixelBuffer(1, 1);
        }

        return Frames[Math.Clamp(frameIndex, 0, Frames.Count - 1)].Render(this, archivePalette);
    }

    private void Init(Stream stream, int maxFrames)
    {
        using BinaryReader reader = BinaryReaderExtensions.Create(stream);
        stream.Seek(0, SeekOrigin.Begin);
        if (stream.Length < 16)
        {
            return;
        }

        reader.ReadUInt32();
        reader.ReadUInt32();
        uint formatRaw = reader.ReadUInt32();
        Format = formatRaw == 2 ? SpfFormat.Colorized : SpfFormat.Palettized;

        if (Format == SpfFormat.Palettized)
        {
            var palette = new (byte R, byte G, byte B)[256];
            for (int i = 0; i < 256; i++)
            {
                palette[i] = FromRgb565(reader.ReadUInt16());
            }

            for (int i = 0; i < 256; i++)
            {
                reader.ReadUInt16(); // RGB555 copy unused for preview
            }

            EmbeddedPalette = palette;
        }

        uint frameCount = reader.ReadUInt32();
        if (frameCount > 10_000)
        {
            return;
        }

        var headers = new List<SpfFrameHeader>((int)frameCount);
        for (int i = 0; i < frameCount; i++)
        {
            headers.Add(new SpfFrameHeader(
                reader.ReadUInt16(),
                reader.ReadUInt16(),
                reader.ReadUInt16(),
                reader.ReadUInt16(),
                reader.ReadInt16(),
                reader.ReadInt16(),
                reader.ReadUInt32(),
                reader.ReadUInt32(),
                reader.ReadUInt32(),
                reader.ReadUInt32(),
                reader.ReadUInt32()));
        }

        uint blobSize = reader.ReadUInt32();
        long dataStart = stream.Position;
        byte[] blob = reader.ReadBytes((int)Math.Min(blobSize, Math.Max(0, stream.Length - dataStart)));

        int decode = maxFrames == int.MaxValue
            ? headers.Count
            : Math.Min(headers.Count, Math.Max(1, maxFrames));
        var frames = new List<SpfFrame>(headers.Count);
        for (int i = 0; i < headers.Count; i++)
        {
            frames.Add(i < decode
                ? DecodeFrame(headers[i], blob)
                : new SpfFrame(headers[i], 0, 0, Array.Empty<byte>(), null));
        }

        Frames = frames;
    }

    private SpfFrame DecodeFrame(SpfFrameHeader header, byte[] blob)
    {
        int width = Math.Max(0, header.Right - header.Left);
        int height = Math.Max(0, header.Bottom - header.Top);
        if (width <= 0 || height <= 0)
        {
            return new SpfFrame(header, width, height, Array.Empty<byte>(), null);
        }

        int start = (int)Math.Min(header.StartAddress, (uint)blob.Length);
        if (Format == SpfFormat.Colorized)
        {
            var colors = new PixelBuffer(width, height);
            int pitch = (int)header.ByteWidth;
            if (pitch <= 0)
            {
                pitch = width * 2;
            }

            for (int y = 0; y < height; y++)
            {
                int row = start + y * pitch;
                for (int x = 0; x < width; x++)
                {
                    int i = row + x * 2;
                    if (i + 1 >= blob.Length)
                    {
                        break;
                    }

                    ushort packed = (ushort)(blob[i] | (blob[i + 1] << 8));
                    var (r, g, b) = FromRgb565(packed);
                    byte a = packed == 0 ? (byte)0 : (byte)255;
                    colors.SetPixel(x, y, b, g, r, a);
                }
            }

            return new SpfFrame(header, width, height, Array.Empty<byte>(), colors);
        }

        int indexPitch = (int)header.ByteWidth;
        if (indexPitch <= 0)
        {
            indexPitch = width;
        }

        var indexed = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            int row = start + y * indexPitch;
            int copy = Math.Min(width, Math.Max(0, blob.Length - row));
            if (copy <= 0)
            {
                continue;
            }

            Buffer.BlockCopy(blob, row, indexed, y * width, copy);
        }

        return new SpfFrame(header, width, height, indexed, null);
    }

    internal static (byte R, byte G, byte B) FromRgb565(ushort packed)
    {
        int r = ((packed >> 11) & 0x1F) * 255 / 31;
        int g = ((packed >> 5) & 0x3F) * 255 / 63;
        int b = (packed & 0x1F) * 255 / 31;
        return ((byte)r, (byte)g, (byte)b);
    }
}

internal readonly record struct SpfFrameHeader(
    ushort Left,
    ushort Top,
    ushort Right,
    ushort Bottom,
    short CenterX,
    short CenterY,
    uint Flags,
    uint StartAddress,
    uint ByteWidth,
    uint ByteCount,
    uint ImageByteCount);

public sealed class SpfFrame
{
    internal SpfFrame(SpfFrameHeader header, int width, int height, byte[] indexed, PixelBuffer? colorized)
    {
        Left = header.Left;
        Top = header.Top;
        Width = width;
        Height = height;
        CenterX = header.CenterX;
        CenterY = header.CenterY;
        Indexed = indexed;
        Colorized = colorized;
    }

    public SpfFrame(int left, int top, int width, int height, byte[] indexed, PixelBuffer? colorized = null, short centerX = 0, short centerY = 0)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        CenterX = centerX;
        CenterY = centerY;
        Indexed = indexed;
        Colorized = colorized;
    }

    public int Left { get; }
    public int Top { get; }
    public int Width { get; }
    public int Height { get; }
    public short CenterX { get; }
    public short CenterY { get; }
    public byte[] Indexed { get; }
    public PixelBuffer? Colorized { get; }

    public PixelBuffer Render(SpfFile file, Palette? archivePalette)
    {
        if (Colorized is not null)
        {
            return Colorized;
        }

        var buffer = new PixelBuffer(Math.Max(1, Width), Math.Max(1, Height));
        for (int i = 0; i < Indexed.Length && i < Width * Height; i++)
        {
            byte index = Indexed[i];
            if (index == 0)
            {
                continue;
            }

            byte r, g, b;
            if (file.EmbeddedPalette.Count > index)
            {
                (r, g, b) = file.EmbeddedPalette[index];
            }
            else if (archivePalette is not null)
            {
                (r, g, b) = archivePalette[index];
            }
            else
            {
                r = g = b = index;
            }

            int x = i % Width;
            int y = i / Width;
            buffer.SetPixel(x, y, b, g, r, 255);
        }

        return buffer;
    }
}
