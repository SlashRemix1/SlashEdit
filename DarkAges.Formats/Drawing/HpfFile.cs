using DarkAges.Formats.Data;
using DarkAges.Formats.Imaging;

namespace DarkAges.Formats.Drawing;

public sealed class HpfFile
{
    public HpfFile(Stream stream) => Init(stream);
    public HpfFile(ArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        Init(stream);
    }

    public int Width => 28;
    public int Height => Data.Length / Width;
    public byte[] Header { get; private set; } = new byte[8];
    public byte[] Data { get; private set; } = Array.Empty<byte>();

    public PixelBuffer Render(Palette palette) => palette.Render(Data, Width, Height);

    private void Init(Stream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        byte[] source;
        int offset;
        int length;
        if (stream is MemoryStream ms && ms.TryGetBuffer(out ArraySegment<byte> segment) && segment.Array is not null)
        {
            source = segment.Array;
            offset = segment.Offset;
            length = segment.Count;
        }
        else
        {
            source = new byte[stream.Length];
            int read = 0;
            while (read < source.Length)
            {
                int n = stream.Read(source, read, source.Length - read);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            offset = 0;
            length = read;
        }

        if (length <= 0)
        {
            Data = Array.Empty<byte>();
            return;
        }

        byte[] decompressed;
        if (source[offset] == 0x55)
        {
            byte[] compressed;
            if (offset == 0 && length == source.Length)
            {
                compressed = source;
            }
            else
            {
                compressed = new byte[length];
                Buffer.BlockCopy(source, offset, compressed, 0, length);
            }

            decompressed = Decompress(compressed);
        }
        else
        {
            decompressed = new byte[length];
            Buffer.BlockCopy(source, offset, decompressed, 0, length);
        }

        if (decompressed.Length <= 8)
        {
            Data = Array.Empty<byte>();
            return;
        }

        Header = new byte[8];
        Buffer.BlockCopy(decompressed, 0, Header, 0, 8);
        Data = new byte[decompressed.Length - 8];
        Buffer.BlockCopy(decompressed, 8, Data, 0, Data.Length);
    }

    private static byte[] Decompress(byte[] hpfBytes)
    {
        uint k = 7;
        uint val = 0;
        uint val2 = 0;
        uint val3 = 0;
        uint i = 0;
        uint j = 0;
        uint l = 0;
        uint m = 0;
        int hpfSize = hpfBytes.Length;
        var rawBytes = new byte[Math.Max(hpfSize * 10, 16)];
        var intOdd = new uint[256];
        var intEven = new uint[256];
        var bytePair = new byte[513];

        for (i = 0; i < 256; i++)
        {
            intOdd[i] = 2 * i + 1;
            intEven[i] = 2 * i + 2;
            bytePair[i * 2 + 1] = (byte)i;
            bytePair[i * 2 + 2] = (byte)i;
        }

        while (val != 0x100)
        {
            val = 0;
            while (val <= 0xFF)
            {
                if (k == 7)
                {
                    l++;
                    k = 0;
                }
                else
                {
                    k++;
                }

                int index = (int)(4 + l - 1);
                if (index < 0 || index >= hpfBytes.Length)
                {
                    goto Done;
                }

                if ((hpfBytes[index] & (1 << (int)k)) != 0)
                {
                    if (val >= intEven.Length)
                    {
                        goto Done;
                    }

                    val = intEven[val];
                }
                else
                {
                    if (val >= intOdd.Length)
                    {
                        goto Done;
                    }

                    val = intOdd[val];
                }
            }

            val3 = val;
            if (val >= bytePair.Length)
            {
                break;
            }

            val2 = bytePair[val];
            while (val3 != 0 && val2 != 0)
            {
                if (val2 >= bytePair.Length)
                {
                    goto Done;
                }

                i = bytePair[val2];
                if (i >= intOdd.Length)
                {
                    goto Done;
                }

                j = intOdd[i];
                if (j == val2)
                {
                    j = intEven[i];
                    intEven[i] = val3;
                }
                else
                {
                    intOdd[i] = val3;
                }

                if (intOdd[val2] == val3)
                {
                    intOdd[val2] = j;
                }
                else
                {
                    intEven[val2] = j;
                }

                bytePair[val3] = (byte)i;
                bytePair[j] = (byte)val2;
                val3 = i;
                val2 = bytePair[val3];
            }

            val += 0xFFFFFF00;
            if (val != 0x100)
            {
                if (m >= rawBytes.Length)
                {
                    Array.Resize(ref rawBytes, rawBytes.Length * 2);
                }

                rawBytes[m] = (byte)val;
                m++;
            }
        }

    Done:
        var finalData = new byte[m];
        Buffer.BlockCopy(rawBytes, 0, finalData, 0, (int)m);
        return finalData;
    }
}
