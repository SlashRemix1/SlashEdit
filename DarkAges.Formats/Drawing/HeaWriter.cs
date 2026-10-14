using DarkAges.Formats.Imaging;

namespace DarkAges.Formats.Drawing;

public static class HeaWriter
{
    public static byte[] Write(HeaFile source, PixelBuffer pixels)
    {
        if (source.IsMask)
        {
            return WriteMask(source, pixels);
        }

        int dw = Math.Max(1, source.DestWidth > 0 ? source.DestWidth : source.Width);
        int dh = Math.Max(1, source.DestHeight > 0 ? source.DestHeight : source.Height);
        if (source.Payload.Length == dw * dh * 2 || source.Payload.Length == 0)
        {
            return WriteRgb565(source, pixels, dw, dh);
        }

        if (source.Payload.Length == dw * dh)
        {
            return WriteIndexed(source, pixels, dw, dh);
        }

        return WriteRgb565(source, pixels, pixels.Width, pixels.Height);
    }

    public static byte[] WriteRgb565(PixelBuffer pixels)
    {
        int width = Math.Max(1, pixels.Width);
        int height = Math.Max(1, pixels.Height);
        return EncodeRgb565(Header(width, height, width, height, 0), pixels, width, height);
    }

    public static byte[] WriteRgb565(HeaFile source, PixelBuffer pixels, int width, int height) =>
        EncodeRgb565(HeaderFrom(source, width, height, canvasW: width, canvasH: height), pixels, width, height);

    private static byte[] EncodeRgb565(byte[] header, PixelBuffer pixels, int width, int height)
    {
        var fitted = new PixelBuffer(width, height);
        fitted.Blit(pixels, 0, 0);
        byte[] payload = new byte[width * height * 2];
        for (int i = 0; i < width * height; i++)
        {
            int p = i * 4;
            ushort packed = fitted.Bgra[p + 3] < 16
                ? (ushort)0
                : Pack565(fitted.Bgra[p + 2], fitted.Bgra[p + 1], fitted.Bgra[p]);
            payload[i * 2] = (byte)packed;
            payload[i * 2 + 1] = (byte)(packed >> 8);
        }

        return Concat(header, payload);
    }

    public static byte[] WriteIndexed(HeaFile source, PixelBuffer pixels, int width, int height)
    {
        var fitted = new PixelBuffer(width, height);
        fitted.Blit(pixels, 0, 0);
        Palette pal = ColorQuantizer.Build(fitted);
        byte[] indexed = ColorQuantizer.Index(fitted, pal);
        return Concat(HeaderFrom(source, width, height), indexed);
    }

    public static byte[] WriteMask(HeaFile source, PixelBuffer pixels)
    {
        int width = Math.Max(1, source.AtlasWidth > 0 ? source.AtlasWidth : pixels.Width);
        int height = Math.Max(1, source.AtlasHeight > 0 ? source.AtlasHeight : pixels.Height);
        return EncodeMask(
            HeaderFrom(source, source.DestWidth, source.DestHeight, bands: -1, canvasW: width, canvasH: height),
            pixels,
            width,
            height);
    }

    public static byte[] WriteTileMask(PixelBuffer tiles)
    {
        int width = Math.Max(1, tiles.Width);
        int height = Math.Max(1, tiles.Height);
        int atlasW = Math.Max(8, width);
        int atlasH = Math.Max(8, height);
        int bands = Math.Max(1, (int)Math.Ceiling(atlasW / 1000.0));
        var header = new byte[40];
        Write32(header, 4, Math.Max(16, atlasW));
        Write32(header, 8, Math.Max(16, atlasH));
        Write32(header, 12, width);
        Write32(header, 16, height);
        Write32(header, 20, Math.Clamp(width, 1, 512));
        Write32(header, 24, Math.Clamp(height, 1, 512));
        Write32(header, 28, atlasW);
        Write32(header, 32, atlasH);
        Write32(header, 36, bands);
        return EncodeMask(header, tiles, atlasW, atlasH);
    }

