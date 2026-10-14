using System.IO.Compression;

namespace DarkAges.Formats.Imaging;

public static class PngWriter
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static void Write(PixelBuffer buffer, string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
        Write(buffer, stream);
    }

    public static void Write(PixelBuffer buffer, Stream stream)
    {
        stream.Write(Signature);
        Span<byte> ihdr = stackalloc byte[13];
        WriteBe(ihdr, 0, (uint)buffer.Width);
        WriteBe(ihdr, 4, (uint)buffer.Height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        WriteChunk(stream, "IHDR"u8, ihdr);
        WriteChunk(stream, "IDAT"u8, DeflateRgba(buffer));
        WriteChunk(stream, "IEND"u8, []);
    }

    private static byte[] DeflateRgba(PixelBuffer buffer)
    {
        int row = 1 + buffer.Width * 4;
        var raw = new byte[row * buffer.Height];
        byte[] bgra = buffer.Bgra;
        for (int y = 0; y < buffer.Height; y++)
        {
            int dest = y * row;
            raw[dest] = 0;
            int src = y * buffer.Width * 4;
            for (int x = 0; x < buffer.Width; x++)
            {
                int si = src + x * 4;
                int di = dest + 1 + x * 4;
                raw[di] = bgra[si + 2];
                raw[di + 1] = bgra[si + 1];
                raw[di + 2] = bgra[si];
                raw[di + 3] = bgra[si + 3];
            }
        }

        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        WriteBe(len, 0, (uint)data.Length);
        stream.Write(len);
        stream.Write(type);
        stream.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (byte b in type)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        crc ^= 0xFFFFFFFF;
        WriteBe(len, 0, crc);
        stream.Write(len);
    }

    private static void WriteBe(Span<byte> dest, int offset, uint value)
    {
        dest[offset] = (byte)(value >> 24);
        dest[offset + 1] = (byte)(value >> 16);
        dest[offset + 2] = (byte)(value >> 8);
        dest[offset + 3] = (byte)value;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
