using DarkAges.Formats.Data;
using DarkAges.Formats.Imaging;

namespace DarkAges.Formats.Drawing;

public readonly struct LftGlyph
{
    public LftGlyph(byte advance, byte left, byte top, byte right, byte bottom, ushort packedSize, uint bitmapOffset)
    {
        Advance = advance;
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
        PackedSize = packedSize;
        BitmapOffset = bitmapOffset;
    }

    public byte Advance { get; }
    public byte Left { get; }
    public byte Top { get; }
    public byte Right { get; }
    public byte Bottom { get; }
    public ushort PackedSize { get; }
    public uint BitmapOffset { get; }
    public int PixelWidth => Math.Max(0, Right - Left);
    public int PixelHeight => Math.Max(0, Bottom - Top);
    public int RowStride => ((PixelWidth + 31) / 32) * 4;
    public bool HasBitmap => BitmapOffset != 0 && PixelWidth > 0 && PixelHeight > 0;
}

public readonly record struct LftPackedGlyph(byte Advance, byte Left, byte Top, byte Right, byte Bottom, byte[] Bitmap);

public sealed class LftFile
{
    public const int GlyphKeys = 65535;
    public const int RecordSize = 11;
    public const int HeaderSize = 4;
    public const int BitmapBase = HeaderSize + GlyphKeys * RecordSize;

    public LftFile(ushort nominalWidth, ushort nominalHeight, IReadOnlyList<LftGlyph> glyphs, byte[] bitmap)
    {
        NominalWidth = nominalWidth;
        NominalHeight = nominalHeight;
        Glyphs = glyphs;
        Bitmap = bitmap;
    }

    public ushort NominalWidth { get; }
    public ushort NominalHeight { get; }
    public IReadOnlyList<LftGlyph> Glyphs { get; }
    public byte[] Bitmap { get; }

    public static LftFile Load(ArchiveEntry entry) => Load(entry.ReadAllBytes());

    public static LftFile Load(byte[] data)
    {
        if (data.Length < BitmapBase)
        {
            return new LftFile(12, 12, Array.Empty<LftGlyph>(), []);
        }

        ushort width = BitConverter.ToUInt16(data, 0);
        ushort height = BitConverter.ToUInt16(data, 2);
        var glyphs = new LftGlyph[GlyphKeys];
        int pos = HeaderSize;
        for (int i = 0; i < GlyphKeys; i++)
        {
            glyphs[i] = new LftGlyph(
                data[pos],
                data[pos + 1],
                data[pos + 2],
                data[pos + 3],
                data[pos + 4],
                BitConverter.ToUInt16(data, pos + 5),
                NormalizeOffset(BitConverter.ToUInt32(data, pos + 7)));
            pos += RecordSize;
        }

        int bitmapLen = Math.Max(0, data.Length - BitmapBase);
        var bitmap = new byte[bitmapLen];
        if (bitmapLen > 0)
        {
            Buffer.BlockCopy(data, BitmapBase, bitmap, 0, bitmapLen);
        }

        return new LftFile(width, height, glyphs, bitmap);
    }

    public PixelBuffer Render(int key, byte b = 240, byte g = 240, byte r = 240)
    {
        int w = Math.Max(1, (int)NominalWidth);
        int h = Math.Max(1, (int)NominalHeight);
        var dest = new PixelBuffer(w, h);
        Blit(dest, 0, 0, key, b, g, r);
        return dest;
    }

    public void Blit(PixelBuffer dest, int penX, int lineTop, int key, byte b = 240, byte g = 240, byte r = 240)
    {
        if ((uint)key >= (uint)Glyphs.Count)
        {
            return;
        }

        LftGlyph glyph = Glyphs[key];
        if (!glyph.HasBitmap)
        {
            return;
        }

        int width = glyph.PixelWidth;
        int height = glyph.PixelHeight;
        int stride = glyph.RowStride;
        long start = glyph.BitmapOffset;
        long end = start + (long)stride * height;
        if (start < 0 || end > Bitmap.Length)
        {
            return;
        }

        for (int y = 0; y < height; y++)
        {
            int row = (int)start + y * stride;
            for (int x = 0; x < width; x++)
            {
                byte bit = (byte)(0x80 >> (x & 7));
                if ((Bitmap[row + x / 8] & bit) != 0)
                {
                    dest.SetPixel(penX + glyph.Left + x, lineTop + glyph.Top + y, b, g, r, 255);
                }
            }
        }
    }

