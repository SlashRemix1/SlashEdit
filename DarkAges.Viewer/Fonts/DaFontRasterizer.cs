using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ImagePixelFormat = System.Drawing.Imaging.PixelFormat;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using DarkAges.Formats.Data;
using DarkAges.Formats.Drawing;
using DarkAges.Formats.Imaging;
using Sd = System.Drawing;
using SdFont = System.Drawing.Font;
using SdFontFamily = System.Drawing.FontFamily;
using SdFontStyle = System.Drawing.FontStyle;
using SdGraphics = System.Drawing.Graphics;

namespace DarkAges.Viewer.Fonts;

public readonly record struct DaFontImportResult(FntFile? Fnt, LftFile? Lft, string Summary);

public static class DaFontRasterizer
{
    public static DaFontImportResult Replace(string path, FntFile current, IProgress<(int Done, int Total, string Name)>? progress = null)
    {
        using var fonts = LoadFaces(path);
        SdFontFamily family = fonts.Families[0];
        int width = current.GlyphWidth;
        int height = current.GlyphHeight;
        GlyphTypeface? cmap = TryCmap(path);
        float size = FitSize(family, width, height, current.Kind == FntKind.Korean ? "한Wgy@" : "W@gypQ|[]", cmap);
        using var font = new SdFont(family, size, SdFontStyle.Regular, GraphicsUnit.Pixel);
        int count = current.Count;
        var glyphs = new PixelBuffer[count];
        for (int i = 0; i < count; i++)
        {
            progress?.Report((i, count, current.Kind == FntKind.English ? $"{(char)(FntFile.EnglishFirstChar + i)}" : $"glyph {i}"));
            string text = current.Kind == FntKind.English
                ? char.ConvertFromUtf32(FntFile.EnglishFirstChar + i)
                : FntFile.KoreanCharAt(i);
            glyphs[i] = HasGlyph(cmap, text)
                ? Rasterize(font, family, text, width, height).Pixels
                : new PixelBuffer(width, height);
        }

        return new DaFontImportResult(
            current.WithGlyphs(glyphs),
            null,
            $"{family.Name} → {current.Kind.ToString().ToLowerInvariant()} FNT  {width}×{height}  {size:0.#}px  {count} glyphs");
    }

    public static DaFontImportResult Replace(string path, LftFile current, IProgress<(int Done, int Total, string Name)>? progress = null)
    {
        using var fonts = LoadFaces(path);
        SdFontFamily family = fonts.Families[0];
        int width = Math.Max(1, (int)current.NominalWidth);
        int height = Math.Max(1, (int)current.NominalHeight);
        GlyphTypeface? cmap = TryCmap(path);
        float size = FitSize(family, width, height, "W@gypQ|한", cmap);
        using var font = new SdFont(family, size, SdFontStyle.Regular, GraphicsUnit.Pixel);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var packed = new Dictionary<int, LftPackedGlyph>();

        packed[0x08] = Empty(0);
        packed[0x09] = Empty(0);
        packed[0x0A] = Empty(0);
        packed[0x0D] = Empty(0);

        int planned = 256;
        progress?.Report((0, planned, "ANSI"));
        for (int key = 0; key < 256; key++)
        {
            if (key is 0x08 or 0x09 or 0x0A or 0x0D)
            {
                continue;
            }

            string text = LatinText(key);
            if (key == 0x20 || !HasGlyph(cmap, text))
            {
                byte advance = key == 0x20
                    ? MeasureAdvance(font, " ", width)
                    : (byte)Math.Max(1, width / 2);
                packed[key] = Empty(advance);
                continue;
            }

            GlyphImage image = Rasterize(font, family, text, width, height);
            packed[key] = LftFile.PackGlyph(image.Pixels, image.Advance);
        }

        Encoding korean = Encoding.GetEncoding(949);
        Encoding japanese = Encoding.GetEncoding(932);
        int total = planned + 94 * 126;
        int done = 256;
        for (int lead = 0x81; lead <= 0xFE; lead++)
        {
            for (int trail = 0x40; trail <= 0xFE; trail++)
            {
                int key = (lead << 8) | trail;
                if ((uint)key >= LftFile.GlyphKeys)
                {
                    continue;
                }

                done++;
                if ((done & 63) == 0)
                {
                    progress?.Report((Math.Min(done, total), total, $"DBCS {lead:X2}{trail:X2}"));
                }

                if (!TryDbcsText(korean, lead, trail, out string text)
                    && !TryDbcsText(japanese, lead, trail, out text))
                {
                    continue;
                }

                if (!HasGlyph(cmap, text))
                {
                    continue;
                }

                GlyphImage image = Rasterize(font, family, text, width, height);
                packed[key] = LftFile.PackGlyph(image.Pixels, image.Advance);
            }
        }

        LftFile next = current.WithPackedGlyphs(packed);
        return new DaFontImportResult(
            null,
            next,
            $"{family.Name} → LFT  {width}×{height}  {size:0.#}px  {packed.Count} keys replaced");
    }

