using System.Text;
using DarkAges.Formats.Data;
using DarkAges.Formats.Imaging;

namespace DarkAges.Formats.Drawing;

public enum FntKind
{
    English,
    Korean
}

public sealed class FntFile
{
    public const int EnglishGlyphWidth = 8;
    public const int EnglishGlyphHeight = 12;
    public const int EnglishGlyphCount = 94;
    public const int EnglishFirstChar = 0x21;
    public const int KoreanGlyphWidth = 16;
    public const int KoreanGlyphHeight = 12;
    public const int KoreanGlyphCount = 2401;
    public const int EnglishBytes = EnglishGlyphCount * EnglishGlyphHeight;
    public const int KoreanBytes = KoreanGlyphCount * KoreanGlyphHeight * 2;

    public FntFile(FntKind kind, IReadOnlyList<byte[]> glyphs)
    {
        Kind = kind;
        Glyphs = glyphs.ToList();
    }

    public FntKind Kind { get; }
    public IReadOnlyList<byte[]> Glyphs { get; }
    public int GlyphWidth => Kind == FntKind.English ? EnglishGlyphWidth : KoreanGlyphWidth;
    public int GlyphHeight => Kind == FntKind.English ? EnglishGlyphHeight : KoreanGlyphHeight;
    public int RowBytes => Kind == FntKind.English ? 1 : 2;
    public int GlyphStride => GlyphHeight * RowBytes;
    public int Count => Glyphs.Count;

    public static FntFile Load(ArchiveEntry entry) => Load(entry.ReadAllBytes());

    public static FntFile Load(byte[] data)
    {
        FntKind kind = data.Length >= KoreanBytes ? FntKind.Korean : FntKind.English;
        int width = kind == FntKind.English ? EnglishGlyphWidth : KoreanGlyphWidth;
        int height = kind == FntKind.English ? EnglishGlyphHeight : KoreanGlyphHeight;
        int stride = height * ((width + 7) / 8);
        int count = kind == FntKind.English ? EnglishGlyphCount : KoreanGlyphCount;
        var glyphs = new List<byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            int start = i * stride;
            var row = new byte[stride];
            if (start < data.Length)
            {
                Buffer.BlockCopy(data, start, row, 0, Math.Min(stride, data.Length - start));
            }

            glyphs.Add(row);
        }

        return new FntFile(kind, glyphs);
    }

    public static int PeekFrameCount(ArchiveEntry entry) =>
        entry.Size >= KoreanBytes ? KoreanGlyphCount : EnglishGlyphCount;

    public PixelBuffer Render(int index, byte b = 240, byte g = 240, byte r = 240)
    {
        var dest = new PixelBuffer(GlyphWidth, GlyphHeight);
        Blit(dest, 0, 0, index, b, g, r);
        return dest;
    }

    public void Blit(PixelBuffer dest, int x, int y, int index, byte b = 240, byte g = 240, byte r = 240)
    {
        if ((uint)index >= (uint)Glyphs.Count)
        {
            return;
        }

        byte[] src = Glyphs[index];
        int rowBytes = RowBytes;
        for (int row = 0; row < GlyphHeight; row++)
        {
            int rowOff = row * rowBytes;
            for (int col = 0; col < GlyphWidth; col++)
            {
                int byteIndex = rowOff + col / 8;
                if (byteIndex >= src.Length)
                {
                    break;
                }

                int bit = 7 - (col % 8);
                if (((src[byteIndex] >> bit) & 1) != 0)
                {
                    dest.SetPixel(x + col, y + row, b, g, r, 255);
                }
            }
        }
    }

    public PixelBuffer Sheet(int columns = 16)
    {
        columns = Math.Max(1, columns);
        int rows = Math.Max(1, (int)Math.Ceiling(Count / (double)columns));
        var canvas = new PixelBuffer(columns * (GlyphWidth + 1), rows * (GlyphHeight + 1));
        for (int i = 0; i < Count; i++)
        {
            int col = i % columns;
            int row = i / columns;
            Blit(canvas, col * (GlyphWidth + 1), row * (GlyphHeight + 1), i);
        }

        return canvas;
    }

    public FntFile WithGlyph(int index, PixelBuffer pixels)
    {
        var glyphs = Glyphs.ToList();
        while (glyphs.Count <= index)
        {
            glyphs.Add(new byte[GlyphStride]);
        }

        glyphs[index] = Pack(pixels, GlyphWidth, GlyphHeight);
        return new FntFile(Kind, glyphs);
    }

    public FntFile WithGlyphs(IReadOnlyList<PixelBuffer> pixels)
    {
        int count = Kind == FntKind.English ? EnglishGlyphCount : KoreanGlyphCount;
        var glyphs = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            glyphs[i] = i < pixels.Count
                ? Pack(pixels[i], GlyphWidth, GlyphHeight)
                : new byte[GlyphStride];
        }

        return new FntFile(Kind, glyphs);
    }

    public static string KoreanCharAt(int index)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding korean = Encoding.GetEncoding(949);
        if ((uint)index >= KoreanGlyphCount)
        {
            return " ";
        }

        if (index < 2350)
        {
            int lead = 0xB0 + index / 94;
            int trail = 0xA1 + index % 94;
            return korean.GetString([(byte)lead, (byte)trail]);
        }

        return korean.GetString([0xA4, (byte)(0xA1 + (index - 2350))]);
    }

    public byte[] ToBytes()
    {
        int stride = GlyphStride;
        var data = new byte[Count * stride];
        for (int i = 0; i < Count; i++)
        {
            byte[] src = Glyphs[i];
            Buffer.BlockCopy(src, 0, data, i * stride, Math.Min(stride, src.Length));
        }

        return data;
    }

    public static byte[] Pack(PixelBuffer pixels, int width, int height)
    {
        int rowBytes = (width + 7) / 8;
        var data = new byte[rowBytes * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int sx = Math.Min(x, pixels.Width - 1);
                int sy = Math.Min(y, pixels.Height - 1);
                int p = (sy * pixels.Width + sx) * 4;
                bool on = pixels.Bgra[p + 3] >= 16
                    && pixels.Bgra[p] + pixels.Bgra[p + 1] + pixels.Bgra[p + 2] >= 80;
                if (!on)
                {
                    continue;
                }

                int bit = 7 - (x % 8);
                data[y * rowBytes + x / 8] |= (byte)(1 << bit);
            }
        }

        return data;
    }
}
