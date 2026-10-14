using DarkAges.Formats.Data;
using DarkAges.Formats.Drawing;

namespace DarkAges.Formats.Catalog;

public static class LegendItems
{
    public const int TilesPerFile = 266;

    public static bool IsItemGraphic(ArchiveEntry entry)
    {
        string ext = entry.Extension;
        string stem = Path.GetFileNameWithoutExtension(entry.Name);
        if (ext.Equals(".rgba", StringComparison.OrdinalIgnoreCase)
            && stem.StartsWith("item", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!ext.Equals(".epf", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return stem.Length == 7
            && stem.StartsWith("item", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(stem.AsSpan(4, 3), out _);
    }

    public static bool IsItemSheet(ArchiveEntry entry) =>
        entry.FrameIndex is null
        && entry.Extension.Equals(".epf", StringComparison.OrdinalIgnoreCase)
        && IsItemGraphic(entry);

    public static bool IsRgbaItem(ArchiveEntry entry) =>
        entry.Extension.Equals(".rgba", StringComparison.OrdinalIgnoreCase)
        && Path.GetFileNameWithoutExtension(entry.Name).StartsWith("item", StringComparison.OrdinalIgnoreCase);

    public static int FileNumber(ArchiveEntry entry)
    {
        string stem = Path.GetFileNameWithoutExtension(entry.Name);
        return stem.Length >= 7 && int.TryParse(stem.AsSpan(4, 3), out int n) ? n : 0;
    }

    public static int RgbaItemId(ArchiveEntry entry) => RgbaItemId(entry.Name);

    public static int RgbaItemId(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        if (!stem.StartsWith("item", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var digits = new string(stem.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out int id) ? Math.Max(1, id) : 0;
    }

    public static int ItemId(int fileNumber, int frameIndex) =>
        Math.Max(1, (fileNumber - 1) * TilesPerFile + frameIndex + 1);

    public static IEnumerable<ArchiveEntry> Expand(IEnumerable<ArchiveEntry> entries)
    {
        foreach (ArchiveEntry entry in entries)
        {
            if (!IsItemSheet(entry))
            {
                yield return entry;
                continue;
            }

            IReadOnlyList<ArchiveEntry> tiles = ExpandSheet(entry);
            if (tiles.Count == 0)
            {
                yield return entry;
                continue;
            }

            foreach (ArchiveEntry tile in tiles)
            {
                yield return tile;
            }
        }
    }

    public static IReadOnlyList<ArchiveEntry> ExpandSheet(ArchiveEntry entry)
    {
        try
        {
            int frames = EpfFile.PeekFrameCount(entry);
            if (frames <= 1)
            {
                return [entry];
            }

            int fileNumber = FileNumber(entry);
            var tiles = new List<ArchiveEntry>(frames);
            for (int i = 0; i < frames; i++)
            {
                int id = ItemId(fileNumber, i);
                tiles.Add(entry.WithFrame(i, $"item {id:D4}"));
            }

            return tiles.Count == 0 ? [entry] : tiles;
        }
        catch
        {
            return [entry];
        }
    }
}
