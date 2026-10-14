using System.Buffers.Binary;
using DarkAges.Formats.Data;
using DarkAges.Formats.Imaging;

namespace DarkAges.Formats.Drawing;

/// <summary>
/// Legends AoC working-format image: little-endian width/height (optional flags + anchors) plus RGBA pixels.
/// Packed sequences use a `LRF1` header so gallery/preview can cycle frames.
/// </summary>
public sealed class RgbaImage
{
    public static ReadOnlySpan<byte> SequenceMagic => "LRF1"u8;
    public RgbaImage(PixelBuffer pixels, int anchorX = 0, int anchorY = 0, byte flags = 0)
    {
        Pixels = pixels;
        AnchorX = anchorX;
        AnchorY = anchorY;
        Flags = flags;
    }

    public PixelBuffer Pixels { get; }
    public int AnchorX { get; }
    public int AnchorY { get; }
    public byte Flags { get; }
    public int Width => Pixels.Width;
    public int Height => Pixels.Height;

    public static bool LooksLike(byte[] data) => PeekFrameCount(data) > 0;

    public static bool IsSequence(byte[] data) =>
        data.Length >= 6
        && data[0] == (byte)'L' && data[1] == (byte)'R' && data[2] == (byte)'F' && data[3] == (byte)'1';

    public static int PeekFrameCount(ArchiveEntry entry)
    {
        try
        {
            using Stream stream = entry.Open();
            var header = new byte[6];
            int n = stream.Read(header);
            if (n >= 6 && header[0] == (byte)'L' && header[1] == (byte)'R' && header[2] == (byte)'F' && header[3] == (byte)'1')
            {
                return Math.Max(1, (int)BitConverter.ToUInt16(header, 4));
            }
        }
        catch
        {
            // fall through
        }

        return 1;
    }

    public static int PeekFrameCount(byte[] data)
    {
        if (IsSequence(data))
        {
            return Math.Max(1, (int)BitConverter.ToUInt16(data, 4));
        }

        return TryDecode(data) is null ? 0 : 1;
    }

    public static IReadOnlyList<RgbaImage> DecodeAll(byte[] data, int maxFrames = int.MaxValue)
    {
        if (IsSequence(data))
        {
            int count = BitConverter.ToUInt16(data, 4);
            int take = Math.Min(count, Math.Max(1, maxFrames));
            var frames = new List<RgbaImage>(take);
            int o = 6;
            for (int i = 0; i < count && frames.Count < take; i++)
            {
                if (o + 4 > data.Length)
                {
                    break;
                }

                int len = BitConverter.ToInt32(data, o);
                o += 4;
                if (len < 0 || o + len > data.Length)
                {
                    break;
                }

                RgbaImage? frame = TryDecode(data.AsSpan(o, len).ToArray());
                if (frame is not null)
                {
                    frames.Add(frame);
                }

                o += len;
            }

            return frames;
        }

        RgbaImage? one = TryDecode(data);
        return one is null ? [] : [one];
    }

    public static byte[] Write(PixelBuffer pixels)
    {
        int width = Math.Max(1, pixels.Width);
        int height = Math.Max(1, pixels.Height);
        var data = new byte[4 + width * height * 4];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), (ushort)height);
        byte[] bgra = pixels.Bgra;
        int n = Math.Min(width * height, bgra.Length / 4);
        for (int i = 0; i < n; i++)
        {
            int s = i * 4;
            int d = 4 + i * 4;
            data[d] = bgra[s + 2];
            data[d + 1] = bgra[s + 1];
            data[d + 2] = bgra[s];
            data[d + 3] = bgra[s + 3];
        }

        return data;
    }

    public static byte[] Pack(IReadOnlyList<byte[]> frames)
    {
        if (frames.Count == 1)
        {
            return frames[0];
        }

        int total = 6;
        foreach (byte[] frame in frames)
        {
            total += 4 + frame.Length;
        }

        var data = new byte[total];
        SequenceMagic.CopyTo(data);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), (ushort)frames.Count);
        int o = 6;
        foreach (byte[] frame in frames)
        {
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(o), frame.Length);
            o += 4;
            frame.CopyTo(data, o);
            o += frame.Length;
        }

        return data;
    }

    public static RgbaImage? TryDecode(byte[] data)
    {
        if (IsSequence(data))
        {
            IReadOnlyList<RgbaImage> frames = DecodeAll(data, 1);
            return frames.Count == 0 ? null : frames[0];
        }

        if (data.Length < 4)
        {
            return null;
        }

        if (TryLayout(data, flagBytes: 0, anchor: false, out RgbaImage? image)
            || TryLayout(data, flagBytes: 0, anchor: true, out image)
            || TryLayout(data, flagBytes: 1, anchor: false, out image)
            || TryLayout(data, flagBytes: 1, anchor: true, out image))
        {
            return image;
        }

        return null;
    }

    private static bool TryLayout(byte[] data, int flagBytes, bool anchor, out RgbaImage? image)
    {
        image = null;
        int header = flagBytes + 4 + (anchor ? 4 : 0);
        if (data.Length < header)
        {
            return false;
        }

        int o = flagBytes;
        int width = BitConverter.ToUInt16(data, o);
        int height = BitConverter.ToUInt16(data, o + 2);
        if (width is < 1 or > 4096 || height is < 1 or > 4096)
        {
            return false;
        }

        int pixels = width * height * 4;
        int leftover = data.Length - header - pixels;
        if (leftover is not 0 and not 1)
        {
            return false;
        }

        int ax = 0;
        int ay = 0;
        if (anchor)
        {
            ax = BitConverter.ToUInt16(data, o + 4);
            ay = BitConverter.ToUInt16(data, o + 6);
        }

        byte flags = flagBytes > 0 ? data[0] : (byte)0;
        image = new RgbaImage(PixelBuffer.FromRgba(width, height, data.AsSpan(header, pixels)), ax, ay, flags);
        return true;
    }
}
