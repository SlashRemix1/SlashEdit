using DarkAges.Formats.Imaging;

namespace DarkAges.Formats.Drawing;

/// <summary>
/// Median-cut 256-color quantizer. Index 0 is reserved for transparent (alpha &lt; 16).
/// </summary>
public static class ColorQuantizer
{
    public const int TransparentIndex = 0;
    public const byte AlphaCutoff = 16;

    public static Palette Build(PixelBuffer pixels, int maxColors = 255)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        maxColors = Math.Clamp(maxColors, 1, 255);
        var counts = new Dictionary<int, int>();
        byte[] bgra = pixels.Bgra;
        int n = pixels.Width * pixels.Height;
        for (int i = 0; i < n; i++)
        {
            int p = i * 4;
            if (bgra[p + 3] < AlphaCutoff)
            {
                continue;
            }

            int key = (bgra[p + 2] << 16) | (bgra[p + 1] << 8) | bgra[p];
            counts.TryGetValue(key, out int c);
            counts[key] = c + 1;
        }

        var palette = new Palette();
        if (counts.Count == 0)
        {
            palette.Set(1, 255, 255, 255);
            return palette;
        }

        var unique = counts.Select(kv => new ColorBin(kv.Key, kv.Value)).ToArray();
        IReadOnlyList<int> colors = unique.Length <= maxColors
            ? unique.Select(u => u.Rgb).ToList()
            : MedianCut(unique, maxColors);

        for (int i = 0; i < colors.Count && i < 255; i++)
        {
            int rgb = colors[i];
            palette.Set(i + 1, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        return palette;
    }

    public static byte[] Index(PixelBuffer pixels, Palette palette, bool dither = false)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(palette);
        int w = pixels.Width;
        int h = pixels.Height;
        var data = new byte[w * h];
        byte[] bgra = pixels.Bgra;
        if (!dither)
        {
            for (int i = 0; i < data.Length; i++)
            {
                int p = i * 4;
                data[i] = palette.NearestIndex(bgra[p + 2], bgra[p + 1], bgra[p], bgra[p + 3]);
            }

            return data;
        }

        var errR = new int[w + 2];
        var errG = new int[w + 2];
        var errB = new int[w + 2];
        var nextR = new int[w + 2];
        var nextG = new int[w + 2];
        var nextB = new int[w + 2];
        for (int y = 0; y < h; y++)
        {
            Array.Clear(nextR);
            Array.Clear(nextG);
            Array.Clear(nextB);
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                int p = i * 4;
                byte a = bgra[p + 3];
                if (a < AlphaCutoff)
                {
                    data[i] = TransparentIndex;
                    continue;
                }

                int r = ClampByte(bgra[p + 2] + errR[x + 1]);
                int g = ClampByte(bgra[p + 1] + errG[x + 1]);
                int b = ClampByte(bgra[p] + errB[x + 1]);
                byte index = palette.NearestIndex((byte)r, (byte)g, (byte)b, 255);
                data[i] = index;
                var (pr, pg, pb) = palette[index];
                int dr = r - pr;
                int dg = g - pg;
                int db = b - pb;
                Diffuse(errR, nextR, x, dr);
                Diffuse(errG, nextG, x, dg);
                Diffuse(errB, nextB, x, db);
            }

            (errR, nextR) = (nextR, errR);
            (errG, nextG) = (nextG, errG);
            (errB, nextB) = (nextB, errB);
        }

        return data;
    }

    public static (Palette Palette, byte[] Indexed) Quantize(PixelBuffer pixels, bool dither = false)
    {
        Palette palette = Build(pixels);
        return (palette, Index(pixels, palette, dither));
    }

    private static void Diffuse(int[] row, int[] next, int x, int err)
    {
        row[x + 2] += err * 7 / 16;
        next[x] += err * 3 / 16;
        next[x + 1] += err * 5 / 16;
        next[x + 2] += err / 16;
    }

    private static int ClampByte(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    private static List<int> MedianCut(ColorBin[] unique, int maxColors)
    {
        var boxes = new List<Box> { Box.ForRange(unique, 0, unique.Length) };
        while (boxes.Count < maxColors)
        {
            int best = -1;
            int bestScore = -1;
            for (int i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Count <= 1)
                {
                    continue;
                }

                int score = boxes[i].Range * boxes[i].Population;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }

            if (best < 0)
            {
                break;
            }

            (Box a, Box b) = boxes[best].Split(unique);
            boxes[best] = a;
            boxes.Add(b);
        }

        return boxes.Select(b => b.Average(unique)).ToList();
    }

    private readonly struct ColorBin(int rgb, int count)
    {
        public int Rgb { get; } = rgb;
        public int Count { get; } = count;
        public byte R => (byte)(Rgb >> 16);
        public byte G => (byte)(Rgb >> 8);
        public byte B => (byte)Rgb;
    }

    private readonly struct Box(int from, int to, int population, int range, int channel)
    {
        public int From { get; } = from;
        public int To { get; } = to;
        public int Population { get; } = population;
        public int Range { get; } = range;
        public int Channel { get; } = channel;
        public int Count => To - From;

        public static Box ForRange(ColorBin[] unique, int from, int to)
        {
            int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0, pop = 0;
            for (int i = from; i < to; i++)
            {
                ColorBin c = unique[i];
                pop += c.Count;
                minR = Math.Min(minR, c.R);
                minG = Math.Min(minG, c.G);
                minB = Math.Min(minB, c.B);
                maxR = Math.Max(maxR, c.R);
                maxG = Math.Max(maxG, c.G);
                maxB = Math.Max(maxB, c.B);
            }

            int dR = maxR - minR, dG = maxG - minG, dB = maxB - minB;
            int channel = dR >= dG && dR >= dB ? 0 : dG >= dB ? 1 : 2;
            return new Box(from, to, pop, Math.Max(dR, Math.Max(dG, dB)), channel);
        }

        public (Box A, Box B) Split(ColorBin[] unique)
        {
            int channel = Channel;
            Array.Sort(unique, From, Count, Comparer<ColorBin>.Create((a, b) =>
                channel == 0 ? a.R.CompareTo(b.R) : channel == 1 ? a.G.CompareTo(b.G) : a.B.CompareTo(b.B)));
            int half = Math.Max(1, Population / 2);
            int run = 0;
            int mid = From + 1;
            for (int i = From; i < To - 1; i++)
            {
                run += unique[i].Count;
                mid = i + 1;
                if (run >= half)
                {
                    break;
                }
            }

            return (ForRange(unique, From, mid), ForRange(unique, mid, To));
        }

        public int Average(ColorBin[] unique)
        {
            long r = 0, g = 0, b = 0, w = 0;
            for (int i = From; i < To; i++)
            {
                int c = unique[i].Count;
                r += unique[i].R * (long)c;
                g += unique[i].G * c;
                b += unique[i].B * c;
                w += c;
            }

            if (w == 0)
            {
                return unique[From].Rgb;
            }

            return ((int)(r / w) << 16) | ((int)(g / w) << 8) | (int)(b / w);
        }
    }
}
