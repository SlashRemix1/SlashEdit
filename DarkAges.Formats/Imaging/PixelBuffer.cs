using System.IO;

namespace DarkAges.Formats.Imaging;

public sealed class PixelBuffer
{
    public PixelBuffer(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Bgra = new byte[Width * Height * 4];
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }

    public void ReadBgraFrom(Stream stream, long byteOffset)
    {
        stream.Seek(byteOffset, SeekOrigin.Begin);
        stream.ReadExactly(Bgra, 0, Bgra.Length);
    }

    public static PixelBuffer FromRgba(int width, int height, ReadOnlySpan<byte> rgba)
    {
        var buffer = new PixelBuffer(width, height);
        int pixels = Math.Min(buffer.Bgra.Length / 4, rgba.Length / 4);
        for (int i = 0; i < pixels; i++)
        {
            int s = i * 4;
            int d = i * 4;
            buffer.Bgra[d] = rgba[s + 2];
            buffer.Bgra[d + 1] = rgba[s + 1];
            buffer.Bgra[d + 2] = rgba[s];
            buffer.Bgra[d + 3] = rgba[s + 3];
        }

        return buffer;
    }

    public bool HasVisiblePixels()
    {
        for (int i = 3; i < Bgra.Length; i += 4)
        {
            if (Bgra[i] != 0)
            {
                return true;
            }
        }

        return false;
    }

    public PixelBuffer CropVisible(int pad, out int originX, out int originY)
    {
        int minX = Width;
        int minY = Height;
        int maxX = -1;
        int maxY = -1;
        for (int y = 0; y < Height; y++)
        {
            int row = y * Width;
            for (int x = 0; x < Width; x++)
            {
                if (Bgra[(row + x) * 4 + 3] == 0)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }

        if (maxX < minX)
        {
            originX = 0;
            originY = 0;
            return this;
        }

        pad = Math.Max(0, pad);
        minX = Math.Max(0, minX - pad);
        minY = Math.Max(0, minY - pad);
        maxX = Math.Min(Width - 1, maxX + pad);
        maxY = Math.Min(Height - 1, maxY + pad);
        originX = minX;
        originY = minY;
        int cw = maxX - minX + 1;
        int ch = maxY - minY + 1;
        if (cw == Width && ch == Height)
        {
            return this;
        }

        var dest = new PixelBuffer(cw, ch);
        for (int y = 0; y < ch; y++)
        {
            Buffer.BlockCopy(Bgra, ((minY + y) * Width + minX) * 4, dest.Bgra, y * cw * 4, cw * 4);
        }

        return dest;
    }

    public void Clear(byte b = 0, byte g = 0, byte r = 0, byte a = 0)
    {
        for (int i = 0; i < Bgra.Length; i += 4)
        {
            Bgra[i] = b;
            Bgra[i + 1] = g;
            Bgra[i + 2] = r;
            Bgra[i + 3] = a;
        }
    }

    public void FillRect(int left, int top, int width, int height, byte b, byte g, byte r, byte a)
    {
        int x0 = Math.Max(0, left);
        int y0 = Math.Max(0, top);
        int x1 = Math.Min(Width, left + width);
        int y1 = Math.Min(Height, top + height);
        for (int y = y0; y < y1; y++)
        {
            int row = y * Width;
            for (int x = x0; x < x1; x++)
            {
                int i = (row + x) * 4;
                Bgra[i] = b;
                Bgra[i + 1] = g;
                Bgra[i + 2] = r;
                Bgra[i + 3] = a;
            }
        }
    }

    public void FillDiamond(int left, int top, int width, int height, byte b, byte g, byte r, byte a)
    {
        if (width < 2 || height < 2 || a == 0)
        {
            return;
        }

        int cx = left + width / 2;
        int midY = top + height / 2;
        int botY = top + height - 1;
        int right = left + width;
        int inv = 255 - a;
        for (int y = top; y <= botY; y++)
        {
            if ((uint)y >= (uint)Height)
            {
                continue;
            }

            int x0;
            int x1;
            if (y <= midY)
            {
                int span = Math.Max(1, midY - top);
                int t = y - top;
                x0 = cx + (left - cx) * t / span;
                x1 = cx + (right - cx) * t / span;
            }
            else
            {
                int span = Math.Max(1, botY - midY);
                int t = y - midY;
                x0 = left + (cx - left) * t / span;
                x1 = right + (cx - right) * t / span;
            }

            if (x0 > x1)
            {
                (x0, x1) = (x1, x0);
            }

            x0 = Math.Max(0, x0);
            x1 = Math.Min(Width - 1, x1);
            int row = y * Width;
            for (int x = x0; x <= x1; x++)
            {
                int i = (row + x) * 4;
                Bgra[i] = (byte)((Bgra[i] * inv + b * a) / 255);
                Bgra[i + 1] = (byte)((Bgra[i + 1] * inv + g * a) / 255);
                Bgra[i + 2] = (byte)((Bgra[i + 2] * inv + r * a) / 255);
                Bgra[i + 3] = 255;
            }
        }
    }

    public PixelBuffer Clone()
    {
        var copy = new PixelBuffer(Width, Height);
        Buffer.BlockCopy(Bgra, 0, copy.Bgra, 0, Bgra.Length);
        return copy;
    }

    public (byte B, byte G, byte R, byte A) GetPixel(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            return (0, 0, 0, 0);
        }

        int i = (y * Width + x) * 4;
        return (Bgra[i], Bgra[i + 1], Bgra[i + 2], Bgra[i + 3]);
    }