    public PixelBuffer RenderText(string text, byte b = 240, byte g = 240, byte r = 240)
    {
        int width = Math.Max(1, Measure(text));
        int height = Math.Max(1, (int)NominalHeight);
        var dest = new PixelBuffer(width, height);
        int x = 0;
        foreach (char ch in text)
        {
            int key = ch & 0xFF;
            Blit(dest, x, 0, key, b, g, r);
            x += Advance(key);
        }

        return dest;
    }

    public int Advance(int key)
    {
        if (key is 0x08 or 0x09 or 0x0A or 0x0D)
        {
            return 0;
        }

        if ((uint)key >= (uint)Glyphs.Count)
        {
            return NominalWidth / 2;
        }

        LftGlyph glyph = Glyphs[key];
        if (key == 0x20 && glyph.BitmapOffset == 0)
        {
            return Math.Max(1, NominalWidth / 2);
        }

        return glyph.Advance;
    }

    public int Measure(string text)
    {
        int width = 0;
        foreach (char ch in text)
        {
            width += Advance(ch & 0xFF);
        }

        return Math.Max(1, width);
    }

    public LftFile WithGlyph(int key, PixelBuffer pixels, byte? advance = null) =>
        WithPackedGlyphs(new Dictionary<int, LftPackedGlyph> { [key] = PackGlyph(pixels, advance) });

    public LftFile WithPackedGlyphs(IReadOnlyDictionary<int, LftPackedGlyph> packed)
    {
        var glyphs = new LftGlyph[GlyphKeys];
        for (int i = 0; i < GlyphKeys; i++)
        {
            glyphs[i] = i < Glyphs.Count ? Glyphs[i] : default;
        }

        var overlay = new byte[GlyphKeys][];
        foreach ((int key, LftPackedGlyph glyph) in packed)
        {
            if ((uint)key >= GlyphKeys)
            {
                continue;
            }

            glyphs[key] = new LftGlyph(
                glyph.Advance,
                glyph.Left,
                glyph.Top,
                glyph.Right,
                glyph.Bottom,
                (ushort)glyph.Bitmap.Length,
                glyph.Bitmap.Length == 0 ? 0u : 1u);
            overlay[key] = glyph.Bitmap;
        }

        return Rebuild(NominalWidth, NominalHeight, glyphs, CopyBitmaps(overlay, glyphs));
    }

    public static LftPackedGlyph PackGlyph(PixelBuffer pixels, byte? advance = null)
    {
        byte[] packed = Pack(pixels, out byte left, out byte top, out byte right, out byte bottom);
        byte width = (byte)Math.Clamp(Math.Max((int)right, 1), 1, 255);
        byte adv = advance ?? width;
        if (packed.Length == 0)
        {
            return new LftPackedGlyph(adv, 0, 0, 0, 0, []);
        }

        return new LftPackedGlyph((byte)Math.Max(adv, width), left, top, right, bottom, packed);
    }

    public byte[] ToBytes()
    {
        var glyphs = new LftGlyph[GlyphKeys];
        for (int i = 0; i < GlyphKeys; i++)
        {
            glyphs[i] = i < Glyphs.Count ? Glyphs[i] : default;
        }

        var chunks = new byte[GlyphKeys][];
        return Rebuild(NominalWidth, NominalHeight, glyphs, CopyBitmaps(chunks, glyphs)).Write();
    }

    private byte[][] CopyBitmaps(byte[][] overlay, LftGlyph[] glyphs)
    {
        var chunks = new byte[GlyphKeys][];
        for (int i = 0; i < GlyphKeys; i++)
        {
            if (overlay[i] is not null)
            {
                chunks[i] = overlay[i];
                continue;
            }

            LftGlyph glyph = glyphs[i];
            if (!glyph.HasBitmap)
            {
                chunks[i] = [];
                continue;
            }

            int len = glyph.RowStride * glyph.PixelHeight;
            if (glyph.BitmapOffset + len > Bitmap.Length)
            {
                chunks[i] = [];
                continue;
            }

            chunks[i] = Bitmap.AsSpan((int)glyph.BitmapOffset, len).ToArray();
        }

        return chunks;
    }

