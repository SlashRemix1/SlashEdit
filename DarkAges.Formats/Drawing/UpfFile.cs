using DarkAges.Formats.Data;
using DarkAges.Formats.Imaging;

namespace DarkAges.Formats.Drawing;

/// <summary>
/// Dialog chrome tiles. Observed files are a 16-bit header (count, width, height, unused)
/// followed by count packed 8-bit frames.
/// </summary>
public sealed class UpfFile
{
    public UpfFile(int width, int height, IReadOnlyList<byte[]> frames)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Frames = frames.ToList();
    }

    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<byte[]> Frames { get; }
    public int Count => Frames.Count;
    public int FrameBytes => Width * Height;

    public static UpfFile Load(ArchiveEntry entry) => Load(entry.ReadAllBytes());

    public static UpfFile Load(byte[] data)
    {
        if (data.Length < 8)
        {
            return new UpfFile(16, 16, []);
        }

        int count = BitConverter.ToUInt16(data, 0);
        int width = BitConverter.ToUInt16(data, 2);
        int height = BitConverter.ToUInt16(data, 4);
        if (count is < 1 or > 64 || width is < 1 or > 256 || height is < 1 or > 256)
        {
            return new UpfFile(16, 16, []);
        }

        int frameBytes = width * height;
        var frames = new List<byte[]>(count);
        int src = 8;
        for (int i = 0; i < count; i++)
        {
            var frame = new byte[frameBytes];
            int n = Math.Min(frameBytes, Math.Max(0, data.Length - src));
            if (n > 0)
            {
                Buffer.BlockCopy(data, src, frame, 0, n);
            }

            frames.Add(frame);
            src += frameBytes;
        }

        return new UpfFile(width, height, frames);
    }

    public static int PeekFrameCount(ArchiveEntry entry)
    {
        if (entry.Size < 8)
        {
            return 1;
        }

        try
        {
            using Stream stream = entry.Open();
            Span<byte> header = stackalloc byte[2];
            if (stream.Read(header) < 2)
            {
                return 1;
            }

            int count = BitConverter.ToUInt16(header);
            return count is >= 1 and <= 64 ? count : 1;
        }
        catch
        {
            return 1;
        }
    }

    public PixelBuffer Render(int index, Palette? palette = null)
    {
        Palette pal = palette ?? Fallback();
        if (Count == 0)
        {
            return new PixelBuffer(Width, Height);
        }

        byte[] frame = Frames[Math.Clamp(index, 0, Count - 1)];
        return pal.Render(frame, Width, Height);
    }

    public PixelBuffer Sheet(Palette? palette = null)
    {
        Palette pal = palette ?? Fallback();
        int columns = Math.Min(Count, 4);
        int rows = Math.Max(1, (int)Math.Ceiling(Count / (double)Math.Max(1, columns)));
        var canvas = new PixelBuffer(columns * Width, rows * Height);
        for (int i = 0; i < Count; i++)
        {
            canvas.Blit(pal.Render(Frames[i], Width, Height), (i % columns) * Width, (i / columns) * Height);
        }

        return canvas;
    }

    public UpfFile WithFrame(int index, PixelBuffer pixels, Palette palette)
    {
        var frames = Frames.ToList();
        while (frames.Count <= index)
        {
            frames.Add(new byte[FrameBytes]);
        }

        var fitted = new PixelBuffer(Width, Height);
        fitted.Blit(pixels, 0, 0);
        byte[] indexed = palette.Quantize(fitted);
        if (indexed.Length != FrameBytes)
        {
            var data = new byte[FrameBytes];
            Buffer.BlockCopy(indexed, 0, data, 0, Math.Min(indexed.Length, data.Length));
            indexed = data;
        }

        frames[index] = indexed;
        return new UpfFile(Width, Height, frames);
    }

    public byte[] ToBytes()
    {
        int frameBytes = FrameBytes;
        var data = new byte[8 + Count * frameBytes];
        BitConverter.TryWriteBytes(data.AsSpan(0, 2), (ushort)Count);
        BitConverter.TryWriteBytes(data.AsSpan(2, 2), (ushort)Width);
        BitConverter.TryWriteBytes(data.AsSpan(4, 2), (ushort)Height);
        int dst = 8;
        foreach (byte[] frame in Frames)
        {
            Buffer.BlockCopy(frame, 0, data, dst, Math.Min(frameBytes, frame.Length));
            dst += frameBytes;
        }

        return data;
    }

    public static UpfFile FromPixels(PixelBuffer pixels, Palette palette, int width = 16, int height = 16)
    {
        var fitted = new PixelBuffer(width, height);
        fitted.Blit(pixels, 0, 0);
        return new UpfFile(width, height, [palette.Quantize(fitted)]);
    }

    private static Palette Fallback()
    {
        var pal = new Palette();
        for (int i = 0; i < 256; i++)
        {
            pal.Set(i, (byte)i, (byte)i, (byte)i);
        }

        return pal;
    }
}
