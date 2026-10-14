using System.IO.Compression;
using DarkAges.Formats.Data;
using DarkAges.Formats.Imaging;
using DarkAges.Formats.IO;

namespace DarkAges.Formats.Drawing;

public sealed class EfaFile
{
    public EfaFile(Stream stream) => Init(stream, int.MaxValue);

    public EfaFile(ArchiveEntry entry)
        : this(entry, int.MaxValue)
    {
    }

    public EfaFile(ArchiveEntry entry, int maxFrames)
    {
        using Stream stream = entry.Open();
        Init(stream, maxFrames);
    }

    public static int PeekFrameCount(ArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        if (stream.Length < 8)
        {
            return 1;
        }

        using BinaryReader reader = BinaryReaderExtensions.Create(stream);
        reader.ReadInt32();
        int n = reader.ReadInt32();
        return n is > 0 and <= 10_000 ? n : 1;
    }

    public int ExpectedNumberOfFrames { get; private set; }
    public EfaBlend Blend { get; private set; }
    public IReadOnlyList<EfaFrame> Frames { get; private set; } = Array.Empty<EfaFrame>();

    private void Init(Stream stream, int maxFrames)
    {
        using BinaryReader reader = BinaryReaderExtensions.Create(stream);
        stream.Seek(0, SeekOrigin.Begin);
        if (stream.Length < 64)
        {
            return;
        }

        Blend = EfaBlendRules.FromRaw(reader.ReadInt32());
        ExpectedNumberOfFrames = reader.ReadInt32();
        reader.ReadBytes(56);
        if (ExpectedNumberOfFrames < 0 || ExpectedNumberOfFrames > 10_000)
        {
            return;
        }

        var headers = new EfaFrameHeader[ExpectedNumberOfFrames];
        for (int i = 0; i < ExpectedNumberOfFrames; i++)
        {
            headers[i] = EfaFrameHeader.ReadExact(reader);
        }

        int last = maxFrames == int.MaxValue
            ? ExpectedNumberOfFrames
            : Math.Min(ExpectedNumberOfFrames, Math.Max(1, maxFrames));
        var frames = new List<EfaFrame>(last);
        for (int i = 0; i < last; i++)
        {
            try
            {
                EfaFrameHeader header = headers[i];
                byte[] compressedData = reader.ReadBytes(Math.Max(0, header.Size));
                using var compressedStream = new MemoryStream(compressedData);
                using var decompressedStream = new MemoryStream();
                compressedStream.Seek(2, SeekOrigin.Begin);
                using var decompressionStream = new DeflateStream(compressedStream, CompressionMode.Decompress);
                decompressionStream.CopyTo(decompressedStream);
                frames.Add(new EfaFrame(header, decompressedStream.ToArray()));
            }
            catch
            {
                break;
            }
        }

        Frames = frames;
    }
}

public sealed class EfaFrame
{
    public EfaFrame(EfaFrameHeader header, byte[] data)
    {
        Header = header;
        Data = data;
    }

    public EfaFrameHeader Header { get; }
    public byte[] Data { get; }
    public int Width => Header.Width;
    public int PixelWidth => Width > 0 ? Width / 2 : 0;
    public int ByteCount => Header.ByteCount;
    public int PixelHeight => Width > 0 ? ByteCount / Width : 0;

    public PixelBuffer Render(EfaBlend blend = EfaBlend.Additive)
    {
        int w = Math.Max(1, PixelWidth);
        int h = Math.Max(1, PixelHeight);
        var buffer = new PixelBuffer(w, h);
        int pixels = Math.Min(Data.Length / 2, w * h);
        bool lumAlpha = EfaBlendRules.LuminanceAlpha(blend);
        for (int i = 0; i < pixels; i++)
        {
            ushort packed = (ushort)(Data[i * 2] | (Data[i * 2 + 1] << 8));
            if (packed == 0)
            {
                continue;
            }

            int r = ((packed >> 11) & 0x1F) * 255 / 31;
            int g = ((packed >> 5) & 0x3F) * 255 / 63;
            int b = (packed & 0x1F) * 255 / 31;
            byte a = lumAlpha ? (byte)Math.Clamp((r + g + b) / 3, 1, 255) : (byte)255;
            int x = i % w;
            int y = i / w;
            buffer.SetPixel(x, y, (byte)b, (byte)g, (byte)r, a);
        }

        return buffer;
    }
}

public sealed class EfaFrameHeader
{
    public int Size { get; init; }
    public int RawSize { get; init; }
    public int Width { get; init; }
    public int ByteCount { get; init; }
    public short OriginX { get; init; }
    public short OriginY { get; init; }
    public byte[] Raw { get; init; } = [];

    public static EfaFrameHeader ReadExact(BinaryReader reader)
    {
        byte[] blob = reader.ReadBytes(64);
        if (blob.Length < 64)
        {
            Array.Resize(ref blob, 64);
        }

        return new EfaFrameHeader
        {
            Size = BitConverter.ToInt32(blob, 8),
            RawSize = BitConverter.ToInt32(blob, 12),
            Width = BitConverter.ToInt32(blob, 24),
            ByteCount = BitConverter.ToInt32(blob, 32),
            OriginX = BitConverter.ToInt16(blob, 40),
            OriginY = BitConverter.ToInt16(blob, 42),
            Raw = blob
        };
    }
}
