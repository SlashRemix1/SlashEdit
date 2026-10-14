using DarkAges.Formats.Imaging;

namespace DarkAges.Formats.Drawing;

public static class SpfWriter
{
    public static byte[] WritePalettized(Palette palette, IReadOnlyList<SpfFrame> frames)
    {
        var pal = new (byte R, byte G, byte B)[256];
        for (int i = 0; i < 256; i++)
        {
            pal[i] = palette[i];
        }

        return Write(SpfFormat.Palettized, pal, frames);
    }

    public static byte[] WriteColorized(IReadOnlyList<SpfFrame> frames) =>
        Write(SpfFormat.Colorized, [], frames);

    public static byte[] Write(SpfFile file, IReadOnlyList<SpfFrame>? frames = null) =>
        Write(file.Format, file.EmbeddedPalette, frames ?? file.Frames);

    private static byte[] Write(
        SpfFormat format,
        IReadOnlyList<(byte R, byte G, byte B)> pal,
        IReadOnlyList<SpfFrame> list)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(format == SpfFormat.Colorized ? 2u : 0u);

        if (format != SpfFormat.Colorized)
        {
            for (int i = 0; i < 256; i++)
            {
                (byte r, byte g, byte b) = i < pal.Count ? pal[i] : ((byte)0, (byte)0, (byte)0);
                writer.Write(ToRgb565(r, g, b));
            }

            for (int i = 0; i < 256; i++)
            {
                (byte r, byte g, byte b) = i < pal.Count ? pal[i] : ((byte)0, (byte)0, (byte)0);
                writer.Write(ToRgb555(r, g, b));
            }
        }

        writer.Write((uint)list.Count);
        using var blob = new MemoryStream();
        var starts = new uint[list.Count];
        var byteWidths = new uint[list.Count];
        var byteCounts = new uint[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            SpfFrame frame = list[i];
            starts[i] = (uint)blob.Length;
            int width = Math.Max(0, frame.Width);
            int height = Math.Max(0, frame.Height);
            if (format == SpfFormat.Colorized)
            {
                int pitch = width * 2;
                byteWidths[i] = (uint)pitch;
                PixelBuffer colors = frame.Colorized ?? new PixelBuffer(Math.Max(1, width), Math.Max(1, height));
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int p = (y * colors.Width + x) * 4;
                        ushort packed = 0;
                        if (p + 3 < colors.Bgra.Length && colors.Bgra[p + 3] >= 16)
                        {
                            packed = ToRgb565(colors.Bgra[p + 2], colors.Bgra[p + 1], colors.Bgra[p]);
                        }

                        blob.WriteByte((byte)packed);
                        blob.WriteByte((byte)(packed >> 8));
                    }
                }

                byteCounts[i] = (uint)(pitch * height);
            }
            else
            {
                byteWidths[i] = (uint)width;
                byte[] indexed = frame.Indexed ?? [];
                int need = width * height;
                int take = Math.Min(need, indexed.Length);
                if (take > 0)
                {
                    blob.Write(indexed, 0, take);
                }

                if (take < need)
                {
                    blob.Write(new byte[need - take]);
                }

                byteCounts[i] = (uint)need;
            }
        }

        byte[] data = blob.ToArray();
        for (int i = 0; i < list.Count; i++)
        {
            SpfFrame frame = list[i];
            writer.Write((ushort)frame.Left);
            writer.Write((ushort)frame.Top);
            writer.Write((ushort)(frame.Left + frame.Width));
            writer.Write((ushort)(frame.Top + frame.Height));
            writer.Write(frame.CenterX);
            writer.Write(frame.CenterY);
            writer.Write(0u);
            writer.Write(starts[i]);
            writer.Write(byteWidths[i]);
            writer.Write(byteCounts[i]);
            writer.Write(byteCounts[i]);
        }

        writer.Write((uint)data.Length);
        writer.Write(data);
        return stream.ToArray();
    }

    public static ushort ToRgb565(byte r, byte g, byte b) =>
        (ushort)(((r * 31 / 255) << 11) | ((g * 63 / 255) << 5) | (b * 31 / 255));

    public static ushort ToRgb555(byte r, byte g, byte b) =>
        (ushort)(((r * 31 / 255) << 10) | ((g * 31 / 255) << 5) | (b * 31 / 255));
}
