using System.IO.Compression;
using DarkAges.Formats.Imaging;

namespace DarkAges.Formats.Drawing;

public static class EfaWriter
{
    public static byte[] Write(IReadOnlyList<EfaFrame> frames, EfaBlend blend = EfaBlend.Additive)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var compressed = new List<byte[]>(frames.Count);
        var headers = new List<byte[]>(frames.Count);
        for (int i = 0; i < frames.Count; i++)
        {
            EfaFrame frame = frames[i];
            byte[] payload = frame.Data ?? [];
            byte[] deflated = Deflate(payload);
            compressed.Add(deflated);
            headers.Add(BuildHeader(frame, deflated.Length, payload.Length));
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((int)blend);
        writer.Write(frames.Count);
        writer.Write(new byte[56]);
        foreach (byte[] header in headers)
        {
            writer.Write(header);
        }

        foreach (byte[] blob in compressed)
        {
            writer.Write(blob);
        }

        return stream.ToArray();
    }

    public static byte[] FromPixels(
        PixelBuffer pixels,
        short originX = 0,
        short originY = 0,
        EfaBlend blend = EfaBlend.Additive)
    {
        int w = Math.Max(1, pixels.Width);
        int h = Math.Max(1, pixels.Height);
        var data = new byte[w * h * 2];
        for (int i = 0; i < w * h; i++)
        {
            int p = i * 4;
            ushort packed = 0;
            if (p + 3 < pixels.Bgra.Length && pixels.Bgra[p + 3] >= 16)
            {
                packed = SpfWriter.ToRgb565(pixels.Bgra[p + 2], pixels.Bgra[p + 1], pixels.Bgra[p]);
            }

            data[i * 2] = (byte)packed;
            data[i * 2 + 1] = (byte)(packed >> 8);
        }

        var header = new EfaFrameHeader
        {
            Size = 0,
            RawSize = data.Length,
            Width = w * 2,
            ByteCount = data.Length,
            OriginX = originX,
            OriginY = originY,
            Raw = new byte[64]
        };
        return Write([new EfaFrame(header, data)], blend);
    }

    private static byte[] Deflate(byte[] payload)
    {
        using var output = new MemoryStream();
        output.WriteByte(0x78);
        output.WriteByte(0x9C);
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(payload);
        }

        return output.ToArray();
    }

    private static byte[] BuildHeader(EfaFrame frame, int compressedSize, int rawSize)
    {
        byte[] raw = frame.Header.Raw is { Length: 64 } ? (byte[])frame.Header.Raw.Clone() : new byte[64];
        WriteInt(raw, 8, compressedSize);
        WriteInt(raw, 12, rawSize);
        WriteInt(raw, 24, frame.Header.Width);
        WriteInt(raw, 32, frame.Header.ByteCount);
        WriteShort(raw, 40, frame.Header.OriginX);
        WriteShort(raw, 42, frame.Header.OriginY);
        return raw;
    }

    private static void WriteInt(byte[] data, int offset, int value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteShort(byte[] data, int offset, short value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
    }
}
