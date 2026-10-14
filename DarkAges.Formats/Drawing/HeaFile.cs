using DarkAges.Formats.Data;
using DarkAges.Formats.Imaging;
using DarkAges.Formats.IO;

namespace DarkAges.Formats.Drawing;

/// <summary>
/// Fragments of Temuair custom 640×480 artwork container in seo.dat.
/// Header is readable; pixel payload is a private packed atlas.
/// </summary>
public sealed class HeaFile
{
    public HeaFile(Stream stream) => Init(stream);
    public HeaFile(ArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        Init(stream);
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int DestWidth { get; private set; }
    public int DestHeight { get; private set; }
    public int TileWidth { get; private set; }
    public int TileHeight { get; private set; }
    public int AtlasWidth { get; private set; }
    public int AtlasHeight { get; private set; }
    public int LayerCount { get; private set; }
    public int ByteLength { get; private set; }
    public byte[] Payload { get; private set; } = [];
    public IReadOnlyList<uint> BandStarts { get; private set; } = [];
    public IReadOnlyList<uint> RowOffsets { get; private set; } = [];
    public bool IsMask { get; private set; }
    public bool IsValid => Width > 0 && Height > 0;

    public static bool LooksLike(byte[] data)
    {
        if (data.Length < 40)
        {
            return false;
        }

        int w = BitConverter.ToInt32(data, 4);
        int h = BitConverter.ToInt32(data, 8);
        return w is >= 16 and <= 4096 && h is >= 16 and <= 4096;
    }

    private void Init(Stream stream)
    {
        using BinaryReader reader = BinaryReaderExtensions.Create(stream);
        stream.Seek(0, SeekOrigin.Begin);
        ByteLength = (int)Math.Min(int.MaxValue, stream.Length);
        if (stream.Length < 40)
        {
            return;
        }

        reader.ReadInt32();
        Width = reader.ReadInt32();
        Height = reader.ReadInt32();
        DestWidth = reader.ReadInt32();
        DestHeight = reader.ReadInt32();
        TileWidth = reader.ReadInt32();
        TileHeight = reader.ReadInt32();
        AtlasWidth = reader.ReadInt32();
        AtlasHeight = reader.ReadInt32();
        LayerCount = reader.ReadInt32();
        if (Width is < 1 or > 4096 || Height is < 1 or > 4096)
        {
            Width = 0;
            Height = 0;
        }

        int remain = (int)Math.Max(0, stream.Length - stream.Position);
        Payload = remain > 0 ? reader.ReadBytes(remain) : [];
        ParseMask();
    }

    public PixelBuffer? TryRender(Palette? palette = null)
    {
        if (!IsValid || Payload.Length == 0)
        {
            return null;
        }

        int dw = Math.Max(1, DestWidth > 0 ? DestWidth : Width);
        int dh = Math.Max(1, DestHeight > 0 ? DestHeight : Height);
        long pixels = (long)dw * dh;
        if (Payload.Length == pixels * 2)
        {
            return DecodeRgb565(Payload, dw, dh);
        }

        if (Payload.Length == pixels)
        {
            return DecodeIndexed(Payload, dw, dh, palette);
        }

        int aw = Math.Max(1, AtlasWidth > 0 ? AtlasWidth : Width);
        int ah = Math.Max(1, AtlasHeight > 0 ? AtlasHeight : Height);
        long atlas = (long)aw * ah;
        if (atlas != pixels && Payload.Length == atlas * 2)
        {
            return DecodeRgb565(Payload, aw, ah);
        }

        if (atlas != pixels && Payload.Length == atlas)
        {
            return DecodeIndexed(Payload, aw, ah, palette);
        }

        return IsMask ? DecodeMask() : null;
    }

    private static PixelBuffer DecodeRgb565(byte[] payload, int width, int height)
    {
        var buffer = new PixelBuffer(width, height);
        int pixels = width * height;
        for (int i = 0; i < pixels; i++)
        {
            ushort packed = (ushort)(payload[i * 2] | (payload[i * 2 + 1] << 8));
            if (packed == 0)
            {
                continue;
            }

            int r = ((packed >> 11) & 0x1F) * 255 / 31;
            int g = ((packed >> 5) & 0x3F) * 255 / 63;
            int b = (packed & 0x1F) * 255 / 31;
            buffer.SetPixel(i % width, i / width, (byte)b, (byte)g, (byte)r, 255);
        }

        return buffer;
    }

    private static PixelBuffer DecodeIndexed(byte[] payload, int width, int height, Palette? palette)
    {
        Palette pal = palette ?? GrayRamp();
        return pal.Render(payload, width, height);
    }

    private void ParseMask()
    {
        if (LayerCount is < 1 or > 64
            || AtlasWidth is < 8 or > 16000
            || AtlasHeight is < 8 or > 8000
            || TileWidth is < 1 or > 512
            || TileHeight is < 1 or > 512)
        {
            return;
        }

        int dw = Math.Max(1, DestWidth > 0 ? DestWidth : Width);
        int dh = Math.Max(1, DestHeight > 0 ? DestHeight : Height);
        long artwork = (long)dw * dh;
        long atlas = (long)AtlasWidth * AtlasHeight;
        if (Payload.Length == artwork
            || Payload.Length == artwork * 2
            || Payload.Length == atlas
            || Payload.Length == atlas * 2)
        {
            return;
        }

        using var stream = new MemoryStream(Payload, writable: false);
        using var reader = new BinaryReader(stream);
        long table = (long)LayerCount * 4 + (long)LayerCount * AtlasHeight * 4;
        if (table >= Payload.Length || table < 0)
        {
            return;
        }

        var starts = new uint[LayerCount];
        for (int i = 0; i < LayerCount; i++)
        {
            starts[i] = reader.ReadUInt32();
        }

        var rows = new uint[LayerCount * AtlasHeight];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = reader.ReadUInt32();
        }

        BandStarts = starts;
        RowOffsets = rows;
        IsMask = true;
    }

