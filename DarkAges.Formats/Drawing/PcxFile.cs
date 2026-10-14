using DarkAges.Formats.Data;
using DarkAges.Formats.Imaging;

namespace DarkAges.Formats.Drawing;

public sealed class PcxFile
{
    public PcxFile(Stream stream) => Init(stream);
    public PcxFile(ArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        Init(stream);
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public PixelBuffer Image { get; private set; } = new(1, 1);

    private void Init(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        byte[] bytes = ms.ToArray();
        if (bytes.Length < 128 || bytes[0] != 0x0A)
        {
            return;
        }

        int xmin = BitConverter.ToInt16(bytes, 4);
        int ymin = BitConverter.ToInt16(bytes, 6);
        int xmax = BitConverter.ToInt16(bytes, 8);
        int ymax = BitConverter.ToInt16(bytes, 10);
        Width = xmax - xmin + 1;
        Height = ymax - ymin + 1;
        int bitsPerPixel = bytes[3];
        int nPlanes = bytes[65];
        int bytesPerLine = BitConverter.ToInt16(bytes, 66);
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        var palette = new (byte R, byte G, byte B)[256];
        if (bytes.Length >= 769 && bytes[^769] == 0x0C)
        {
            int palOff = bytes.Length - 768;
            for (int i = 0; i < 256; i++)
            {
                palette[i] = (bytes[palOff + i * 3], bytes[palOff + i * 3 + 1], bytes[palOff + i * 3 + 2]);
            }
        }
        else
        {
            for (int i = 0; i < 16; i++)
            {
                palette[i] = (bytes[16 + i * 3], bytes[16 + i * 3 + 1], bytes[16 + i * 3 + 2]);
            }
        }

        var decoded = new byte[nPlanes * bytesPerLine * Height];
        int src = 128;
        int dst = 0;
        int decodedLen = decoded.Length;
        while (dst < decodedLen && src < bytes.Length)
        {
            byte value = bytes[src++];
            int count = 1;
            if ((value & 0xC0) == 0xC0)
            {
                count = value & 0x3F;
                if (src >= bytes.Length)
                {
                    break;
                }

                value = bytes[src++];
            }

            for (int c = 0; c < count && dst < decodedLen; c++)
            {
                decoded[dst++] = value;
            }
        }

        Image = new PixelBuffer(Width, Height);
        if (bitsPerPixel == 8 && nPlanes == 1)
        {
            for (int y = 0; y < Height; y++)
            {
                int row = y * bytesPerLine;
                for (int x = 0; x < Width; x++)
                {
                    byte index = decoded[row + x];
                    var (r, g, b) = palette[index];
                    byte a = index == 0 ? (byte)0 : (byte)255;
                    Image.SetPixel(x, y, b, g, r, a);
                }
            }
        }
        else if (nPlanes == 3 && bitsPerPixel == 8)
        {
            int planeSize = bytesPerLine * Height;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int p = y * bytesPerLine + x;
                    byte r = decoded[p];
                    byte g = decoded[planeSize + p];
                    byte b = decoded[planeSize * 2 + p];
                    Image.SetPixel(x, y, b, g, r, 255);
                }
            }
        }
    }
}
