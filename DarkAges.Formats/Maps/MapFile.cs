namespace DarkAges.Formats.Maps;

public sealed class MapFile
{
    public const int MaxDimension = 1024;

    public MapFile(int width, int height, string? name = null, MapSizeHeader sizeHeader = MapSizeHeader.None)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Name = name ?? "";
        SizeHeader = sizeHeader;
        Tiles = new MapTile[Width * Height];
        for (int i = 0; i < Tiles.Length; i++)
        {
            Tiles[i] = new MapTile();
        }
    }

    public string Name { get; set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public MapSizeHeader SizeHeader { get; set; }
    public MapTile[] Tiles { get; private set; }

    public MapTile this[int x, int y]
    {
        get => Tiles[y * Width + x];
        set => Tiles[y * Width + x] = value;
    }

    public static MapFile Load(string path)
    {
        using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);
        var data = new byte[fs.Length];
        int read = 0;
        while (read < data.Length)
        {
            int n = fs.Read(data, read, data.Length - read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }
        var map = FromBytes(data);
        map.Name = path;
        return map;
    }

    public static MapFile FromBytes(byte[] data)
    {
        if (TryReadEmbeddedSize(data, out int width, out int height, out int offset, out MapSizeHeader header))
        {
            MapTile[] tiles = ReadTiles(data, offset, width * height);
            return new MapFile(width, height, sizeHeader: header) { Tiles = tiles };
        }

        int count = data.Length / 6;
        MapTile[] raw = ReadTiles(data, 0, count);
        (int guessedWidth, int guessedHeight) = GuessSize(count);
        return new MapFile(guessedWidth, guessedHeight) { Tiles = raw };
    }

    public static bool TryPeekSize(byte[] data, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (data is null || data.Length < 6)
        {
            return false;
        }

        if (TryReadEmbeddedSize(data, out width, out height, out _, out _))
        {
            return width > 0 && height > 0;
        }

        int count = data.Length / 6;
        (width, height) = GuessSize(count);
        return width > 0 && height > 0;
    }

    public void LoadState(int width, int height, MapTile[] tiles, MapSizeHeader header)
    {
        if (width <= 0 || height <= 0 || tiles.Length != width * height)
        {
            throw new ArgumentException("Width × height must equal the tile count.");
        }

        Width = width;
        Height = height;
        Tiles = tiles;
        SizeHeader = header;
    }

    public void SetSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || width * height != Tiles.Length)
        {
            throw new ArgumentException("Width × height must equal the tile count.");
        }

        Width = width;
        Height = height;
    }

    public void Resize(int width, int height, MapTile fill = default)
    {
        width = Math.Max(1, Math.Min(MaxDimension, width));
        height = Math.Max(1, Math.Min(MaxDimension, height));
        if (width == Width && height == Height)
        {
            return;
        }

        var next = new MapTile[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                next[y * width + x] = x < Width && y < Height ? Tiles[y * Width + x] : fill;
            }
        }

        Width = width;
        Height = height;
        Tiles = next;
    }

    /// <summary>
    /// Adds tiles on a side when the value is positive, or removes them when negative.
    /// Content keeps its relative position: positive left/top shift existing tiles right/down.
    /// </summary>
    public void Pad(int left, int top, int right, int bottom, MapTile fill = default)
    {
        if (!TryPadSize(Width, Height, left, top, right, bottom, out int width, out int height))
        {
            throw new ArgumentOutOfRangeException(
                nameof(left),
                $"Resulting size {Width + left + right}×{Height + top + bottom} is outside 1–{MaxDimension}.");
        }

        if (left == 0 && top == 0 && right == 0 && bottom == 0)
        {
            return;
        }

        var next = new MapTile[width * height];
        for (int i = 0; i < next.Length; i++)
        {
            next[i] = fill;
        }

        for (int y = 0; y < Height; y++)
        {
            int dy = y + top;
            if ((uint)dy >= (uint)height)
            {
                continue;
            }

            for (int x = 0; x < Width; x++)
            {
                int dx = x + left;
                if ((uint)dx >= (uint)width)
                {
                    continue;
                }

                next[dy * width + dx] = Tiles[y * Width + x];
            }
        }

        Width = width;
        Height = height;
        Tiles = next;
    }

    public static bool TryPadSize(
        int width,
        int height,
        int left,
        int top,
        int right,
        int bottom,
        out int newWidth,
        out int newHeight)
    {
        long w = (long)width + left + right;
        long h = (long)height + top + bottom;
        newWidth = w is >= 1 and <= MaxDimension ? (int)w : 0;
        newHeight = h is >= 1 and <= MaxDimension ? (int)h : 0;
        return newWidth > 0 && newHeight > 0;
    }

    public void Save(string path)
    {
        File.WriteAllBytes(path, ToBytes());
        Name = path;
    }

    public byte[] ToBytes()
    {
        int headerBytes = SizeHeader switch
        {
            MapSizeHeader.UInt16 => 4,
            MapSizeHeader.UInt32 => 8,
            _ => 0
        };
        var data = new byte[headerBytes + Tiles.Length * 6];
        if (SizeHeader == MapSizeHeader.UInt16)
        {
            BitConverter.TryWriteBytes(data.AsSpan(0, 2), (ushort)Width);
            BitConverter.TryWriteBytes(data.AsSpan(2, 2), (ushort)Height);
        }
        else if (SizeHeader == MapSizeHeader.UInt32)
        {
            BitConverter.TryWriteBytes(data.AsSpan(0, 4), (uint)Width);
            BitConverter.TryWriteBytes(data.AsSpan(4, 4), (uint)Height);
        }

        for (int i = 0; i < Tiles.Length; i++)
        {
            MapTile tile = Tiles[i];
            int o = headerBytes + i * 6;
            BitConverter.TryWriteBytes(data.AsSpan(o, 2), tile.Floor);
            BitConverter.TryWriteBytes(data.AsSpan(o + 2, 2), tile.LeftWall);
            BitConverter.TryWriteBytes(data.AsSpan(o + 4, 2), tile.RightWall);
        }

        return data;
    }

    public static IReadOnlyList<(int Width, int Height)> SizeOptions(int tileCount)
    {
        var options = new List<(int, int)>();
        double root = Math.Sqrt(tileCount);
        if (Math.Abs(root - Math.Round(root)) < 0.0001)
        {
            int n = (int)Math.Round(root);
            if (n >= 1 && n <= MaxDimension)
            {
                options.Add((n, n));
            }
        }

        for (int w = 2; w < tileCount && w <= MaxDimension; w++)
        {
            if (tileCount % w != 0)
            {
                continue;
            }

            int h = tileCount / w;
            if (h < 2 || h > MaxDimension)
            {
                continue;
            }

            if (options.Exists(o => o.Item1 == w && o.Item2 == h))
            {
                continue;
            }

            options.Add((w, h));
        }

        return options
            .OrderByDescending(o => o.Item1 == o.Item2)
            .ThenBy(o => Math.Abs(o.Item1 - o.Item2))
            .ThenBy(o => o.Item1)
            .ToList();
    }

    private static (int Width, int Height) GuessSize(int count)
    {
        IReadOnlyList<(int Width, int Height)> options = SizeOptions(count);
        return options.Count > 0 ? options[0] : (Math.Max(1, count), 1);
    }

    private static MapTile[] ReadTiles(byte[] data, int offset, int count)
    {
        int available = Math.Max(0, (data.Length - offset) / 6);
        count = Math.Min(count, available);
        var tiles = new MapTile[count];
        for (int i = 0; i < count; i++)
        {
            int o = offset + i * 6;
            tiles[i] = new MapTile(
                BitConverter.ToUInt16(data, o),
                BitConverter.ToUInt16(data, o + 2),
                BitConverter.ToUInt16(data, o + 4));
        }

        return tiles;
    }

    private static bool TryReadEmbeddedSize(
        byte[] data,
        out int width,
        out int height,
        out int offset,
        out MapSizeHeader header)
    {
        width = 0;
        height = 0;
        offset = 0;
        header = MapSizeHeader.None;
        if (data.Length >= 8)
        {
            int w32 = BitConverter.ToInt32(data, 0);
            int h32 = BitConverter.ToInt32(data, 4);
            if (IsPlausibleSize(w32, h32) && 8 + w32 * h32 * 6 == data.Length)
            {
                width = w32;
                height = h32;
                offset = 8;
                header = MapSizeHeader.UInt32;
                return true;
            }
        }

        if (data.Length >= 4)
        {
            int w16 = BitConverter.ToUInt16(data, 0);
            int h16 = BitConverter.ToUInt16(data, 2);
            if (IsPlausibleSize(w16, h16) && 4 + w16 * h16 * 6 == data.Length)
            {
                width = w16;
                height = h16;
                offset = 4;
                header = MapSizeHeader.UInt16;
                return true;
            }
        }

        return false;
    }

    private static bool IsPlausibleSize(int width, int height) =>
        width >= 1 && height >= 1 && width <= MaxDimension && height <= MaxDimension;
}

public enum MapSizeHeader
{
    None,
    UInt16,
    UInt32
}

public struct MapTile
{
    public MapTile(ushort floor, ushort leftWall, ushort rightWall)
    {
        Floor = floor;
        LeftWall = leftWall;
        RightWall = rightWall;
    }

    public ushort Floor { get; set; }
    public ushort LeftWall { get; set; }
    public ushort RightWall { get; set; }
}
