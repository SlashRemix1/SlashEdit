using DarkAges.Formats.Imaging;

namespace DarkAges.Formats.Drawing;

public static class PcxWriter
{
    public static byte[] Write(PixelBuffer pixels, Palette? palette = null, bool dither = false)
    {
        Palette pal = palette ?? ColorQuantizer.Build(pixels);
        byte[] indexed = ColorQuantizer.Index(pixels, pal, dither);
        return Write(pixels.Width, pixels.Height, indexed, pal);
    }

    public static byte[] Write(int width, int height, byte[] indexed, Palette palette)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        int bytesPerLine = (width + 1) & ~1;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)0x0A);
        writer.Write((byte)5);
        writer.Write((byte)1);
        writer.Write((byte)8);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write((short)(width - 1));
        writer.Write((short)(height - 1));
        writer.Write((short)72);
        writer.Write((short)72);
        for (int i = 0; i < 16; i++)
        {
            var (r, g, b) = palette[i];
            writer.Write(r);
            writer.Write(g);
            writer.Write(b);
        }

        writer.Write((byte)0);
        writer.Write((byte)1);
        writer.Write((short)bytesPerLine);
        writer.Write((short)1);
        writer.Write(new byte[58]);

        var row = new byte[bytesPerLine];
        for (int y = 0; y < height; y++)
        {
            int src = y * width;
            int n = Math.Min(width, Math.Max(0, indexed.Length - src));
            if (n > 0)
            {
                Buffer.BlockCopy(indexed, src, row, 0, n);
            }

            if (n < bytesPerLine)
            {
                Array.Clear(row, n, bytesPerLine - n);
            }

            WriteRle(writer, row);
        }

        writer.Write((byte)0x0C);
        writer.Write(palette.ToBytes());
        return stream.ToArray();
    }

    private static void WriteRle(BinaryWriter writer, byte[] row)
    {
        int i = 0;
        while (i < row.Length)
        {
            byte value = row[i];
            int run = 1;
            while (i + run < row.Length && row[i + run] == value && run < 63)
            {
                run++;
            }

            if (run > 1 || (value & 0xC0) == 0xC0)
            {
                writer.Write((byte)(0xC0 | run));
                writer.Write(value);
            }
            else
            {
                writer.Write(value);
            }

            i += run;
        }
    }
}