    public static bool IsTrueType(string path)
    {
        string ext = System.IO.Path.GetExtension(path);
        return ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".otf", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ttc", StringComparison.OrdinalIgnoreCase);
    }

    private static PrivateFontCollection LoadFaces(string path)
    {
        var fonts = new PrivateFontCollection();
        fonts.AddFontFile(path);
        if (fonts.Families.Length == 0)
        {
            fonts.Dispose();
            throw new InvalidOperationException("That file does not contain a usable font family.");
        }

        return fonts;
    }

    private static GlyphTypeface? TryCmap(string path)
    {
        try
        {
            var uri = new Uri(System.IO.Path.GetFullPath(path));
            return new GlyphTypeface(uri);
        }
        catch
        {
            return null;
        }
    }

    private static bool HasGlyph(GlyphTypeface? cmap, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (cmap is null)
        {
            return text[0] is >= ' ' and not '\uFFFD';
        }

        int code = char.ConvertToUtf32(text, 0);
        return cmap.CharacterToGlyphMap.ContainsKey(code);
    }

    private static string LatinText(int key)
    {
        if (key is >= 0x20 and <= 0x7E)
        {
            return ((char)key).ToString();
        }

        try
        {
            string text = MetaText.Latin.GetString([(byte)key]);
            return string.IsNullOrEmpty(text) || text[0] == '\uFFFD' ? ((char)key).ToString() : text;
        }
        catch
        {
            return ((char)key).ToString();
        }
    }

    private static bool TryDbcsText(Encoding encoding, int lead, int trail, out string text)
    {
        try
        {
            byte[] pair = [(byte)lead, (byte)trail];
            text = encoding.GetString(pair);
            if (string.IsNullOrEmpty(text) || text[0] == '\uFFFD')
            {
                return false;
            }

            byte[] round = encoding.GetBytes(text);
            return round.Length == 2 && round[0] == lead && round[1] == trail;
        }
        catch
        {
            text = "";
            return false;
        }
    }

