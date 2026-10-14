using DarkAges.Formats.Data;
using DarkAges.Formats.Drawing;

namespace DarkAges.Formats.Catalog;

public static class PaletteLocator
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Palette> PalCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PaletteTable> TableCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<ArchiveEntry>> PalListCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Palette?> EntryCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Palette?> DefaultCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IArchive> ExtraArchives = new(StringComparer.OrdinalIgnoreCase);

    public static void Invalidate(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        lock (Gate)
        {
            RemoveByPrefix(PalCache, sourcePath);
            RemoveByPrefix(TableCache, sourcePath);
            RemoveByPrefix(PalListCache, sourcePath);
            RemoveByPrefix(EntryCache, sourcePath);
            RemoveByPrefix(DefaultCache, sourcePath);
        }
    }

    private static void RemoveByPrefix<T>(Dictionary<string, T> cache, string sourcePath)
    {
        foreach (string key in cache.Keys.Where(k =>
                     k.StartsWith(sourcePath, StringComparison.OrdinalIgnoreCase)
                     || k.Contains(sourcePath, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            cache.Remove(key);
        }
    }

    public static IReadOnlyList<ArchiveEntry> Palettes(IArchive archive)
    {
        lock (Gate)
        {
            if (PalListCache.TryGetValue(archive.SourcePath, out List<ArchiveEntry>? cached))
            {
                return cached;
            }

            IArchive source = archive is FilteredArchive filtered ? filtered.Inner : archive;
            var list = source.Entries.Where(e => e.Extension.Equals(".pal", StringComparison.OrdinalIgnoreCase)).ToList();
            IArchive? khanPal = KhanPaletteArchive(archive);
            if (khanPal is not null && !khanPal.SourcePath.Equals(archive.SourcePath, StringComparison.OrdinalIgnoreCase))
            {
                list.AddRange(khanPal.Entries.Where(e => e.Extension.Equals(".pal", StringComparison.OrdinalIgnoreCase)));
            }

            PalListCache[archive.SourcePath] = list;
            return list;
        }
    }

    public static Palette? Named(IArchive archive, string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Equals("(auto)", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Palette? pal = TryGet(archive, name);
        if (pal is not null)
        {
            return pal;
        }

        ArchiveEntry? listed = Palettes(archive).FirstOrDefault(e =>
            e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            || e.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));
        return listed is null ? null : TryGet(archive, listed.Name);
    }

    public static Palette? Find(IArchive archive, params string[] preferredNames)
    {
        foreach (string name in preferredNames)
        {
            Palette? pal = TryGet(archive, name);
            if (pal is not null)
            {
                return pal;
            }
        }

        return DefaultPalette(archive);
    }

    public static Palette? FindNumbered(IArchive archive, string prefix, int number)
    {
        return TryGet(archive, $"{prefix}{number:D3}.pal")
            ?? TryGet(archive, $"{prefix}{number:D4}.pal")
            ?? TryGet(archive, $"{prefix}{number}.pal");
    }

    public static Palette? ForFloorTile(IArchive archive, int tileId)
    {
        int palNum = FloorPaletteNumber(archive, tileId);
        int tableKey = CapricornFloorTableKey(tileId);
        return FindNumbered(archive, "mpt", palNum)
            ?? FromTable(archive, "mptpal.tbl", "mpt", tableKey)
            ?? FromTable(archive, "mpspal.tbl", "mps", tableKey)
            ?? TryGet(archive, "mpspal.pal")
            ?? TryGet(archive, "mpt000.pal")
            ?? CombinedFieldPalette(archive)
            ?? DefaultPalette(archive);
    }

    /// <summary>
    /// TILEAS pixels are indexed for the base floor palette (mpt0000).
    /// mptpal.tbl overrides belong to TILEA and recolor snow grass olive and snow water brown.
    /// </summary>
    public static Palette? ForSnowFloor(IArchive archive)
    {
        return FindNumbered(archive, "mpt", 0)
            ?? TryGet(archive, "mpt0000.pal")
            ?? TryGet(archive, "mpt000.pal")
            ?? CombinedFieldPalette(archive)
            ?? DefaultPalette(archive);
    }

    public static Palette? ForWallTile(IArchive archive, int wallId)
    {
        return FromTable(archive, "stcpal.tbl", "stc", CapricornWallTableKey(wallId))
            ?? TryGet(archive, "stcpal.pal")
            ?? CombinedFieldPalette(archive)
            ?? DefaultPalette(archive);
    }

    /// <summary>Capricorn MapEditor: tileTable[(floor - 1) + 2] == table[floor + 1].</summary>
    public static int CapricornFloorTableKey(int tileId) => tileId + 1;

    /// <summary>Capricorn MapEditor: wallTable[mapId + 1].</summary>
    public static int CapricornWallTableKey(int wallId) => wallId + 1;

    public static int FloorPaletteNumber(IArchive archive, int tileId)
    {
        PaletteTable? table = GetTable(archive, "mptpal.tbl");
        if (table is null)
        {
            return 0;
        }

        return table.GetPaletteNumber(CapricornFloorTableKey(tileId));
    }

    public static int WallPaletteNumber(IArchive archive, int wallId)
    {
        PaletteTable? table = GetTable(archive, "stcpal.tbl");
        return table?.GetPaletteNumber(CapricornWallTableKey(wallId)) ?? 0;
    }

    public static Palette? ForEntry(IArchive archive, ArchiveEntry entry)
    {
        string key = archive.SourcePath + "|" + entry.Name;
        lock (Gate)
        {
            if (EntryCache.TryGetValue(key, out Palette? cached))
            {
                return cached;
            }
        }

        Palette? resolved = Resolve(archive, entry);
        lock (Gate)
        {
            EntryCache[key] = resolved;
        }

        return resolved;
    }

    /// <summary>effpal.tbl palette numbers ≥ 1000 mean luminance-to-alpha on palettized effect EPF.</summary>
    public static PaletteBlit BlitFor(IArchive archive, ArchiveEntry entry)
    {
        string stem = Path.GetFileNameWithoutExtension(entry.Name);
        if (!stem.StartsWith("efct", StringComparison.OrdinalIgnoreCase)
            && !stem.StartsWith("eff", StringComparison.OrdinalIgnoreCase))
        {
            return PaletteBlit.Opaque;
        }

        RelatedFiles.TryParse(entry.Name, out _, out int number);
        PaletteTable? table = GetTable(archive, "effpal.tbl");
        int palNum = table?.GetPaletteNumber(number) ?? 0;
        return palNum >= 1000 ? PaletteBlit.LuminanceAlpha : PaletteBlit.Opaque;
    }

    private static Palette? Resolve(IArchive archive, ArchiveEntry entry)
    {
        string stem = Path.GetFileNameWithoutExtension(entry.Name);
        RelatedFiles.TryParse(entry.Name, out string letters, out int number);
        string ext = entry.Extension;

        // MPF palettes live in the dummy frame (DADataViewer / BMP2EPF): mns{PaletteNumber:D3}.pal.
        // Do not use the file stem (mns042.pal) or a hades-wide default such as mns019.
        if (ext.Equals(".mpf", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                int palNum = MpfFile.PeekPaletteNumber(entry);
                Palette? mns = FindNumbered(archive, "mns", palNum)
                    ?? FindNumbered(archive, letters, palNum);
                if (mns is not null)
                {
                    return mns;
                }
            }
            catch
            {
                // fall through
            }
        }

        Palette? exact = TryGet(archive, stem + ".pal");
        if (exact is not null)
        {
            return exact;
        }

        if (KhanPieces.TryParse(entry.Name, out KhanPieceInfo khan))
        {
            string prefix = KhanPieces.PalettePrefix(khan);
            Palette? khanPal = FindNumbered(archive, prefix, 0)
                ?? TryGet(archive, prefix + "000.pal")
                ?? TryGet(archive, prefix + ".pal");
            if (khanPal is null && prefix is not "palc" and not "palb" and not "palm")
            {
                khanPal = TryGet(archive, "palc000.pal") ?? TryGet(archive, "palu000.pal");
            }

            if (khanPal is not null)
            {
                return khanPal;
            }
        }

        if (stem.StartsWith("item", StringComparison.OrdinalIgnoreCase))
        {
            int tileId = entry.FrameIndex is int frame
                ? LegendItems.ItemId(Math.Max(number, 1), frame)
                : LegendItems.ItemId(Math.Max(number, 1), 0);
            Palette? item = FromTable(archive, "itempal.tbl", "item", tileId)
                ?? TryGet(archive, "item000.pal");
            if (item is not null)
            {
                return item;
            }
        }

        if (stem.StartsWith("efct", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("eff", StringComparison.OrdinalIgnoreCase))
        {
            Palette? fx = FromTable(archive, "effpal.tbl", "eff", number)
                ?? FindNumbered(archive, "eff", number);
            if (fx is not null)
            {
                return fx;
            }
        }

        if (stem.StartsWith("skill", StringComparison.OrdinalIgnoreCase) ||
            stem.StartsWith("spell", StringComparison.OrdinalIgnoreCase) ||
            stem.StartsWith("gui", StringComparison.OrdinalIgnoreCase))
        {
            Palette? gui = TryGet(archive, "gui06.pal") ?? TryGet(archive, "gui000.pal") ?? TryGet(archive, "gui001.pal");
            if (gui is not null)
            {
                return gui;
            }
        }

        if (letters.StartsWith("stc", StringComparison.OrdinalIgnoreCase))
        {
            Palette? stc = FromTable(archive, "stcpal.tbl", "stc", CapricornWallTableKey(number)) ?? TryGet(archive, "stcpal.pal");
            if (stc is not null)
            {
                return stc;
            }
        }

        if (letters.StartsWith("sts", StringComparison.OrdinalIgnoreCase))
        {
            Palette? sts = FromTable(archive, "stspal.tbl", "sts", number) ?? TryGet(archive, "stspal.pal");
            if (sts is not null)
            {
                return sts;
            }
        }

        if (FloorTiles.IsAtlas(entry) || stem.Equals("TILEA", StringComparison.OrdinalIgnoreCase) || stem.Equals("TILEAS", StringComparison.OrdinalIgnoreCase))
        {
            bool snow = stem.Equals("TILEAS", StringComparison.OrdinalIgnoreCase);
            Palette? floor = snow
                ? ForSnowFloor(archive)
                : ForFloorTile(archive, Math.Max(1, (entry.FrameIndex ?? 0) + 1));
            if (floor is not null)
            {
                return floor;
            }
        }

        if (letters.StartsWith("mpt", StringComparison.OrdinalIgnoreCase) || letters.StartsWith("mps", StringComparison.OrdinalIgnoreCase) || letters.StartsWith("gnd", StringComparison.OrdinalIgnoreCase))
        {
            Palette? floor = FromTable(archive, "mptpal.tbl", "mpt", number)
                ?? FromTable(archive, "mpspal.tbl", "mps", number)
                ?? TryGet(archive, "mpspal.pal");
            if (floor is not null)
            {
                return floor;
            }
        }

        if (IsKhanPrefix(letters))
        {
            Palette? body = TryGet(archive, "palm000.pal")
                ?? TryGet(archive, "palb000.pal");
            if (body is not null)
            {
                return body;
            }
        }

        Palette? numbered = FindNumbered(archive, letters, number)
            ?? TryGet(archive, $"{letters}000.pal")
            ?? FromTable(archive, $"{letters}pal.tbl", letters, number);
        if (numbered is not null)
        {
            return numbered;
        }

        if (letters.Length >= 2)
        {
            ArchiveEntry? prefixPal = Palettes(archive).FirstOrDefault(e =>
                e.Name.StartsWith(letters, StringComparison.OrdinalIgnoreCase));
            if (prefixPal is not null)
            {
                return TryGet(archive, prefixPal.Name);
            }
        }

        return DefaultPalette(archive);
    }

    private static bool IsKhanPrefix(string letters) =>
        letters.Length >= 2 &&
        (letters[0] is 'm' or 'M' or 'w' or 'W') &&
        char.IsLetter(letters[1]) &&
        !letters.StartsWith("mns", StringComparison.OrdinalIgnoreCase) &&
        !letters.StartsWith("mpt", StringComparison.OrdinalIgnoreCase) &&
        !letters.StartsWith("mps", StringComparison.OrdinalIgnoreCase) &&
        !letters.StartsWith("mp3", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] PreferredDefaults =
    [
        "mpt0000.pal", "mpt000.pal", "mpt00.pal", "mpspal.pal",
        "field001.pal", "field.pal", "legend.pal", "legend01.pal"
    ];

    /// <summary>
    /// 3.x / Legends of Orphelias combined DATs have TILEA but no mpt####.pal.
    /// Floors and walls share field001.pal (Backpal* is a dark UI background).
    /// </summary>
    private static Palette? CombinedFieldPalette(IArchive archive) =>
        TryGet(archive, "field001.pal")
        ?? TryGet(archive, "field.pal")
        ?? TryGet(archive, "legend.pal");

    private static bool IsBackgroundPalette(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        return stem.StartsWith("backpal", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("staff", StringComparison.OrdinalIgnoreCase);
    }

    private static Palette? DefaultPalette(IArchive archive)
    {
        lock (Gate)
        {
            if (DefaultCache.TryGetValue(archive.SourcePath, out Palette? cached))
            {
                return cached;
            }
        }

        Palette? found = null;
        foreach (string name in PreferredDefaults)
        {
            found = TryGet(archive, name);
            if (found is not null)
            {
                break;
            }
        }

        found ??= Palettes(archive)
            .Where(e => !IsBackgroundPalette(e.Name))
            .Select(e => TryGet(archive, e.Name))
            .FirstOrDefault(p => p is not null)
            ?? Palettes(archive).Select(e => TryGet(archive, e.Name)).FirstOrDefault(p => p is not null);
        lock (Gate)
        {
            DefaultCache[archive.SourcePath] = found;
        }

        return found;
    }

    private static Palette? FromTable(IArchive archive, string tableName, string palPrefix, int index)
    {
        PaletteTable? table = GetTable(archive, tableName);
        if (table is null)
        {
            return null;
        }

        int palNum = table.GetPaletteNumber(index);
        return FindNumbered(archive, palPrefix, palNum);
    }

    private static PaletteTable? GetTable(IArchive archive, string tableName)
    {
        string key = archive.SourcePath + "|" + tableName;
        lock (Gate)
        {
            if (TableCache.TryGetValue(key, out PaletteTable? cached))
            {
                return cached;
            }
        }

        ArchiveEntry? tableEntry = archive.GetEntry(tableName)
            ?? KhanPaletteArchive(archive)?.GetEntry(tableName);
        if (tableEntry is null)
        {
            return null;
        }

        try
        {
            var table = new PaletteTable();
            table.AddTable(tableEntry);
            lock (Gate)
            {
                TableCache[key] = table;
            }

            return table;
        }
        catch
        {
            return null;
        }
    }

    private static Palette? TryGet(IArchive archive, string name)
    {
        Palette? pal = TryGetFrom(archive, name);
        if (pal is not null)
        {
            return pal;
        }

        IArchive? khanPal = KhanPaletteArchive(archive);
        return khanPal is null ? null : TryGetFrom(khanPal, name);
    }

    private static Palette? TryGetFrom(IArchive archive, string name)
    {
        string key = archive.SourcePath + "|" + name;
        lock (Gate)
        {
            if (PalCache.TryGetValue(key, out Palette? cached))
            {
                return cached;
            }
        }

        ArchiveEntry? entry = archive.GetEntry(name);
        if (entry is null)
        {
            return null;
        }

        try
        {
            var pal = new Palette(entry);
            lock (Gate)
            {
                PalCache[key] = pal;
            }

            return pal;
        }
        catch
        {
            return null;
        }
    }

    private static IArchive? KhanPaletteArchive(IArchive archive)
    {
        string fileName = Path.GetFileName(archive.SourcePath);
        if (fileName.StartsWith("khanpal", StringComparison.OrdinalIgnoreCase))
        {
            return archive;
        }

        string? dir = Path.GetDirectoryName(archive.SourcePath);
        if (string.IsNullOrEmpty(dir))
        {
            return null;
        }

        foreach (string candidate in new[]
                 {
                     Path.Combine(dir, "khanpal.dat"),
                     Path.Combine(Directory.GetParent(dir)?.FullName ?? dir, "khanpal.dat")
                 })
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            lock (Gate)
            {
                if (ExtraArchives.TryGetValue(candidate, out IArchive? cached))
                {
                    return cached;
                }

                try
                {
                    var opened = new DataArchive(candidate);
                    ExtraArchives[candidate] = opened;
                    return opened;
                }
                catch
                {
                    return null;
                }
            }
        }

        return null;
    }
}
