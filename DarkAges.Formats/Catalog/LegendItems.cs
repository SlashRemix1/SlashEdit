using DarkAges.Formats.Data;
using DarkAges.Formats.Drawing;

namespace DarkAges.Formats.Catalog;

public static class LegendItems
{
    /// <summary>Legend/7.xx sheets are often 266 tiles. Never use this to number DarkAges.dat — walk each EPF's real frame count.</summary>
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

    public static int ItemId(IArchive archive, int fileNumber, int frameIndex) =>
        ItemId(archive.Entries, fileNumber, frameIndex);

    public static int ItemId(IEnumerable<ArchiveEntry> entries, int fileNumber, int frameIndex)
    {
        int before = 0;
        foreach (ArchiveEntry sheet in ItemSheets(entries))
        {
            if (FileNumber(sheet) >= fileNumber)
            {
                break;
            }

            before += Math.Max(0, EpfFile.PeekFrameCount(sheet));
        }

        return Math.Max(1, before + frameIndex + 1);
    }

    public static IEnumerable<ArchiveEntry> Expand(IEnumerable<ArchiveEntry> entries)
    {
        var list = entries as IList<ArchiveEntry> ?? entries.ToList();
        Dictionary<string, IReadOnlyList<ArchiveEntry>> sheets = ExpandSheets(list);
        foreach (ArchiveEntry entry in list)
        {
            if (sheets.TryGetValue(entry.Name, out IReadOnlyList<ArchiveEntry>? tiles))
            {
                foreach (ArchiveEntry tile in tiles)
                {
                    yield return tile;
                }

                continue;
            }

            yield return entry;
        }
    }

    public static Dictionary<string, IReadOnlyList<ArchiveEntry>> ExpandSheets(IEnumerable<ArchiveEntry> entries)
    {
        var map = new Dictionary<string, IReadOnlyList<ArchiveEntry>>(StringComparer.OrdinalIgnoreCase);
        int nextId = 1;
        foreach (ArchiveEntry sheet in ItemSheets(entries))
        {
            map[sheet.Name] = ExpandSheet(sheet, ref nextId);
        }

        return map;
    }

    public static IReadOnlyList<ArchiveEntry> ExpandSheet(ArchiveEntry entry) =>
        ExpandSheet(entry, framesBefore: 0);

    public static IReadOnlyList<ArchiveEntry> ExpandSheet(ArchiveEntry entry, int framesBefore)
    {
        int nextId = Math.Max(1, framesBefore + 1);
        return ExpandSheet(entry, ref nextId);
    }

    private static IReadOnlyList<ArchiveEntry> ExpandSheet(ArchiveEntry entry, ref int nextId)
    {
        try
        {
            int frames = EpfFile.PeekFrameCount(entry);
            if (frames <= 1)
            {
                if (frames == 1)
                {
                    nextId++;
                }

                return [entry];
            }

            var tiles = new List<ArchiveEntry>(frames);
            for (int i = 0; i < frames; i++)
            {
                int id = nextId++;
                tiles.Add(entry.WithFrame(i, $"item {id:D4}"));
            }

            return tiles.Count == 0 ? [entry] : tiles;
        }
        catch
        {
            return [entry];
        }
    }

    private static IEnumerable<ArchiveEntry> ItemSheets(IEnumerable<ArchiveEntry> entries) =>
        entries
            .Where(IsItemSheet)
            .OrderBy(FileNumber)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase);
}