    public PixelBuffer DecodeMask()
    {
        int width = Math.Max(1, AtlasWidth);
        int height = Math.Max(1, AtlasHeight);
        var dest = new PixelBuffer(width, height);
        if (!IsMask || RowOffsets.Count < height)
        {
            return dest;
        }

        using var stream = new MemoryStream(Payload, writable: false);
        long table = (long)LayerCount * 4 + (long)LayerCount * height * 4;
        int bands = Math.Max(1, LayerCount);
        for (int y = 0; y < height; y++)
        {
            for (int band = 0; band < bands; band++)
            {
                int index = band * height + y;
                if (index >= RowOffsets.Count)
                {
                    continue;
                }

                uint word = RowOffsets[index];
                long pos = table + word * 2L;
                if (pos < table || pos >= Payload.Length)
                {
                    continue;
                }

                stream.Position = pos;
                int x = band < BandStarts.Count ? (int)BandStarts[band] : 0;
                int end = band + 1 < BandStarts.Count ? (int)BandStarts[band + 1] : width;
                end = Math.Min(end, width);
                while (x < end && stream.Position + 2 <= stream.Length)
                {
                    int flags = stream.ReadByte();
                    int length = stream.ReadByte();
                    if (flags < 0 || length < 0)
                    {
                        break;
                    }

                    if (length == 0)
                    {
                        continue;
                    }

                    byte intensity = (byte)(flags & 0x3F);
                    byte gray = (byte)Math.Min(255, intensity * 4);
                    byte alpha = intensity == 0 ? (byte)0 : (byte)255;
                    for (int i = 0; i < length && x < end; i++, x++)
                    {
                        dest.SetPixel(x, y, gray, gray, gray, alpha);
                    }
                }
            }
        }

        return dest;
    }

    private static Palette GrayRamp()
    {
        var gray = new Palette();
        for (int i = 0; i < 256; i++)
        {
            gray.Set(i, (byte)i, (byte)i, (byte)i);
        }

        return gray;
    }
}