    private static byte[] EncodeMask(byte[] header, PixelBuffer pixels, int width, int height)
    {
        var fitted = new PixelBuffer(width, height);
        fitted.Blit(pixels, 0, 0);
        int bands = Math.Max(1, (int)Math.Ceiling(width / 1000.0));
        if (header.Length >= 40)
        {
            int listed = BitConverter.ToInt32(header, 36);
            if (listed > 0)
            {
                bands = listed;
            }
        }

        var starts = new uint[bands];
        for (int i = 0; i < bands; i++)
        {
            starts[i] = (uint)(i * 1000);
        }

        var runs = new List<ushort>(width * height / 4);
        var offsets = new uint[bands * height];
        for (int y = 0; y < height; y++)
        {
            int x = 0;
            for (int band = 0; band < bands; band++)
            {
                offsets[band * height + y] = (uint)runs.Count;
                int end = band + 1 < bands ? (int)starts[band + 1] : width;
                end = Math.Min(end, width);
                if (x < (int)starts[band])
                {
                    x = (int)starts[band];
                }

                while (x < end)
                {
                    byte intensity = Intensity(fitted, x, y);
                    int run = 1;
                    while (x + run < end && run < 255 && Intensity(fitted, x + run, y) == intensity)
                    {
                        run++;
                    }

                    runs.Add((ushort)(intensity | (run << 8)));
                    x += run;
                }
            }
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(header);
        foreach (uint start in starts)
        {
            writer.Write(start);
        }

        foreach (uint off in offsets)
        {
            writer.Write(off);
        }

        foreach (ushort run in runs)
        {
            writer.Write(run);
        }

        return stream.ToArray();
    }

    private static byte Intensity(PixelBuffer pixels, int x, int y)
    {
        int p = (y * pixels.Width + x) * 4;
        if (pixels.Bgra[p + 3] < 16)
        {
            return 0;
        }

        int lum = (pixels.Bgra[p] + pixels.Bgra[p + 1] + pixels.Bgra[p + 2]) / 3;
        return (byte)Math.Clamp(lum / 4, 0, 63);
    }

    private static byte[] HeaderFrom(HeaFile source, int destW, int destH, int bands = -1, int canvasW = -1, int canvasH = -1)
    {
        var data = new byte[40];
        Write32(data, 4, source.Width > 0 ? source.Width : 640);
        Write32(data, 8, source.Height > 0 ? source.Height : 480);
        Write32(data, 12, destW > 0 ? destW : source.DestWidth);
        Write32(data, 16, destH > 0 ? destH : source.DestHeight);
        Write32(data, 20, source.TileWidth);
        Write32(data, 24, source.TileHeight);
        Write32(data, 28, canvasW > 0 ? canvasW : source.AtlasWidth);
        Write32(data, 32, canvasH > 0 ? canvasH : source.AtlasHeight);
        Write32(data, 36, bands >= 0 ? bands : source.LayerCount);
        return data;
    }

    private static byte[] Header(int width, int height, int destW, int destH, int layers)
    {
        var data = new byte[40];
        Write32(data, 4, width);
        Write32(data, 8, height);
        Write32(data, 12, destW);
        Write32(data, 16, destH);
        Write32(data, 28, destW);
        Write32(data, 32, destH);
        Write32(data, 36, layers);
        return data;
    }

    private static byte[] Concat(byte[] header, byte[] payload)
    {
        var data = new byte[header.Length + payload.Length];
        Buffer.BlockCopy(header, 0, data, 0, header.Length);
        Buffer.BlockCopy(payload, 0, data, header.Length, payload.Length);
        return data;
    }

    private static void Write32(byte[] data, int offset, int value)
    {
        uint v = (uint)value;
        data[offset] = (byte)v;
        data[offset + 1] = (byte)(v >> 8);
        data[offset + 2] = (byte)(v >> 16);
        data[offset + 3] = (byte)(v >> 24);
    }

    private static ushort Pack565(byte r, byte g, byte b) =>
        (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
}
