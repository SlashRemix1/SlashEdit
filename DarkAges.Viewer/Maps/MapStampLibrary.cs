using DarkAges.Formats.Maps;
using DarkAges.Viewer.Services;

namespace DarkAges.Viewer.Maps;

public static class MapStampLibrary
{
    public static List<StructureStamp> Load(MapCatalogStats stats, IReadOnlyCollection<int> wallIds)
    {
        StructurePresetStore store = StructurePresetStore.Load();
        var hidden = new HashSet<string>(store.HiddenKeys, StringComparer.Ordinal);
        var custom = store.CustomStamps().Where(s => !hidden.Contains(s.Key)).ToList();
        var stamps = stats.Structures.Where(s => !hidden.Contains(s.Key)).ToList();
        var seen = new HashSet<string>(
            custom.Select(s => s.Key).Concat(stamps.Select(s => s.Key)),
            StringComparer.Ordinal);
        IReadOnlyList<WallStyle> partners = stats.ListPartnerStyles(wallIds, 96);
        foreach (WallStyle style in partners)
        {
            AddUnlessHidden(stamps, seen, hidden, PairCorner(style));
            AddUnlessHidden(stamps, seen, hidden, PairRun(style));
            AddUnlessHidden(stamps, seen, hidden, PairColumn(style));
            AddUnlessHidden(stamps, seen, hidden, PairHouse(style, 4, 3));
            AddUnlessHidden(stamps, seen, hidden, PairHouse(style, 5, 4));
        }

        foreach (StructureStamp path in PathKits())
        {
            AddUnlessHidden(stamps, seen, hidden, path);
        }

        foreach ((ushort left, ushort right, int count) in stats.TreeSprites)
        {
            AddUnlessHidden(stamps, seen, hidden, TreeSprite(left, right, count));
        }

        return custom
            .Concat(stamps
                .OrderByDescending(s => s.Uses)
                .ThenBy(s => s.Label, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    public static IReadOnlyList<StructureStamp> Of(IEnumerable<StructureStamp> stamps, StampCategory category) =>
        stamps.Where(s => s.Category == category).ToList();

    public static bool IsProcedural(StructureStamp stamp)
    {
        string key = stamp.Key;
        return key.StartsWith("kit-", StringComparison.Ordinal)
            || key.StartsWith("l-", StringComparison.Ordinal)
            || key.StartsWith("yard-", StringComparison.Ordinal)
            || key.StartsWith("hall-", StringComparison.Ordinal)
            || key.StartsWith("u-", StringComparison.Ordinal)
            || key.StartsWith("gate-", StringComparison.Ordinal)
            || key.StartsWith("twin-", StringComparison.Ordinal)
            || key.StartsWith("pair-", StringComparison.Ordinal)
            || key.StartsWith("path-", StringComparison.Ordinal);
    }

    public static StructureStamp Pick(IReadOnlyList<StructureStamp> stamps, Random rng, int maxWidth, int maxHeight, bool favorLarge = false)
    {
        var fit = stamps.Where(s => s.Width <= maxWidth && s.Height <= maxHeight).ToList();
        if (fit.Count == 0)
        {
            fit = stamps.ToList();
        }

        int total = 0;
        foreach (StructureStamp stamp in fit)
        {
            total += Weight(stamp, favorLarge);
        }

        int pick = rng.Next(Math.Max(1, total));
        foreach (StructureStamp stamp in fit)
        {
            pick -= Weight(stamp, favorLarge);
            if (pick < 0)
            {
                return stamp;
            }
        }

        return fit[^1];
    }

    private static int Weight(StructureStamp stamp, bool favorLarge = false)
    {
        int uses = stamp.Uses > 0 ? 4 + Math.Min(24, stamp.Uses) : 3;
        int weight = stamp.CopyFloors ? uses + 4 : uses;
        if (favorLarge)
        {
            weight *= 1 + Math.Clamp(stamp.Width * stamp.Height / 8, 0, 5);
        }

        return weight;
    }

    private static void AddUnlessHidden(
        List<StructureStamp> stamps,
        HashSet<string> seen,
        HashSet<string> hidden,
        StructureStamp stamp)
    {
        if (hidden.Contains(stamp.Key))
        {
            return;
        }

        Add(stamps, seen, stamp);
    }

    private static void Add(List<StructureStamp> stamps, HashSet<string> seen, StructureStamp stamp)
    {
        if (seen.Add(stamp.Key))
        {
            stamps.Add(stamp);
        }
    }

    private static IEnumerable<StructureStamp> PathKits()
    {
        yield return PathRibbon(8, 3, eastWest: true, "east-west road");
        yield return PathRibbon(12, 3, eastWest: true, "east-west road");
        yield return PathRibbon(3, 8, eastWest: false, "north-south road");
        yield return PathRibbon(3, 12, eastWest: false, "north-south road");
        yield return PathJunction(5, "cross", (x, y) => x == 2 || y == 2);
        yield return PathJunction(7, "cross", (x, y) => x == 3 || y == 3);
        yield return PathJunction(5, "T south", (x, y) => y == 2 || x == 2 && y >= 2);
        yield return PathJunction(5, "T north", (x, y) => y == 2 || x == 2 && y <= 2);
        yield return PathJunction(5, "T east", (x, y) => x == 2 || y == 2 && x >= 2);
        yield return PathJunction(5, "T west", (x, y) => x == 2 || y == 2 && x <= 2);
        yield return PathJunction(5, "L south-west", (x, y) => x <= 2 && y == 2 || x == 2 && y >= 2);
        yield return PathJunction(5, "L south-east", (x, y) => x >= 2 && y == 2 || x == 2 && y >= 2);
    }

    private static StructureStamp PathRibbon(int width, int height, bool eastWest, string name)
    {
        var tiles = new MapTile[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool walk = eastWest ? y == height / 2 : x == width / 2;
                tiles[y * width + x] = new MapTile(walk ? (ushort)2 : (ushort)1, 0, 0);
            }
        }

        return PathStamp($"path-{name}-{width}x{height}", $"Path  {name}  {width}×{height}", width, height, tiles, 4);
    }

    private static StructureStamp PathJunction(int size, string name, Func<int, int, bool> walk)
    {
        var tiles = new MapTile[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool on = walk(x, y);
                bool verge = !on && (
                    (x > 0 && walk(x - 1, y)) || (x + 1 < size && walk(x + 1, y))
                    || (y > 0 && walk(x, y - 1)) || (y + 1 < size && walk(x, y + 1)));
                tiles[y * size + x] = on
                    ? new MapTile(2, 0, 0)
                    : verge ? new MapTile(1, 0, 0) : default;
            }
        }

        return PathStamp($"path-{name}-{size}x{size}", $"Path  {name}  {size}×{size}", size, size, tiles, 5);
    }

    private static StructureStamp PathStamp(string key, string label, int width, int height, MapTile[] tiles, int uses) =>
        new(key, label, width, height, tiles, uses, CopyFloors: true, Category: StampCategory.Path);

    private static StructureStamp PairCorner(WallStyle walls)
    {
        var tiles = new[] { new MapTile(0, walls.CornerLeft, walls.CornerRight) };
        return new StructureStamp(
            $"pair-lr-{walls.CornerLeft}-{walls.CornerRight}",
            $"Pair corner  L{walls.CornerLeft}/R{walls.CornerRight}",
            1,
            1,
            tiles,
            12,
            CopyFloors: false,
            Category: StampCategory.Pair);
    }

    private static StructureStamp PairRun(WallStyle walls)
    {
        var tiles = new[]
        {
            new MapTile(0, walls.SpanLeft, 0),
            new MapTile(0, walls.SpanLeft2, 0),
            new MapTile(0, walls.CornerLeft, walls.CornerRight)
        };
        return new StructureStamp(
            $"pair-run-{walls.CornerLeft}-{walls.CornerRight}",
            $"Pair 3-wide  L{walls.CornerLeft}/R{walls.CornerRight}",
            3,
            1,
            tiles,
            14,
            CopyFloors: false,
            Category: StampCategory.Pair);
    }

    private static StructureStamp PairColumn(WallStyle walls)
    {
        var tiles = new[]
        {
            new MapTile(0, 0, walls.EdgeRight),
            new MapTile(0, walls.CornerLeft, walls.CornerRight)
        };
        return new StructureStamp(
            $"pair-col-{walls.CornerLeft}-{walls.CornerRight}",
            $"Pair column  L{walls.CornerLeft}/R{walls.CornerRight}",
            1,
            2,
            tiles,
            10,
            CopyFloors: false,
            Category: StampCategory.Pair);
    }

    private static StructureStamp PairHouse(WallStyle walls, int width, int height)
    {
        StructureStamp kit = MapGenerator.CreateKit(walls, width, height);
        return kit with
        {
            Key = $"pair-house-{walls.CornerLeft}-{walls.CornerRight}-{width}x{height}",
            Label = $"Pair house  L{walls.CornerLeft}/R{walls.CornerRight}  {width}×{height}",
            Uses = 8,
            Category = StampCategory.Pair
        };
    }

    private static StructureStamp TreeSprite(ushort left, ushort right, int uses)
    {
        var tiles = new[] { new MapTile(0, left, right) };
        string tag = left != 0 && right != 0 ? $"L{left}/R{right}" : left != 0 ? $"L{left}" : $"R{right}";
        return new StructureStamp(
            $"tree-{left}-{right}",
            $"Tree  {tag}",
            1,
            1,
            tiles,
            Math.Max(1, uses),
            CopyFloors: false,
            Category: StampCategory.Tree);
    }
}
