using DarkAges.Formats.Data;

namespace DarkAges.Formats.Catalog;

public static class FloorTiles
{
    public const int TileWidth = 56;
    public const int TileHeight = 27;
    public const int TileSize = TileWidth * TileHeight;

    public static bool IsFloorName(ArchiveEntry entry) => IsFloorName(entry.Name);

    public static bool IsFloorName(string name)
    {
        string file = Path.GetFileName(name);
        return file.Equals("TILEA.BMP", StringComparison.OrdinalIgnoreCase)
            || file.Equals("TILEAS.BMP", StringComparison.OrdinalIgnoreCase)
            || (file.StartsWith("tile_", StringComparison.OrdinalIgnoreCase)
                && file.EndsWith(".rgba", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsAtlas(ArchiveEntry entry) =>
        entry.FrameIndex is null
        && IsFloorName(entry)
        && !entry.Extension.Equals(".rgba", StringComparison.OrdinalIgnoreCase);

    public static int Count(int byteLength) => Math.Max(0, byteLength / TileSize);

    public static IEnumerable<ArchiveEntry> Expand(IEnumerable<ArchiveEntry> entries)
    {
        foreach (ArchiveEntry entry in entries)
        {
            if (!IsAtlas(entry))
            {
                yield return entry;
                continue;
            }

            int tiles = Count(entry.Size);
            if (tiles <= 1)
            {
                yield return entry;
                continue;
            }

            string label = Path.GetFileNameWithoutExtension(entry.Name)
                .Equals("TILEAS", StringComparison.OrdinalIgnoreCase)
                ? "snow floor"
                : "floor";
            for (int i = 0; i < tiles; i++)
            {
                yield return entry.WithFrame(i, $"{label} {i + 1:D5}");
            }
        }
    }

    public static byte[] LoadAtlas(ArchiveEntry entry) => entry.ReadAllBytes();

    public static byte[] ReadTile(byte[] atlas, int index)
    {
        var data = new byte[TileSize];
        long offset = (long)index * TileSize;
        if (offset < 0 || offset + TileSize > atlas.Length)
        {
            return data;
        }

        Buffer.BlockCopy(atlas, (int)offset, data, 0, TileSize);
        return data;
    }

    public static byte[] ReadTile(ArchiveEntry entry, int index)
    {
        using Stream stream = entry.Open();
        long offset = (long)index * TileSize;
        if (offset < 0 || offset + TileSize > stream.Length)
        {
            return new byte[TileSize];
        }

        stream.Seek(offset, SeekOrigin.Begin);
        var data = new byte[TileSize];
        int read = 0;
        while (read < data.Length)
        {
            int n = stream.Read(data, read, data.Length - read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }

        return data;
    }

    public static byte[] WriteTile(byte[] atlas, int index, byte[] tile)
    {
        int count = Math.Max(Count(atlas.Length), Math.Max(0, index) + 1);
        var data = atlas.Length == count * TileSize ? atlas : new byte[count * TileSize];
        if (!ReferenceEquals(data, atlas) && atlas.Length > 0)
        {
            Buffer.BlockCopy(atlas, 0, data, 0, Math.Min(atlas.Length, data.Length));
        }

        byte[] src = tile.Length == TileSize ? tile : PadTile(tile);
        Buffer.BlockCopy(src, 0, data, Math.Max(0, index) * TileSize, TileSize);
        return data;
    }

    public static byte[] BlankTile() => new byte[TileSize];

    private static byte[] PadTile(byte[] tile)
    {
        var data = new byte[TileSize];
        Buffer.BlockCopy(tile, 0, data, 0, Math.Min(TileSize, tile.Length));
        return data;
    }
}
