using DarkAges.Formats.Drawing;
using DarkAges.Formats.Imaging;
using DarkAges.Formats.Maps;

namespace DarkAges.Viewer.Maps;

public sealed class MapOverlayLayer
{
    public PixelBuffer Pixels { get; private set; } = new(1, 1);
    public bool Dirty { get; set; }
    public string? FileName { get; set; }

    public int Width => Pixels.Width;
    public int Height => Pixels.Height;
    public bool HasPixels => Pixels.HasVisiblePixels();

    public void Reset(int width, int height, string? fileName = null)
    {
        Pixels = new PixelBuffer(Math.Max(1, width), Math.Max(1, height));
        FileName = fileName;
        Dirty = false;
    }

    public void Ensure(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (Pixels.Width == width && Pixels.Height == height)
        {
            return;
        }

        var next = new PixelBuffer(width, height);
        next.Blit(Pixels, 0, 0);
        Pixels = next;
    }

    public void Pad(int left, int top, int right, int bottom)
    {
        if (!MapFile.TryPadSize(Pixels.Width, Pixels.Height, left, top, right, bottom, out int width, out int height))
        {
            Ensure(Math.Max(1, Pixels.Width + left + right), Math.Max(1, Pixels.Height + top + bottom));
            return;
        }

        if (left == 0 && top == 0 && right == 0 && bottom == 0
            && width == Pixels.Width && height == Pixels.Height)
        {
            return;
        }

        var next = new PixelBuffer(width, height);
        next.Blit(Pixels, left, top);
        Pixels = next;
        Dirty = true;
    }

    public void Clear()
    {
        Pixels.Clear();
        Dirty = true;
    }

    public void Replace(PixelBuffer tiles, string? fileName = null, bool dirty = true)
    {
        Pixels = tiles;
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            FileName = fileName;
        }

        Dirty = dirty;
    }

    public void ApplyImage(PixelBuffer image, int mapWidth, int mapHeight, bool grayscale)
    {
        PixelBuffer fitted = HeaLights.ToTileOverlay(image, mapWidth, mapHeight);
        Pixels = grayscale ? ToGray(fitted) : fitted;
        Dirty = true;
    }

    public IReadOnlyList<(int X, int Y)> Paint(
        int cx,
        int cy,
        int radius,
        int strength,
        bool erase,
        byte b,
        byte g,
        byte r)
    {
        Ensure(Math.Max(Pixels.Width, cx + 1), Math.Max(Pixels.Height, cy + 1));
        radius = Math.Clamp(radius, 0, 32);
        strength = Math.Clamp(strength, 0, 255);
        var dirty = new List<(int X, int Y)>();
        int r2 = radius * radius;
        for (int y = cy - radius; y <= cy + radius; y++)
        {
            for (int x = cx - radius; x <= cx + radius; x++)
            {
                int dx = x - cx;
                int dy = y - cy;
                if (dx * dx + dy * dy > r2
                    || (uint)x >= (uint)Pixels.Width
                    || (uint)y >= (uint)Pixels.Height)
                {
                    continue;
                }

                double fall = radius == 0 ? 1 : 1.0 - Math.Sqrt(dx * dx + dy * dy) / (radius + 0.25);
                int amount = (int)Math.Round(strength * Math.Clamp(fall, 0, 1));
                if (amount <= 0 && !erase)
                {
                    continue;
                }

                var (pb, pg, pr, pa) = Pixels.GetPixel(x, y);
                if (erase)
                {
                    int inv = Math.Max(0, 255 - Math.Max(amount, 1));
                    byte na = (byte)(pa * inv / 255);
                    Pixels.SetPixel(
                        x,
                        y,
                        (byte)(pb * inv / 255),
                        (byte)(pg * inv / 255),
                        (byte)(pr * inv / 255),
                        na);
                }
                else
                {
                    byte nb = (byte)Math.Min(255, Math.Max(pb, b * amount / 255));
                    byte ng = (byte)Math.Min(255, Math.Max(pg, g * amount / 255));
                    byte nr = (byte)Math.Min(255, Math.Max(pr, r * amount / 255));
                    byte na = (byte)Math.Min(255, Math.Max(pa, amount));
                    Pixels.SetPixel(x, y, nb, ng, nr, na);
                }

                dirty.Add((x, y));
            }
        }

        if (dirty.Count > 0)
        {
            Dirty = true;
        }

        return dirty;
    }

    public IReadOnlyList<(int X, int Y)> Stamp(PixelBuffer stamp, int cx, int cy, int radius)
    {
        radius = Math.Clamp(radius, 0, 64);
        int size = Math.Max(1, 2 * radius + 1);
        PixelBuffer fitted = stamp.Width == size && stamp.Height == size ? stamp : stamp.Resize(size, size);
        int x0 = cx - radius;
        int y0 = cy - radius;
        var dirty = new List<(int X, int Y)>();
        for (int y = 0; y < fitted.Height; y++)
        {
            for (int x = 0; x < fitted.Width; x++)
            {
                int dx = x0 + x;
                int dy = y0 + y;
                if ((uint)dx >= (uint)Pixels.Width || (uint)dy >= (uint)Pixels.Height)
                {
                    continue;
                }

                var (b, g, r, a) = fitted.GetPixel(x, y);
                if (a == 0)
                {
                    continue;
                }

                var (pb, pg, pr, pa) = Pixels.GetPixel(dx, dy);
                Pixels.SetPixel(
                    dx,
                    dy,
                    (byte)Math.Min(255, pb + b * a / 255),
                    (byte)Math.Min(255, pg + g * a / 255),
                    (byte)Math.Min(255, pr + r * a / 255),
                    (byte)Math.Min(255, (int)Math.Max(pa, a)));
                dirty.Add((dx, dy));
            }
        }

        if (dirty.Count > 0)
        {
            Dirty = true;
        }

        return dirty;
    }

    public byte[] CloneBytes()
    {
        var copy = new byte[Pixels.Bgra.Length];
        Buffer.BlockCopy(Pixels.Bgra, 0, copy, 0, copy.Length);
        return copy;
    }

    public void Restore(byte[]? data, int width, int height)
    {
        Pixels = new PixelBuffer(Math.Max(1, width), Math.Max(1, height));
        if (data is { Length: > 0 })
        {
            Buffer.BlockCopy(data, 0, Pixels.Bgra, 0, Math.Min(data.Length, Pixels.Bgra.Length));
        }
    }

    public byte[] ToHeaBytes() => HeaWriter.WriteTileMask(Pixels);

    public static PixelBuffer ToGray(PixelBuffer source)
    {
        var dest = new PixelBuffer(source.Width, source.Height);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                var (b, g, r, a) = source.GetPixel(x, y);
                if (a == 0)
                {
                    continue;
                }

                byte gray = (byte)Math.Clamp((r + g + b) / 3, 0, 255);
                if (gray == 0)
                {
                    continue;
                }

                dest.SetPixel(x, y, gray, gray, gray, 255);
            }
        }

        return dest;
    }
}