    public void SetPixel(int x, int y, byte b, byte g, byte r, byte a)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            return;
        }

        int i = (y * Width + x) * 4;
        Bgra[i] = b;
        Bgra[i + 1] = g;
        Bgra[i + 2] = r;
        Bgra[i + 3] = a;
    }

    public void Blit(PixelBuffer source, int destX, int destY)
    {
        int srcX0 = destX < 0 ? -destX : 0;
        int srcY0 = destY < 0 ? -destY : 0;
        int srcX1 = Math.Min(source.Width, Width - destX);
        int srcY1 = Math.Min(source.Height, Height - destY);
        if (srcX1 <= srcX0 || srcY1 <= srcY0)
        {
            return;
        }

        byte[] src = source.Bgra;
        byte[] dst = Bgra;
        int sw = source.Width;
        int dw = Width;
        for (int sy = srcY0; sy < srcY1; sy++)
        {
            int siRow = sy * sw;
            int diRow = (destY + sy) * dw + destX;
            for (int sx = srcX0; sx < srcX1; sx++)
            {
                int si = (siRow + sx) * 4;
                byte a = src[si + 3];
                if (a == 0)
                {
                    continue;
                }

                int di = (diRow + sx) * 4;
                if (a == 255)
                {
                    dst[di] = src[si];
                    dst[di + 1] = src[si + 1];
                    dst[di + 2] = src[si + 2];
                    dst[di + 3] = 255;
                }
                else
                {
                    int inv = 255 - a;
                    dst[di] = (byte)((src[si] * a + dst[di] * inv) / 255);
                    dst[di + 1] = (byte)((src[si + 1] * a + dst[di + 1] * inv) / 255);
                    dst[di + 2] = (byte)((src[si + 2] * a + dst[di + 2] * inv) / 255);
                    dst[di + 3] = (byte)Math.Min(255, dst[di + 3] + a);
                }
            }
        }
    }

    /// <summary>Capricorn MapEditor transparent walls: dest += source, clamp 255.</summary>
    public void BlitAdd(PixelBuffer source, int destX, int destY)
    {
        int srcX0 = destX < 0 ? -destX : 0;
        int srcY0 = destY < 0 ? -destY : 0;
        int srcX1 = Math.Min(source.Width, Width - destX);
        int srcY1 = Math.Min(source.Height, Height - destY);
        if (srcX1 <= srcX0 || srcY1 <= srcY0)
        {
            return;
        }

        byte[] src = source.Bgra;
        byte[] dst = Bgra;
        int sw = source.Width;
        int dw = Width;
        for (int sy = srcY0; sy < srcY1; sy++)
        {
            int siRow = sy * sw;
            int diRow = (destY + sy) * dw + destX;
            for (int sx = srcX0; sx < srcX1; sx++)
            {
                int si = (siRow + sx) * 4;
                if (src[si + 3] == 0)
                {
                    continue;
                }

                int di = (diRow + sx) * 4;
                dst[di] = (byte)Math.Min(255, dst[di] + src[si]);
                dst[di + 1] = (byte)Math.Min(255, dst[di + 1] + src[si + 1]);
                dst[di + 2] = (byte)Math.Min(255, dst[di + 2] + src[si + 2]);
                dst[di + 3] = 255;
            }
        }
    }

    public PixelBuffer Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (Width == width && Height == height)
        {
            return this;
        }

        var dest = new PixelBuffer(width, height);
        for (int y = 0; y < height; y++)
        {
            int sy = Math.Min(Height - 1, y * Height / height);
            for (int x = 0; x < width; x++)
            {
                int sx = Math.Min(Width - 1, x * Width / width);
                int si = (sy * Width + sx) * 4;
                int di = (y * width + x) * 4;
                dest.Bgra[di] = Bgra[si];
                dest.Bgra[di + 1] = Bgra[si + 1];
                dest.Bgra[di + 2] = Bgra[si + 2];
                dest.Bgra[di + 3] = Bgra[si + 3];
            }
        }

        return dest;
    }

    public PixelBuffer Fit(int maxWidth, int maxHeight)
    {
        maxWidth = Math.Max(1, maxWidth);
        maxHeight = Math.Max(1, maxHeight);
        if (Width <= maxWidth && Height <= maxHeight)
        {
            return this;
        }

        return ScaleTo(maxWidth, maxHeight);
    }

    /// <summary>Scale up or down so the image fills as much of the box as possible without cropping.</summary>
    public PixelBuffer Contain(int maxWidth, int maxHeight)
    {
        maxWidth = Math.Max(1, maxWidth);
        maxHeight = Math.Max(1, maxHeight);
        if (Width == maxWidth && Height == maxHeight)
        {
            return this;
        }

        return ScaleTo(maxWidth, maxHeight);
    }

    private PixelBuffer ScaleTo(int maxWidth, int maxHeight)
    {
        double scale = Math.Min(maxWidth / (double)Math.Max(1, Width), maxHeight / (double)Math.Max(1, Height));
        int nw = Math.Max(1, (int)Math.Round(Width * scale));
        int nh = Math.Max(1, (int)Math.Round(Height * scale));
        if (nw == Width && nh == Height)
        {
            return this;
        }

        return Resize(nw, nh);
    }

    public PixelBuffer ScaleNearest(int factor)
    {
        factor = Math.Max(1, factor);
        var dest = new PixelBuffer(Width * factor, Height * factor);
        for (int y = 0; y < dest.Height; y++)
        {
            int sy = y / factor;
            for (int x = 0; x < dest.Width; x++)
            {
                int sx = x / factor;
                int si = (sy * Width + sx) * 4;
                int di = (y * dest.Width + x) * 4;
                dest.Bgra[di] = Bgra[si];
                dest.Bgra[di + 1] = Bgra[si + 1];
                dest.Bgra[di + 2] = Bgra[si + 2];
                dest.Bgra[di + 3] = Bgra[si + 3];
            }
        }

        return dest;
    }
}
