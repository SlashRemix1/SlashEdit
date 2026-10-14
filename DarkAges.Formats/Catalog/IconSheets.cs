using DarkAges.Formats.Data;
using DarkAges.Formats.Drawing;

namespace DarkAges.Formats.Catalog;

public static class IconSheets
{
    public const int ItemTilesPerFile = 266;

    public static IEnumerable<ArchiveEntry> Expand(IEnumerable<ArchiveEntry> entries)
    {
        var list = entries.ToList();
        Dictionary<string, IReadOnlyList<ArchiveEntry>> skills = ExpandNumbered(list, "skill");
        Dictionary<string, IReadOnlyList<ArchiveEntry>> spells = ExpandNumbered(list, "spell");

        foreach (ArchiveEntry entry in list)
        {
            if (LegendItems.IsItemSheet(entry))
            {
                foreach (ArchiveEntry tile in LegendItems.ExpandSheet(entry))
                {
                    yield return tile;
                }

                continue;
            }

            if (skills.TryGetValue(entry.Name, out IReadOnlyList<ArchiveEntry>? skillTiles))
            {
                foreach (ArchiveEntry tile in skillTiles)
                {
                    yield return tile;
                }

                continue;
            }

            if (spells.TryGetValue(entry.Name, out IReadOnlyList<ArchiveEntry>? spellTiles))
            {
                foreach (ArchiveEntry tile in spellTiles)
                {
                    yield return tile;
                }

                continue;
            }

            yield return entry;
        }
    }

    public static bool IsSkillSheet(ArchiveEntry entry) => IsNumberedSheet(entry, "skill");

    public static bool IsSpellSheet(ArchiveEntry entry) => IsNumberedSheet(entry, "spell");

    private static bool IsNumberedSheet(ArchiveEntry entry, string prefix)
    {
        if (entry.FrameIndex is not null || !entry.Extension.Equals(".epf", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string stem = Path.GetFileNameWithoutExtension(entry.Name);
        return stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && stem.Length > prefix.Length
            && stem.Skip(prefix.Length).All(char.IsDigit);
    }

    private static Dictionary<string, IReadOnlyList<ArchiveEntry>> ExpandNumbered(IReadOnlyList<ArchiveEntry> entries, string prefix)
    {
        var sheets = entries
            .Where(e => IsNumberedSheet(e, prefix))
            .OrderBy(e => FileNumber(e))
            .ToList();
        var map = new Dictionary<string, IReadOnlyList<ArchiveEntry>>(StringComparer.OrdinalIgnoreCase);
        int nextId = 1;
        foreach (ArchiveEntry sheet in sheets)
        {
            IReadOnlyList<ArchiveEntry> tiles = ExpandSheet(sheet, prefix, ref nextId);
            map[sheet.Name] = tiles;
        }

        return map;
    }

    private static IReadOnlyList<ArchiveEntry> ExpandSheet(ArchiveEntry entry, string prefix, ref int nextId)
    {
        try
        {
            IReadOnlyList<EpfFrameHeader> frames = EpfFile.PeekFrames(entry);
            if (frames.Count <= 1)
            {
                return [entry];
            }

            var tiles = new List<ArchiveEntry>(frames.Count);
            for (int i = 0; i < frames.Count; i++)
            {
                if (frames[i].IsEmpty)
                {
                    continue;
                }

                int id = nextId++;
                tiles.Add(entry.WithFrame(i, $"{prefix} {id:D4}"));
            }

            return tiles.Count == 0 ? [entry] : tiles;
        }
        catch
        {
            return [entry];
        }
    }

    private static int FileNumber(ArchiveEntry entry)
    {
        string stem = Path.GetFileNameWithoutExtension(entry.Name);
        var digits = new string(stem.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out int n) ? n : 0;
    }
}