    private static float FitSize(SdFontFamily family, int maxWidth, int maxHeight, string probe, GlyphTypeface? cmap)
    {
        float lo = 4f;
        float hi = Math.Max(maxHeight + 4, 8);
        float best = Math.Min(maxHeight, 8);
        string sampleChar = ProbeChar(probe, cmap);
        for (int i = 0; i < 18; i++)
        {
            float mid = (lo + hi) / 2f;
            using var font = new SdFont(family, mid, SdFontStyle.Regular, GraphicsUnit.Pixel);
            float cell = CellHeight(font, family);
            int wide = 0;
            foreach (char ch in "WM한")
            {
                if (HasGlyph(cmap, ch.ToString()))
                {
                    wide = Math.Max(wide, MeasureAdvance(font, ch.ToString(), 255));
                }
            }

            GlyphImage sample = Rasterize(font, family, sampleChar, maxWidth + 8, maxHeight + 8);
            int inkH = InkHeight(sample.Pixels);
            if (cell <= maxHeight && (wide == 0 || wide <= maxWidth) && inkH <= maxHeight)
            {
                best = mid;
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return Math.Clamp(best, 4f, maxHeight);
    }

    private static string ProbeChar(string probe, GlyphTypeface? cmap)
    {
        foreach (char ch in probe)
        {
            string text = ch.ToString();
            if (HasGlyph(cmap, text))
            {
                return text;
            }
        }

        return "A";
    }

    private static float CellHeight(SdFont font, SdFontFamily family)
    {
        float em = family.GetEmHeight(font.Style);
        if (em <= 0)
        {
            return font.Size;
        }

        return (family.GetCellAscent(font.Style) + family.GetCellDescent(font.Style)) * font.Size / em;
    }

    private static byte MeasureAdvance(SdFont font, string text, int cap)
    {
        using var bmp = new Bitmap(1, 1, ImagePixelFormat.Format32bppArgb);
        using SdGraphics g = SdGraphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
        var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        SizeF size = g.MeasureString(text, font, int.MaxValue, format);
        int advance = (int)Math.Round(size.Width, MidpointRounding.AwayFromZero);
        return (byte)Math.Clamp(advance, 1, cap);
    }

    private static GlyphImage Rasterize(SdFont font, SdFontFamily family, string text, int cellWidth, int cellHeight)
    {
        int canvasW = Math.Max(cellWidth + 4, 8);
        int canvasH = Math.Max(cellHeight + 4, 8);
        using var bmp = new Bitmap(canvasW, canvasH, ImagePixelFormat.Format32bppArgb);
        using SdGraphics g = SdGraphics.FromImage(bmp);
        g.Clear(Sd.Color.Transparent);
        g.SmoothingMode = SmoothingMode.None;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
        float em = family.GetEmHeight(font.Style);
        float ascent = em > 0 ? family.GetCellAscent(font.Style) * font.Size / em : font.Size;
        float top = Math.Max(0, (cellHeight - CellHeight(font, family)) / 2f);
        g.DrawString(text, font, Sd.Brushes.White, new PointF(1, top + 0.5f), StringFormat.GenericTypographic);
        PixelBuffer raw = FromBitmap(bmp);
        PixelBuffer fitted = Contain(raw, cellWidth, cellHeight);
        byte advance = MeasureAdvance(font, text, Math.Max(cellWidth, 1));
        int inkRight = InkRight(fitted);
        if (inkRight > 0)
        {
            advance = (byte)Math.Clamp(Math.Max((int)advance, inkRight), 1, Math.Max(cellWidth, 1));
        }

        return new GlyphImage(fitted, advance);
    }

    private static PixelBuffer Contain(PixelBuffer source, int width, int height)
    {
        int minX = source.Width;
        int minY = source.Height;
        int maxX = -1;
        int maxY = -1;
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                if (source.GetPixel(x, y).A < 16)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        var dest = new PixelBuffer(width, height);
        if (maxX < minX)
        {
            return dest;
        }

        int inkW = maxX - minX + 1;
        int inkH = maxY - minY + 1;
        int dx = Math.Clamp((width - Math.Min(inkW, width)) / 2, 0, Math.Max(0, width - 1));
        int dy = Math.Clamp((height - Math.Min(inkH, height)) / 2, 0, Math.Max(0, height - 1));
        for (int y = 0; y < inkH && dy + y < height; y++)
        {
            for (int x = 0; x < inkW && dx + x < width; x++)
            {
                var (b, g, r, a) = source.GetPixel(minX + x, minY + y);
                if (a >= 16)
                {
                    dest.SetPixel(dx + x, dy + y, b, g, r, a);
                }
            }
        }

        return dest;
    }

    private static int InkHeight(PixelBuffer pixels)
    {
        int minY = pixels.Height;
        int maxY = -1;
        for (int y = 0; y < pixels.Height; y++)
        {
            for (int x = 0; x < pixels.Width; x++)
            {
                if (sourceVisible(pixels, x, y))
                {
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        return maxY < minY ? 0 : maxY - minY + 1;

        static bool sourceVisible(PixelBuffer p, int x, int y) => p.GetPixel(x, y).A >= 16;
    }

    private static int InkRight(PixelBuffer pixels)
    {
        int maxX = -1;
        for (int y = 0; y < pixels.Height; y++)
        {
            for (int x = 0; x < pixels.Width; x++)
            {
                if (pixels.GetPixel(x, y).A >= 16)
                {
                    maxX = Math.Max(maxX, x);
                }
            }
        }

        return maxX + 1;
    }

    private static PixelBuffer FromBitmap(Bitmap bmp)
    {
        var dest = new PixelBuffer(bmp.Width, bmp.Height);
        BitmapData data = bmp.LockBits(
            new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly,
            ImagePixelFormat.Format32bppArgb);
        try
        {
            int row = bmp.Width * 4;
            for (int y = 0; y < bmp.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, dest.Bgra, y * row, row);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        return dest;
    }

    private static LftPackedGlyph Empty(byte advance) => new(advance, 0, 0, 0, 0, []);

    private readonly record struct GlyphImage(PixelBuffer Pixels, byte Advance);
}
