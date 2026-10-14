using DarkAges.Formats.Data;

namespace DarkAges.Formats.Drawing;

/// <summary>
/// Fragments of Temuair wraps a JFIF JPEG in a 4-byte "JPF\0" prefix.
/// Plain .jpg payloads are also accepted.
/// </summary>
public static class JpfFile
{
    public static bool LooksLike(byte[] data) => JpegOffset(data) >= 0;

    public static bool LooksLike(ArchiveEntry entry)
    {
        try
        {
            using Stream stream = entry.Open();
            var head = new byte[8];
            int n = stream.Read(head);
            if (n < 4)
            {
                return false;
            }

            Array.Resize(ref head, n);
            return JpegOffset(head) >= 0;
        }
        catch
        {
            return false;
        }
    }

    public static byte[] JpegBytes(byte[] data)
    {
        int offset = JpegOffset(data);
        if (offset <= 0)
        {
            return data;
        }

        var jpeg = new byte[data.Length - offset];
        Buffer.BlockCopy(data, offset, jpeg, 0, jpeg.Length);
        return jpeg;
    }

    public static int JpegOffset(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return 0;
        }

        if (data.Length >= 7
            && data[0] is (byte)'J' or (byte)'j'
            && data[1] is (byte)'P' or (byte)'p'
            && data[2] is (byte)'F' or (byte)'f'
            && data[4] == 0xFF && data[5] == 0xD8)
        {
            return 4;
        }

        return -1;
    }
}