    private static LftFile Rebuild(ushort nominalWidth, ushort nominalHeight, LftGlyph[] glyphs, byte[][] chunks)
    {
        int total = 0;
        for (int i = 0; i < GlyphKeys; i++)
        {
            total += chunks[i]?.Length ?? 0;
        }

        // Offset 0 means "no bitmap" to the client, so ink starts at byte 1.
        int cursor = total > 0 ? 1 : 0;
        var bitmap = new byte[total + cursor];
        var rebuilt = new LftGlyph[GlyphKeys];
        for (int i = 0; i < GlyphKeys; i++)
        {
            byte[] chunk = chunks[i] ?? [];
            LftGlyph src = glyphs[i];
            uint offset = chunk.Length == 0 ? 0 : (uint)cursor;
            rebuilt[i] = new LftGlyph(
                src.Advance,
                src.Left,
                src.Top,
                src.Right,
                src.Bottom,
                (ushort)chunk.Length,
                offset);
            if (chunk.Length > 0)
            {
                Buffer.BlockCopy(chunk, 0, bitmap, cursor, chunk.Length);
                cursor += chunk.Length;
            }
        }

        return new LftFile(nominalWidth, nominalHeight, rebuilt, bitmap);
    }

    private byte[] Write()
    {
        var data = new byte[BitmapBase + Bitmap.Length];
        BinaryPrimitivesWriteU16(data, 0, NominalWidth);
        BinaryPrimitivesWriteU16(data, 2, NominalHeight);
        int pos = HeaderSize;
        for (int i = 0; i < GlyphKeys; i++)
        {
            LftGlyph g = i < Glyphs.Count ? Glyphs[i] : default;
            data[pos] = g.Advance;
            data[pos + 1] = g.Left;
            data[pos + 2] = g.Top;
            data[pos + 3] = g.Right;
            data[pos + 4] = g.Bottom;
            BinaryPrimitivesWriteU16(data, pos + 5, g.PackedSize);
            BinaryPrimitivesWriteU32(data, pos + 7, g.BitmapOffset);
            pos += RecordSize;
        }

        if (Bitmap.Length > 0)
        {
            Buffer.BlockCopy(Bitmap, 0, data, BitmapBase, Bitmap.Length);
        }

        return data;
    }

    private static uint NormalizeOffset(uint raw) =>
        raw >= BitmapBase ? raw - (uint)BitmapBase : raw;

    private static void BinaryPrimitivesWriteU16(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
    }

    private static void BinaryPrimitivesWriteU32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }

    public static byte[] Pack(PixelBuffer pixels, out byte left, out byte top, out byte right, out byte bottom)
    {
        int minX = pixels.Width;
        int minY = pixels.Height;
        int maxX = -1;
        int maxY = -1;
        for (int y = 0; y < pixels.Height; y++)
        {
            for (int x = 0; x < pixels.Width; x++)
            {
                int p = (y * pixels.Width + x) * 4;
                bool on = pixels.Bgra[p + 3] >= 16
                    && pixels.Bgra[p] + pixels.Bgra[p + 1] + pixels.Bgra[p + 2] >= 80;
                if (!on)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        if (maxX < minX)
        {
            left = 0;
            top = 0;
            right = 0;
            bottom = 0;
            return [];
        }

        left = (byte)Math.Clamp(minX, 0, 255);
        top = (byte)Math.Clamp(minY, 0, 255);
        right = (byte)Math.Clamp(maxX + 1, 0, 255);
        bottom = (byte)Math.Clamp(maxY + 1, 0, 255);
        int width = right - left;
        int height = bottom - top;
        int stride = ((width + 31) / 32) * 4;
        var data = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int sx = left + x;
                int sy = top + y;
                int p = (sy * pixels.Width + sx) * 4;
                bool on = pixels.Bgra[p + 3] >= 16
                    && pixels.Bgra[p] + pixels.Bgra[p + 1] + pixels.Bgra[p + 2] >= 80;
                if (!on)
                {
                    continue;
                }

                data[y * stride + x / 8] |= (byte)(0x80 >> (x & 7));
            }
        }

        return data;
    }
}
