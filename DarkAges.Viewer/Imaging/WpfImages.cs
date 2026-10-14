using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DarkAges.Formats.Drawing;
using DarkAges.Formats.Imaging;

namespace DarkAges.Viewer.Imaging;

public static class WpfImages
{
    public static PixelBuffer? TryDecodeImage(byte[] data)
    {
        try
        {
            byte[] payload = JpfFile.LooksLike(data) ? JpfFile.JpegBytes(data) : data;
            using var stream = new MemoryStream(payload, writable: false);
            BitmapDecoder decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                return null;
            }

            var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            var buffer = new PixelBuffer(converted.PixelWidth, converted.PixelHeight);
            converted.CopyPixels(buffer.Bgra, converted.PixelWidth * 4, 0);
            return buffer;
        }
        catch
        {
            return null;
        }
    }

    public static BitmapSource ToBitmap(PixelBuffer buffer)
    {
        var bmp = BitmapSource.Create(
            buffer.Width,
            buffer.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            buffer.Bgra,
            buffer.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    public static WriteableBitmap ToWriteable(PixelBuffer buffer)
    {
        var bmp = new WriteableBitmap(buffer.Width, buffer.Height, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new System.Windows.Int32Rect(0, 0, buffer.Width, buffer.Height), buffer.Bgra, buffer.Width * 4, 0);
        return bmp;
    }

    public static void Update(WriteableBitmap bmp, PixelBuffer buffer, int x, int y, int width, int height)
    {
        int dx = Math.Max(0, x);
        int dy = Math.Max(0, y);
        int w = Math.Min(width, Math.Min(bmp.PixelWidth, buffer.Width) - dx);
        int h = Math.Min(height, Math.Min(bmp.PixelHeight, buffer.Height) - dy);
        if (w <= 0 || h <= 0)
        {
            return;
        }

        bmp.WritePixels(
            new System.Windows.Int32Rect(dx, dy, w, h),
            buffer.Bgra,
            buffer.Width * 4,
            (dy * buffer.Width + dx) * 4);
    }

    public static void UpdateFull(WriteableBitmap bmp, PixelBuffer buffer)
    {
        if (bmp.PixelWidth != buffer.Width || bmp.PixelHeight != buffer.Height)
        {
            return;
        }

        bmp.WritePixels(new System.Windows.Int32Rect(0, 0, buffer.Width, buffer.Height), buffer.Bgra, buffer.Width * 4, 0);
    }

    public static void SavePng(BitmapSource source, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    public static BitmapSource Checkerboard(int width, int height, PixelBuffer? overlay = null)
    {
        var dest = new PixelBuffer(Math.Max(1, width), Math.Max(1, height));
        for (int y = 0; y < dest.Height; y++)
        {
            for (int x = 0; x < dest.Width; x++)
            {
                bool dark = ((x / 8) + (y / 8)) % 2 == 0;
                byte v = dark ? (byte)40 : (byte)55;
                dest.SetPixel(x, y, v, v, v, 255);
            }
        }

        if (overlay is not null)
        {
            dest.Blit(overlay, 0, 0);
        }

        return ToBitmap(dest);
    }
}
