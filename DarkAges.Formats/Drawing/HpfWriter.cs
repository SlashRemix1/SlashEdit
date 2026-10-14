namespace DarkAges.Formats.Drawing;

public static class HpfWriter
{
    public const int TileWidth = 28;

    public static byte[] Write(byte[] header, byte[] indexed, bool compress = true)
    {
        var raw = new byte[8 + indexed.Length];
        int headerCopy = Math.Min(8, header.Length);
        if (headerCopy > 0)
        {
            Buffer.BlockCopy(header, 0, raw, 0, headerCopy);
        }

        if (indexed.Length > 0)
        {
            Buffer.BlockCopy(indexed, 0, raw, 8, indexed.Length);
        }

        return compress ? Compress(raw) : raw;
    }

    public static byte[] Write(HpfFile file, byte[]? indexed = null, bool compress = true) =>
        Write(file.Header, indexed ?? file.Data, compress);

    /// <summary>
    /// Adaptive bit-tree used by the stock client. Magic 0xFF02AA55, LSB-first bits, same rotate as <see cref="HpfFile"/>.
    /// Uncompressed (no magic) is also valid: the client copies those bytes as-is.
    /// </summary>
    public static byte[] Compress(byte[] raw)
    {
        var odd = new uint[256];
        var even = new uint[256];
        var pair = new byte[513];
        for (int i = 0; i < 256; i++)
        {
            odd[i] = (uint)(2 * i + 1);
            even[i] = (uint)(2 * i + 2);
            pair[i * 2 + 1] = (byte)i;
            pair[i * 2 + 2] = (byte)i;
        }

        var bits = new List<byte>(raw.Length * 9 + 16);
        for (int i = 0; i < raw.Length; i++)
        {
            Encode(raw[i]);
        }

        Encode(256);

        var payload = new List<byte>((bits.Count + 7) / 8);
        byte cur = 0;
        int k = 0;
        foreach (byte bit in bits)
        {
            if (bit != 0)
            {
                cur |= (byte)(1 << k);
            }

            k++;
            if (k == 8)
            {
                payload.Add(cur);
                cur = 0;
                k = 0;
            }
        }

        if (k > 0)
        {
            payload.Add(cur);
        }

        var file = new byte[4 + payload.Count];
        file[0] = 0x55;
        file[1] = 0xAA;
        file[2] = 0x02;
        file[3] = 0xFF;
        payload.CopyTo(file, 4);
        return file;

        void Encode(int symbol)
        {
            int node = symbol == 256 ? 512 : symbol + 256;
            var path = new List<int>(16);
            while (node != 0 && node < pair.Length)
            {
                int parent = pair[node];
                if (parent < 0 || parent >= odd.Length)
                {
                    break;
                }

                path.Add(even[parent] == (uint)node ? 1 : 0);
                node = parent;
            }

            path.Reverse();
            foreach (int bit in path)
            {
                bits.Add((byte)bit);
            }

            Rotate((uint)(symbol == 256 ? 512 : symbol + 256));
        }

        void Rotate(uint val)
        {
            uint val3 = val;
            if (val3 >= pair.Length)
            {
                return;
            }

            uint val2 = pair[val3];
            while (val3 != 0 && val2 != 0)
            {
                if (val2 >= pair.Length)
                {
                    return;
                }

                uint i = pair[val2];
                if (i >= odd.Length)
                {
                    return;
                }

                uint j = odd[i];
                if (j == val2)
                {
                    j = even[i];
                    even[i] = val3;
                }
                else
                {
                    odd[i] = val3;
                }

                if (odd[val2] == val3)
                {
                    odd[val2] = j;
                }
                else
                {
                    even[val2] = j;
                }

                pair[val3] = (byte)i;
                if (j < pair.Length)
                {
                    pair[j] = (byte)val2;
                }

                val3 = i;
                val2 = pair[val3];
            }
        }
    }
}
