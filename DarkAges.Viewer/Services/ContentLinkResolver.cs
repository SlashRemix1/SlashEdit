using System.IO;
using DarkAges.Formats.Catalog;
using DarkAges.Formats.Data;
using DarkAges.Formats.Unity;

namespace DarkAges.Viewer.Services;

public static class ContentLinkResolver
{
    private static readonly Dictionary<string, IReadOnlyList<(int Sheet, int FrameCount)>> ItemLayouts =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool TryResolveVisual(
        ContentSearchHit hit,
        IReadOnlyList<CatalogEntryView> catalogs,
        ArchiveCache cache,
        out CatalogEntryView catalog,
        out ArchiveEntry entry,
        bool openArchives = true)
    {
        catalog = null!;
        entry = null!;

        // Item.bin → inventory icon via TileNumber (not Korean DisplayName).
        if (IsItemTable(hit.TableFile)
            && TryTileNumber(hit, out int tile)
            && TryResolveItemTile(tile, catalogs, cache, openArchives, out catalog, out entry))
        {
            return true;
        }

        string? key = hit.PrefabOrIcon;
        if (string.IsNullOrWhiteSpace(key))
        {
            key = First(hit.Fields, "PrefabName", "UIIconName", "Name", "ID");
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        key = key.Trim();
        // Numeric-only keys on item tables are TileNumbers (already tried above).
        if (IsItemTable(hit.TableFile) && int.TryParse(key, out _))
        {
            return false;
        }

        string prefix = AssetPrefix(key);
        foreach (CatalogEntryView candidate in RankCatalogs(catalogs, prefix, hit.TableFile))
        {
            if (!UnityBundleArchive.LooksLike(candidate.SourcePath))
            {
                continue;
            }

            if (!openArchives && !cache.Contains(candidate))
            {
                continue;
            }

            try
            {
                if (cache.Get(candidate) is not UnityBundleArchive unity)
                {
                    continue;
                }

                if (openArchives && !unity.SpriteIndexReady)
                {
                    unity.EnsureSpriteIndex();
                }

                ArchiveEntry? match = FindSprite(unity, key);
                if (match is not null)
                {
                    catalog = candidate;
                    entry = match;
                    return true;
                }
            }
            catch
            {
                // try next catalog
            }
        }

        return false;
    }

    /// <summary>Resolve using already-open archives only — safe on the UI thread during search.</summary>
    public static bool TryResolveVisualCached(
        ContentSearchHit hit,
        IReadOnlyList<CatalogEntryView> catalogs,
        ArchiveCache cache,
        out CatalogEntryView catalog,
        out ArchiveEntry entry) =>
        TryResolveVisual(hit, catalogs, cache, out catalog, out entry, openArchives: false);

    public static bool TryResolveClassicItem(
        int graphicId,
        IReadOnlyList<CatalogEntryView> catalogs,
        ArchiveCache cache,
        out CatalogEntryView catalog,
        out ArchiveEntry entry,
        bool openArchives = true)
    {
        catalog = null!;
        entry = null!;
        if (graphicId <= 0)
        {
            return false;
        }

        foreach (CatalogEntryView candidate in catalogs.Where(c => c.Role == ArchiveRole.Items))
        {
            if (!openArchives && !cache.Contains(candidate))
            {
                continue;
            }

            try
            {
                IArchive archive = cache.Get(candidate);
                foreach (ArchiveEntry sheet in LegendItems.Expand(archive.Entries))
                {
                    if (sheet.FrameIndex is int frame
                        && LegendItems.ItemId(archive.Entries, LegendItems.FileNumber(sheet), frame) == graphicId)
                    {
                        catalog = candidate;
                        entry = sheet;
                        return true;
                    }

                    // Expanded tiles are labeled "item 0042" — match by display name id.
                    if (sheet.DisplayName.StartsWith("item ", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(sheet.DisplayName.AsSpan(5), out int listed)
                        && listed == graphicId)
                    {
                        catalog = candidate;
                        entry = sheet;
                        return true;
                    }
                }
            }
            catch
            {
                // next
            }
        }

        return false;
    }

    public static bool TryResolveClassicItemCached(
        int graphicId,
        IReadOnlyList<CatalogEntryView> catalogs,
        ArchiveCache cache,
        out CatalogEntryView catalog,
        out ArchiveEntry entry) =>
        TryResolveClassicItem(graphicId, catalogs, cache, out catalog, out entry, openArchives: false);

    private static bool TryResolveItemTile(
        int tileNumber,
        IReadOnlyList<CatalogEntryView> catalogs,
        ArchiveCache cache,
        bool openArchives,
        out CatalogEntryView catalog,
        out ArchiveEntry entry)
    {
        catalog = null!;
        entry = null!;
        foreach (CatalogEntryView candidate in catalogs.Where(c => c.Role == ArchiveRole.Items))
        {
            if (!UnityBundleArchive.LooksLike(candidate.SourcePath))
            {
                continue;
            }

            if (!openArchives && !cache.Contains(candidate))
            {
                continue;
            }

            try
            {
                if (cache.Get(candidate) is not UnityBundleArchive unity)
                {
                    continue;
                }

                if (openArchives && !unity.SpriteIndexReady)
                {
                    unity.EnsureSpriteIndex();
                }

                IReadOnlyList<(int Sheet, int FrameCount)> layout = ItemLayoutFor(unity);
                ArchiveEntry? match = LodRItemIcons.FindSprite(unity, layout, tileNumber);
                if (match is not null)
                {
                    catalog = candidate;
                    entry = match;
                    return true;
                }
            }
            catch
            {
                // next items bundle
            }
        }

        return false;
    }

    private static IReadOnlyList<(int Sheet, int FrameCount)> ItemLayoutFor(UnityBundleArchive unity)
    {
        lock (ItemLayouts)
        {
            if (ItemLayouts.TryGetValue(unity.SourcePath, out IReadOnlyList<(int, int)>? cached))
            {
                return cached;
            }

            IReadOnlyList<(int, int)> layout = LodRItemIcons.SheetLayout(unity);
            ItemLayouts[unity.SourcePath] = layout;
            return layout;
        }
    }

    private static bool IsItemTable(string tableFile)
    {
        string stem = Path.GetFileNameWithoutExtension(tableFile);
        return stem.Equals("Item", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("ItemDescription", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("ItemHelp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryTileNumber(ContentSearchHit hit, out int tile)
    {
        tile = 0;
        if (hit.Fields.TryGetValue("TileNumber", out string? raw)
            && int.TryParse(raw.Trim(), out tile)
            && tile >= 0)
        {
            return true;
        }

        return false;
    }

    private static ArchiveEntry? FindSprite(UnityBundleArchive unity, string key)
    {
        ArchiveEntry? exact = unity.Entries.FirstOrDefault(e =>
            e.DisplayName.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        // Prefab effect080 → listed as effect080 (collapsed) or effect080_0
        ArchiveEntry? group = unity.Entries.FirstOrDefault(e =>
            e.DisplayName.Equals(key, StringComparison.OrdinalIgnoreCase)
            || e.DisplayName.StartsWith(key + "_", StringComparison.OrdinalIgnoreCase));
        if (group is not null)
        {
            return group;
        }

        return unity.Entries.FirstOrDefault(e =>
            e.DisplayName.Contains(key, StringComparison.OrdinalIgnoreCase)
            && LodRAssets.IsImageEntry(e.Name));
    }

    private static IEnumerable<CatalogEntryView> RankCatalogs(
        IReadOnlyList<CatalogEntryView> catalogs,
        string prefix,
        string tableFile)
    {
        string table = Path.GetFileNameWithoutExtension(tableFile);
        ArchiveRole preferred = table.StartsWith("Item", StringComparison.OrdinalIgnoreCase) ? ArchiveRole.Items
            : table.StartsWith("Monster", StringComparison.OrdinalIgnoreCase) ? ArchiveRole.Mobs
            : table.StartsWith("Effect", StringComparison.OrdinalIgnoreCase) || table.Equals("EFA", StringComparison.OrdinalIgnoreCase) ? ArchiveRole.Effects
            : table.StartsWith("Skill", StringComparison.OrdinalIgnoreCase)
                || table.StartsWith("Spell", StringComparison.OrdinalIgnoreCase)
                || table.StartsWith("Emblem", StringComparison.OrdinalIgnoreCase)
                || table.StartsWith("Emot", StringComparison.OrdinalIgnoreCase) ? ArchiveRole.Ui
            : ArchiveRole.Unknown;

        return catalogs
            .Where(c => UnityBundleArchive.LooksLike(c.SourcePath))
            .OrderByDescending(c => ScoreCatalog(c, prefix, preferred))
            .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase);
    }

    private static int ScoreCatalog(CatalogEntryView catalog, string prefix, ArchiveRole preferred)
    {
        int score = 0;
        string file = Path.GetFileName(catalog.SourcePath);
        if (!string.IsNullOrEmpty(prefix)
            && file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        if (preferred != ArchiveRole.Unknown && catalog.Role == preferred)
        {
            score += 40;
        }

        if (LodRAssets.IsIconBundle(catalog.SourcePath) && preferred == ArchiveRole.Ui)
        {
            score += 10;
        }

        return score;
    }

    private static string AssetPrefix(string key)
    {
        var chars = new List<char>();
        foreach (char c in key)
        {
            if (char.IsLetter(c))
            {
                chars.Add(c);
            }
            else
            {
                break;
            }
        }

        return chars.Count == 0 ? "" : new string(chars.ToArray()).ToLowerInvariant();
    }

    private static string? First(IReadOnlyDictionary<string, string> fields, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (fields.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
