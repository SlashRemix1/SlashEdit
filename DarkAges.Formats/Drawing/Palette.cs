using DarkAges.Formats.Data;
using DarkAges.Formats.Imaging;
using DarkAges.Formats.IO;

namespace DarkAges.Formats.Drawing;

public enum PaletteBlit
{
    Opaque = 0,
    Additive = 1,
    LuminanceAlpha = 2
}

public sealed class Palette
{
    private readonly (byte R, byte G, byte B)[] _colors = new (byte, byte, byte)[256];

    public Palette()
    {
    }

    public Palette(Stream stream) => Init(stream);
    public Palette(ArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        Init(stream);
    }

    public static Palette FromRgb(byte[] rgb)
    {
        var palette = new Palette();
        using var stream = new MemoryStream(rgb);
        palette.Init(stream);
        return palette;
    }

    public void Set(int index, byte r, byte g, byte b)
    {
        if ((uint)index < 256)
        {
            _colors[index] = (r, g, b);
        }
    }

    public byte[] ToBytes()
    {
        var data = new byte[768];
        for (int i = 0; i < 256; i++)
        {
            var (r, g, b) = _colors[i];
            data[i * 3] = r;
            data[i * 3 + 1] = g;
            data[i * 3 + 2] = b;
        }

        return data;
    }

    public int Count => 256;
    public (byte R, byte G, byte B) this[int index] => _colors[index];

    public Palette Clone()
    {
        var copy = new Palette();
        for (int i = 0; i < 256; i++)
        {
            var (r, g, b) = _colors[i];
            copy._colors[i] = (r, g, b);
        }

        return copy;
    }

    public PixelBuffer Render(byte[] imageData, int width, int height, bool additive = false) =>
        Render(imageData, width, height, additive ? PaletteBlit.Additive : PaletteBlit.Opaque);

    public PixelBuffer Render(byte[] imageData, int width, int height, PaletteBlit blit)
    {
        if (width <= 0 || height <= 0)
        {
            return new PixelBuffer(1, 1);
        }

        var buffer = new PixelBuffer(width, height);
        byte[] dst = buffer.Bgra;
        int n = Math.Min(imageData.Length, width * height);
        for (int i = 0; i < n; i++)
        {
            byte colorIndex = imageData[i];
            if (colorIndex == 0)
            {
                continue;
            }

            var (r, g, b) = _colors[colorIndex];
            int di = i * 4;
            dst[di] = b;
            dst[di + 1] = g;
            dst[di + 2] = r;
            dst[di + 3] = blit switch
            {
                PaletteBlit.Additive or PaletteBlit.LuminanceAlpha => (byte)Math.Clamp((r + g + b) / 3, 1, 255),
                _ => (byte)255
            };
        }

        return buffer;
    }

    public byte NearestIndex(byte r, byte g, byte b, byte a, int skipStart = -1, int skipCount = 0)
    {
        if (a < 16)
        {
            return 0;
        }

        byte nearest = NearestInRange(r, g, b, skipStart, skipCount);
        if (nearest != 0)
        {
            return nearest;
        }

        return skipStart >= 0 ? NearestInRange(r, g, b, -1, 0) : (byte)1;
    }

    private byte NearestInRange(byte r, byte g, byte b, int skipStart, int skipCount)
    {
        int best = 0;
        int bestDist = int.MaxValue;
        int skipEnd = skipStart + skipCount;
        for (int i = 1; i < 256; i++)
        {
            if (skipStart >= 0 && i >= skipStart && i < skipEnd)
            {
                continue;
            }

            var (pr, pg, pb) = _colors[i];
            int dr = pr - r;
            int dg = pg - g;
            int db = pb - b;
            int dist = dr * dr + dg * dg + db * db;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = i;
                if (dist == 0)
                {
                    break;
                }
            }
        }

        return (byte)best;
    }

    public byte[] Quantize(PixelBuffer buffer)
    {
        int n = buffer.Width * buffer.Height;
        var data = new byte[n];
        byte[] bgra = buffer.Bgra;
        for (int i = 0; i < n; i++)
        {
            int p = i * 4;
            data[i] = NearestIndex(bgra[p + 2], bgra[p + 1], bgra[p], bgra[p + 3]);
        }

        return data;
    }

    public PixelBuffer RenderSwatch(int cell = 12)
    {
        var buffer = new PixelBuffer(16 * cell, 16 * cell);
        for (int i = 0; i < 256; i++)
        {
            int cx = (i % 16) * cell;
            int cy = (i / 16) * cell;
            var (r, g, b) = _colors[i];
            for (int y = 0; y < cell; y++)
            {
                for (int x = 0; x < cell; x++)
                {
                    buffer.SetPixel(cx + x, cy + y, b, g, r, 255);
                }
            }
        }

        return buffer;
    }

    private void Init(Stream stream)
    {
        using BinaryReader reader = BinaryReaderExtensions.Create(stream);
        stream.Seek(0, SeekOrigin.Begin);
        for (int i = 0; i < 256; i++)
        {
            if (stream.Position + 3 > stream.Length)
            {
                break;
            }

            byte r = reader.ReadByte();
            byte g = reader.ReadByte();
            byte b = reader.ReadByte();
            _colors[i] = (r, g, b);
        }
    }
}
