using DarkAges.Formats.Imaging;
using DarkAges.Formats.Maps;

namespace DarkAges.Viewer.Maps;

public sealed class MapGeneratorOptions
{
    public int Width { get; init; } = 20;
    public int Height { get; init; } = 20;
    public ushort Floor { get; init; } = 278;
    public ushort PathFloor { get; init; }
    public int WallSeed { get; init; }
    public int BuildingCount { get; init; } = 6;
    public int PathCount { get; init; } = 2;
    public int Seed { get; init; }
}

public static class MapGenerator
{
    public static MapTile[] Generate(MapGeneratorOptions options, MapAssets assets, MapCatalogStats stats)
    {
        int width = Math.Clamp(options.Width, 4, MapFile.MaxDimension);
        int height = Math.Clamp(options.Height, 4, MapFile.MaxDimension);
        var rng = new Random(options.Seed == 0 ? Random.Shared.Next() : options.Seed);
        ushort floor = VisibleGround(options.Floor, assets);
        ushort path = VisiblePath(options.PathFloor, floor, assets, stats);
        IReadOnlyList<ushort> likes = FloorBand(floor, assets);
        IReadOnlyList<WallStyle> wallSets = stats.ListPartnerStyles(
            assets.WallIds,
            Math.Max(8, options.BuildingCount * 4));
        if (wallSets.Count == 0)
        {
            wallSets = stats.PickWallStyles(options.WallSeed, assets.WallIds, rng, Math.Max(4, options.BuildingCount));
        }
        var tiles = new MapTile[width * height];
        var isPath = new bool[width * height];
        var blocked = new bool[width * height];
        var none = Array.Empty<bool>();

        for (int i = 0; i < tiles.Length; i++)
        {
            tiles[i] = new MapTile(likes[rng.Next(likes.Count)], 0, 0);
        }

        int buildings = Math.Clamp(options.BuildingCount, 0, Math.Max(1, width * height / 18));
        IReadOnlyList<StructureStamp> library = MapStampLibrary.Load(stats, assets.WallIds);
        IReadOnlyList<StructureStamp> houses = MapStampLibrary.Of(library, StampCategory.Building);
        if (houses.Count == 0)
        {
            houses = library;
        }

        var doors = new List<(int X, int Y)>();
        int attempts = 0;
        while (doors.Count < buildings && attempts++ < buildings * 18)
        {
            int maxW = Math.Max(3, width - 2);
            int maxH = Math.Max(2, height - 2);
            StructureStamp stamp = houses.Count == 0
                ? CreateKit(PickStyle(wallSets, rng), Math.Min(6, maxW), Math.Min(4, maxH))
                : MapStampLibrary.Pick(houses, rng, maxW, maxH);
            int bw = Math.Clamp(stamp.Width, 2, maxW);
            int bh = Math.Clamp(stamp.Height, 2, maxH);
            if (stamp.Width > maxW || stamp.Height > maxH)
            {
                continue;
            }

            int x0 = rng.Next(1, Math.Max(2, width - bw));
            int y0 = rng.Next(1, Math.Max(2, height - bh));
            int x1 = x0 + bw - 1;
            int y1 = y0 + bh - 1;
            if (!Fits(width, height, x0, y0, x1, y1, none, blocked))
            {
                continue;
            }

            WallStyle walls = PickStyle(wallSets, rng);
            PlaceStamp(tiles, width, x0, y0, stamp, walls, assets.WallIds, floor, likes, rng, keepFloors: false);
            MarkRect(blocked, width, x0 - 1, y0 - 1, x1 + 1, y1 + 1);
            doors.Add(DoorAnchor(x0, y0, stamp, width, height));
        }

        LayRoutes(tiles, isPath, width, height, doors, options.PathCount, rng);
        PaintPathFloors(tiles, isPath, path, [], rng, keepWalls: true);

        return tiles;
    }

    public static MapTile[] RerollWalls(MapFile map, MapAssets assets, MapCatalogStats stats, int wallSeed, int buildingCount = 6)
    {
        int width = map.Width;
        int height = map.Height;
        var rng = new Random(Random.Shared.Next());
        if (wallSeed <= 2 && assets.WallIds.Count > 0)
        {
            wallSeed = assets.WallIds[rng.Next(assets.WallIds.Count)];
        }

        int buildings = Math.Clamp(buildingCount, 0, Math.Max(1, width * height / 18));
        IReadOnlyList<WallStyle> wallSets = stats.ListPartnerStyles(
            assets.WallIds,
            Math.Max(8, Math.Max(1, buildings) * 4));
        if (wallSets.Count == 0)
        {
            wallSets = stats.PickWallStyles(wallSeed, assets.WallIds, rng, Math.Max(4, buildings));
        }
        var tiles = new MapTile[map.Tiles.Length];
        var blocked = new bool[tiles.Length];
        var none = Array.Empty<bool>();
        ushort ground = MajorityOpenFloor(map.Tiles);
        bool[] structureFloors = StructureFloorFootprint(map, ground);
        for (int i = 0; i < tiles.Length; i++)
        {
            MapTile tile = map.Tiles[i];
            ushort floor = structureFloors[i] && ground != 0 ? ground : tile.Floor;
            tiles[i] = new MapTile(floor, 0, 0);
            if (floor == 0)
            {
                blocked[i] = true;
            }
        }

        IReadOnlyList<StructureStamp> library = MapStampLibrary.Load(stats, assets.WallIds);
        IReadOnlyList<StructureStamp> houses = MapStampLibrary.Of(library, StampCategory.Building);
        var harvested = houses.Where(s => !MapStampLibrary.IsProcedural(s)).ToList();
        IReadOnlyList<StructureStamp> pool = harvested.Count > 0 ? harvested : houses;

        int placed = 0;
        int attempts = 0;
        while (placed < buildings && attempts++ < buildings * 30)
        {
            int maxW = Math.Max(3, Math.Min(20, width - 2));
            int maxH = Math.Max(2, Math.Min(16, height - 2));
            if (maxW < 3 || maxH < 2)
            {
                break;
            }

            StructureStamp stamp = pool.Count == 0
                ? CreateKit(PickStyle(wallSets, rng), Math.Min(6, maxW), Math.Min(4, maxH))
                : MapStampLibrary.Pick(pool, rng, maxW, maxH, favorLarge: true);
            int bw = stamp.Width;
            int bh = stamp.Height;
            if (bw > maxW || bh > maxH)
            {
                continue;
            }

            int x0 = rng.Next(1, Math.Max(2, width - bw));
            int y0 = rng.Next(1, Math.Max(2, height - bh));
            int x1 = x0 + bw - 1;
            int y1 = y0 + bh - 1;
            if (!Fits(width, height, x0, y0, x1, y1, none, blocked))
            {
                continue;
            }

            PlaceStamp(
                tiles,
                width,
                x0,
                y0,
                stamp,
                PickStyle(wallSets, rng),
                assets.WallIds,
                1,
                [],
                rng,
                keepFloors: true,
                preserveWalls: !MapStampLibrary.IsProcedural(stamp));
            MarkRect(blocked, width, x0 - 1, y0 - 1, x1 + 1, y1 + 1);
            placed++;
        }

        return tiles;
    }

    public static MapTile[] RerollFloors(MapFile map, MapAssets assets, MapCatalogStats stats, MapGeneratorOptions options)
    {
        var rng = new Random(Random.Shared.Next());
        ushort floor = VisibleGround(options.Floor, assets);
        ushort path = VisiblePath(options.PathFloor, floor, assets, stats);
        IReadOnlyList<ushort> likes = FloorBand(floor, assets);
        var tiles = new MapTile[map.Tiles.Length];
        for (int i = 0; i < tiles.Length; i++)
        {
            MapTile tile = map.Tiles[i];
            if (tile.Floor == 0)
            {
                tiles[i] = tile;
                continue;
            }

            tiles[i] = new MapTile(likes[rng.Next(likes.Count)], tile.LeftWall, tile.RightWall);
        }

        var isPath = new bool[tiles.Length];
        List<(int X, int Y)> anchors = StructureAnchors(tiles, map.Width, map.Height);
        LayRoutes(tiles, isPath, map.Width, map.Height, anchors, options.PathCount, rng);
        PaintPathFloors(tiles, isPath, path, [], rng, keepWalls: true);
        return tiles;
    }

    public static (MapTile[] Tiles, int Placed) RerollFoliage(MapFile map, MapAssets assets, MapCatalogStats stats, ushort pathFloor = 0)
    {
        IReadOnlyList<StructureStamp> library = MapStampLibrary.Load(stats, assets.WallIds);
        var trees = MapStampLibrary.Of(library, StampCategory.Tree)
            .Where(s => s.Width <= 4 && s.Height <= 4 && s.Cells.Any(c => c.LeftWall != 0 || c.RightWall != 0))
            .ToList();
        if (trees.Count == 0)
        {
            return (map.Tiles, 0);
        }

        int width = map.Width;
        int height = map.Height;
        var rng = new Random(Random.Shared.Next());
        var tiles = map.Tiles.ToArray();
        ClearFoliage(tiles, width, height);
        ushort ground = MajorityOpenFloor(tiles);
        FloorFamily? groundFamily = stats.FamilyOf(ground) ?? FloorFamilies.Find(ground);
        var forbidden = new bool[tiles.Length];
        int open = 0;
        for (int i = 0; i < tiles.Length; i++)
        {
            if (IsFoliageBlocked(tiles[i], ground, groundFamily, pathFloor))
            {
                forbidden[i] = true;
            }
            else if (tiles[i].Floor != 0)
            {
                open++;
            }
        }

        int target = Math.Clamp(open / 14, 4, 120);
        var occupied = new bool[tiles.Length];
        int placed = 0;
        int attempts = 0;
        while (placed < target && attempts++ < target * 16)
        {
            StructureStamp stamp = MapStampLibrary.Pick(trees, rng, 4, 4);
            if (!TryFoliageOrigin(width, height, stamp, rng, out int x0, out int y0))
            {
                continue;
            }

            if (!FoliageFits(tiles, forbidden, occupied, width, height, stamp, x0, y0, ground, groundFamily, pathFloor))
            {
                continue;
            }

            PlaceStamp(
                tiles,
                width,
                x0,
                y0,
                stamp with { CopyFloors = false },
                default,
                assets.WallIds,
                ground,
                [],
                rng,
                keepFloors: true,
                preserveWalls: true);
            MarkFoliage(occupied, width, height, stamp, x0, y0);
            placed++;
        }

        return (tiles, placed);
    }

    private static ushort VisibleGround(ushort requested, MapAssets assets)
    {
        if (requested > 1 && IsPaintedFloor(requested, assets))
        {
            return requested;
        }

        if (IsPaintedFloor(278, assets))
        {
            return 278;
        }

        int limit = assets.FloorCount == 0 ? 400 : assets.FloorCount;
        for (int id = 2; id <= limit; id++)
        {
            if (IsPaintedFloor(id, assets))
            {
                return (ushort)id;
            }
        }

        return requested == 0 ? (ushort)278 : requested;
    }

    public static ushort ResolveStyledPath(ushort requested, ushort ground, MapAssets assets, MapCatalogStats stats) =>
        VisiblePath(requested, ground, assets, stats);

    private static ushort VisiblePath(ushort requested, ushort ground, MapAssets assets, MapCatalogStats stats)
    {
        if (requested > 1 && requested != ground && IsPaintedFloor(requested, assets))
        {
            return requested;
        }

        FloorFamily? pathFamily = FloorFamilies.PathFor(ground);
        FloorFamily? groundFamily = FloorFamilies.Find(ground);
        bool paired = pathFamily is FloorFamily want
            && groundFamily is FloorFamily home
            && want.Min != home.Min;
        ushort learned = stats.ResolveFamilyPath(ground, assets.FloorCount);
        if (learned > 1 && learned != ground && IsPaintedFloor(learned, assets)
            && (!paired || pathFamily!.Value.Contains(learned)))
        {
            return learned;
        }

        if (pathFamily is FloorFamily family)
        {
            int prefer = Math.Clamp(requested > 1 ? requested : (family.Min + family.Max) / 2, family.Min, family.Max);
            if (prefer != ground && IsPaintedFloor(prefer, assets) && (!paired || family.Contains(prefer)))
            {
                return (ushort)prefer;
            }

            ushort? painted = PaintedInFamily(family, ground, assets);
            if (painted is ushort id)
            {
                return id;
            }
        }

        if (paired)
        {
            return ground;
        }

        foreach (ushort id in stats.LikeFloors(ground, assets.FloorCount, take: 16))
        {
            if (id > 1 && id != ground && IsPaintedFloor(id, assets))
            {
                return id;
            }
        }

        return ground;
    }

    private static ushort? PaintedInFamily(FloorFamily family, ushort ground, MapAssets assets)
    {
        int mid = (family.Min + family.Max) / 2;
        int span = Math.Max(family.Max - mid, mid - family.Min);
        for (int delta = 0; delta <= span; delta++)
        {
            foreach (int id in new[] { mid - delta, mid + delta })
            {
                if (id < family.Min || id > family.Max || id == ground)
                {
                    continue;
                }

                if (IsPaintedFloor(id, assets))
                {
                    return (ushort)id;
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<ushort> FloorBand(ushort floor, MapAssets assets)
    {
        var band = new List<ushort>();
        for (int delta = -2; delta <= 2; delta++)
        {
            int id = floor + delta;
            if (id <= 1)
            {
                continue;
            }

            if (id == floor || IsPaintedFloor(id, assets))
            {
                band.Add((ushort)id);
            }
        }

        if (band.Count == 0)
        {
            band.Add(floor);
        }

        return band;
    }

    private static bool IsPaintedFloor(int id, MapAssets assets) =>
        id > 1 && assets.Floor(id, snow: false) is { } image && image.HasVisiblePixels();

    private static IReadOnlyList<ushort> VisibleFloors(IReadOnlyList<ushort> ids, MapAssets assets)
    {
        var kept = new List<ushort>();
        foreach (ushort id in ids)
        {
            if (id <= 1)
            {
                continue;
            }

            PixelBuffer? image = assets.Floor(id, snow: false);
            if (image is not null && image.HasVisiblePixels())
            {
                kept.Add(id);
            }
        }

        return kept;
    }

    private static ushort MajorityOpenFloor(MapTile[] tiles)
    {
        var counts = new Dictionary<ushort, int>();
        foreach (MapTile tile in tiles)
        {
            if (tile.Floor == 0 || tile.LeftWall != 0 || tile.RightWall != 0)
            {
                continue;
            }

            counts[tile.Floor] = counts.GetValueOrDefault(tile.Floor) + 1;
        }

        if (counts.Count == 0)
        {
            return MajorityFloor(tiles);
        }

        return counts.OrderByDescending(e => e.Value).First().Key;
    }

    /// <summary>
    /// Wall tiles plus the floors a structure stamp painted inside its footprint.
    /// Stops at the open ground so paths outside the building stay put.
    /// </summary>
    private static bool[] StructureFloorFootprint(MapFile map, ushort ground)
    {
        int width = map.Width;
        int height = map.Height;
        var mark = new bool[map.Tiles.Length];
        var seen = new bool[map.Tiles.Length];
        int[] dx = [1, -1, 0, 0, 1, 1, -1, -1];
        int[] dy = [0, 0, 1, -1, 1, -1, 1, -1];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int start = y * width + x;
                if (seen[start] || !HasEitherWall(map.Tiles[start]))
                {
                    continue;
                }

                var walls = new List<int>();
                var queue = new Queue<int>();
                queue.Enqueue(start);
                seen[start] = true;
                int minX = x, maxX = x, minY = y, maxY = y;
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue();
                    walls.Add(i);
                    int cx = i % width;
                    int cy = i / width;
                    minX = Math.Min(minX, cx);
                    maxX = Math.Max(maxX, cx);
                    minY = Math.Min(minY, cy);
                    maxY = Math.Max(maxY, cy);
                    for (int d = 0; d < 8; d++)
                    {
                        int nx = cx + dx[d];
                        int ny = cy + dy[d];
                        if ((uint)nx >= (uint)width || (uint)ny >= (uint)height)
                        {
                            continue;
                        }

                        int ni = ny * width + nx;
                        if (seen[ni] || !HasEitherWall(map.Tiles[ni]))
                        {
                            continue;
                        }

                        seen[ni] = true;
                        queue.Enqueue(ni);
                    }
                }

                foreach (int i in walls)
                {
                    mark[i] = true;
                }

                var floorQueue = new Queue<int>(walls);
                var floorSeen = new bool[map.Tiles.Length];
                foreach (int i in walls)
                {
                    floorSeen[i] = true;
                }

                while (floorQueue.Count > 0)
                {
                    int i = floorQueue.Dequeue();
                    int cx = i % width;
                    int cy = i / width;
                    for (int d = 0; d < 4; d++)
                    {
                        int nx = cx + dx[d];
                        int ny = cy + dy[d];
                        if (nx < minX || nx > maxX || ny < minY || ny > maxY)
                        {
                            continue;
                        }

                        int ni = ny * width + nx;
                        if (floorSeen[ni])
                        {
                            continue;
                        }

                        floorSeen[ni] = true;
                        MapTile tile = map.Tiles[ni];
                        if (tile.Floor == 0 || tile.Floor == ground)
                        {
                            continue;
                        }

                        mark[ni] = true;
                        floorQueue.Enqueue(ni);
                    }
                }
            }
        }

        return mark;
    }

    private static bool HasEitherWall(MapTile tile) => tile.LeftWall != 0 || tile.RightWall != 0;

    private static ushort MajorityFloor(MapTile[] tiles)
    {
        var counts = new Dictionary<ushort, int>();
        foreach (MapTile tile in tiles)
        {
            if (tile.Floor == 0)
            {
                continue;
            }

            counts[tile.Floor] = counts.GetValueOrDefault(tile.Floor) + 1;
        }

        ushort best = 1;
        int bestCount = 0;
        foreach (KeyValuePair<ushort, int> entry in counts)
        {
            if (entry.Value > bestCount)
            {
                bestCount = entry.Value;
                best = entry.Key;
            }
        }

        return best;
    }

    private static WallStyle PickStyle(IReadOnlyList<WallStyle> styles, Random rng) =>
        styles[rng.Next(styles.Count)];

    private static void LayRoutes(
        MapTile[] tiles,
        bool[] isPath,
        int width,
        int height,
        List<(int X, int Y)> nodes,
        int extra,
        Random rng)
    {
        var points = new List<(int X, int Y)>();
        foreach ((int X, int Y) node in nodes)
        {
            int x = Math.Clamp(node.X, 0, width - 1);
            int y = Math.Clamp(node.Y, 0, height - 1);
            if (CanWalk(tiles, width, height, x, y))
            {
                points.Add((x, y));
            }
        }

        ConnectPoints(tiles, isPath, width, height, points, rng);
        for (int n = 0; n < extra; n++)
        {
            if (!TryBranch(tiles, isPath, width, height, points, rng))
            {
                break;
            }
        }
    }

    private static void ConnectPoints(
        MapTile[] tiles,
        bool[] isPath,
        int width,
        int height,
        List<(int X, int Y)> points,
        Random rng)
    {
        if (points.Count == 0)
        {
            return;
        }

        if (points.Count == 1)
        {
            (int ex, int ey) = EdgePoint(width, height, rng);
            OrganicRoute(tiles, isPath, width, height, points[0].X, points[0].Y, ex, ey, WideRoute(rng), rng);
            return;
        }

        var pending = new List<int>();
        for (int i = 1; i < points.Count; i++)
        {
            pending.Add(i);
        }

        var linked = new List<int> { 0 };
        while (pending.Count > 0)
        {
            int best = 0;
            int from = linked[0];
            int bestDist = int.MaxValue;
            for (int p = 0; p < pending.Count; p++)
            {
                (int px, int py) = points[pending[p]];
                foreach (int link in linked)
                {
                    int dist = Math.Abs(points[link].X - px) + Math.Abs(points[link].Y - py);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = p;
                        from = link;
                    }
                }
            }

            int to = pending[best];
            pending.RemoveAt(best);
            OrganicRoute(
                tiles,
                isPath,
                width,
                height,
                points[from].X,
                points[from].Y,
                points[to].X,
                points[to].Y,
                WideRoute(rng),
                rng);
            linked.Add(to);
        }
    }

    private static bool TryBranch(
        MapTile[] tiles,
        bool[] isPath,
        int width,
        int height,
        List<(int X, int Y)> points,
        Random rng)
    {
        (int x0, int y0) = RandomPathCell(isPath, width, rng)
            ?? RandomWalkable(tiles, width, height, rng)
            ?? (points.Count > 0 ? points[rng.Next(points.Count)] : EdgePoint(width, height, rng));
        int minDist = Math.Max(4, Math.Min(width, height) / 3);
        (int x1, int y1) = rng.Next(100) < 45
            ? EdgePoint(width, height, rng)
            : RandomWalkable(tiles, width, height, rng) ?? EdgePoint(width, height, rng);
        if (Math.Abs(x1 - x0) + Math.Abs(y1 - y0) < minDist)
        {
            (x1, y1) = FarPoint(tiles, width, height, x0, y0, minDist, rng);
        }

        if (x0 == x1 && y0 == y1)
        {
            return false;
        }

        OrganicRoute(tiles, isPath, width, height, x0, y0, x1, y1, WideRoute(rng), rng);
        return true;
    }

    private static bool WideRoute(Random rng) => rng.Next(100) < 74;

    private static void OrganicRoute(
        MapTile[] tiles,
        bool[] isPath,
        int width,
        int height,
        int x0,
        int y0,
        int x1,
        int y1,
        bool wide,
        Random rng)
    {
        int x = Math.Clamp(x0, 0, width - 1);
        int y = Math.Clamp(y0, 0, height - 1);
        int tx = Math.Clamp(x1, 0, width - 1);
        int ty = Math.Clamp(y1, 0, height - 1);
        int side = rng.Next(2) == 0 ? 1 : -1;
        int guard = Math.Max(12, (width + height) * 4);
        int prevOx = 0;
        int prevOy = 0;
        bool moved = false;
        bool jogged = false;
        MarkPath(tiles, isPath, width, x, y);

        while ((x != tx || y != ty) && guard-- > 0)
        {
            int dx = tx - x;
            int dy = ty - y;
            bool alongX = dx != 0 && (dy == 0 || PreferHorizontal(dx, dy, rng));
            int dir = alongX ? Math.Sign(dx) : Math.Sign(dy);
            if (dir == 0)
            {
                break;
            }

            int ox = alongX ? 0 : side;
            int oy = alongX ? side : 0;
            int remain = alongX ? Math.Abs(dx) : Math.Abs(dy);
            int thick = wide && rng.Next(100) >= 18 ? 2 : 1;
            int run = remain <= 2 ? remain : rng.Next(2, Math.Min(remain, 5) + 1);
            int jogChance = dx == 0 || dy == 0 ? 46 : 24;
            if (!jogged && moved && remain > 3 && rng.Next(100) < jogChance)
            {
                int jx = alongX ? 0 : side;
                int jy = alongX ? side : 0;
                if (!CanWalk(tiles, width, height, x + jx, y + jy))
                {
                    jx = -jx;
                    jy = -jy;
                }

                if (CanWalk(tiles, width, height, x + jx, y + jy))
                {
                    x += jx;
                    y += jy;
                    MarkRibbon(tiles, isPath, width, height, x, y, thick, ox, oy);
                    moved = true;
                    jogged = true;
                }
            }
            else
            {
                jogged = false;
            }

            if (moved && (ox != prevOx || oy != prevOy))
            {
                MarkRibbon(tiles, isPath, width, height, x, y, Math.Max(thick, 2), prevOx, prevOy);
            }

            bool stepped = false;
            for (int s = 0; s < run; s++)
            {
                int nx = x + (alongX ? dir : 0);
                int ny = y + (alongX ? 0 : dir);
                if (!CanWalk(tiles, width, height, nx, ny))
                {
                    int sx = alongX ? 0 : side;
                    int sy = alongX ? side : 0;
                    if (!CanWalk(tiles, width, height, x + sx, y + sy))
                    {
                        sx = -sx;
                        sy = -sy;
                    }

                    if (!CanWalk(tiles, width, height, x + sx, y + sy))
                    {
                        break;
                    }

                    x += sx;
                    y += sy;
                    MarkRibbon(tiles, isPath, width, height, x, y, thick, ox, oy);
                    stepped = true;
                    moved = true;
                    break;
                }

                x = nx;
                y = ny;
                MarkRibbon(tiles, isPath, width, height, x, y, thick, ox, oy);
                stepped = true;
                moved = true;
                if (x == tx && y == ty)
                {
                    break;
                }
            }

            prevOx = ox;
            prevOy = oy;
            if (!stepped)
            {
                side = -side;
                if (!CanWalk(tiles, width, height, x + 1, y)
                    && !CanWalk(tiles, width, height, x - 1, y)
                    && !CanWalk(tiles, width, height, x, y + 1)
                    && !CanWalk(tiles, width, height, x, y - 1))
                {
                    break;
                }
            }
        }
    }

    private static bool PreferHorizontal(int dx, int dy, Random rng)
    {
        int bias = Math.Abs(dx) >= Math.Abs(dy) ? 70 : 30;
        return rng.Next(100) < bias;
    }

    private static void MarkRibbon(
        MapTile[] tiles,
        bool[] isPath,
        int width,
        int height,
        int x,
        int y,
        int thick,
        int ox,
        int oy)
    {
        MarkPath(tiles, isPath, width, x, y);
        if (thick < 2 || (ox == 0 && oy == 0))
        {
            return;
        }

        if (CanWalk(tiles, width, height, x + ox, y + oy))
        {
            MarkPath(tiles, isPath, width, x + ox, y + oy);
        }
        else if (CanWalk(tiles, width, height, x - ox, y - oy))
        {
            MarkPath(tiles, isPath, width, x - ox, y - oy);
        }
    }

    private static (int X, int Y) EdgePoint(int width, int height, Random rng)
    {
        return rng.Next(4) switch
        {
            0 => (rng.Next(width), 0),
            1 => (rng.Next(width), height - 1),
            2 => (0, rng.Next(height)),
            _ => (width - 1, rng.Next(height))
        };
    }

    private static (int X, int Y)? RandomPathCell(bool[] isPath, int width, Random rng)
    {
        int count = 0;
        int pick = 0;
        for (int i = 0; i < isPath.Length; i++)
        {
            if (!isPath[i])
            {
                continue;
            }

            count++;
            if (rng.Next(count) == 0)
            {
                pick = i;
            }
        }

        return count == 0 ? null : (pick % width, pick / width);
    }

    private static (int X, int Y)? RandomWalkable(MapTile[] tiles, int width, int height, Random rng)
    {
        for (int n = 0; n < 24; n++)
        {
            int x = rng.Next(width);
            int y = rng.Next(height);
            if (CanWalk(tiles, width, height, x, y))
            {
                return (x, y);
            }
        }

        return null;
    }

    private static (int X, int Y) FarPoint(
        MapTile[] tiles,
        int width,
        int height,
        int x0,
        int y0,
        int minDist,
        Random rng)
    {
        (int X, int Y) best = EdgePoint(width, height, rng);
        int bestDist = Math.Abs(best.X - x0) + Math.Abs(best.Y - y0);
        for (int n = 0; n < 16; n++)
        {
            (int X, int Y) candidate = rng.Next(2) == 0
                ? EdgePoint(width, height, rng)
                : RandomWalkable(tiles, width, height, rng) ?? EdgePoint(width, height, rng);
            int dist = Math.Abs(candidate.X - x0) + Math.Abs(candidate.Y - y0);
            if (dist > bestDist)
            {
                best = candidate;
                bestDist = dist;
            }

            if (dist >= minDist && CanWalk(tiles, width, height, candidate.X, candidate.Y))
            {
                return candidate;
            }
        }

        return best;
    }

    private static bool CanWalk(MapTile[] tiles, int width, int height, int x, int y)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height)
        {
            return false;
        }

        return PathWalkable(tiles[y * width + x]);
    }

    private static List<(int X, int Y)> StructureAnchors(MapTile[] tiles, int width, int height)
    {
        var seen = new bool[tiles.Length];
        var nodes = new List<(int X, int Y)>();
        int[] dx = [1, -1, 0, 0];
        int[] dy = [0, 0, 1, -1];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int start = y * width + x;
                if (seen[start] || !HasWall(tiles[start]))
                {
                    continue;
                }

                int doorX = x;
                int doorY = y;
                bool foundDoor = false;
                var queue = new Queue<int>();
                queue.Enqueue(start);
                seen[start] = true;
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue();
                    int cx = i % width;
                    int cy = i / width;
                    for (int d = 0; d < 4; d++)
                    {
                        int nx = cx + dx[d];
                        int ny = cy + dy[d];
                        if ((uint)nx >= (uint)width || (uint)ny >= (uint)height)
                        {
                            continue;
                        }

                        int ni = ny * width + nx;
                        if (HasWall(tiles[ni]))
                        {
                            if (!seen[ni])
                            {
                                seen[ni] = true;
                                queue.Enqueue(ni);
                            }

                            continue;
                        }

                        if (!foundDoor && PathWalkable(tiles[ni]))
                        {
                            doorX = nx;
                            doorY = ny;
                            foundDoor = true;
                        }
                    }
                }

                if (foundDoor)
                {
                    nodes.Add((doorX, doorY));
                }
            }
        }

        return nodes;
    }

    private static bool HasWall(MapTile tile) => tile.LeftWall != 0 || tile.RightWall != 0;

    private static bool PathWalkable(MapTile tile) =>
        tile.Floor != 0 && tile.LeftWall == 0 && tile.RightWall == 0;

    private static void MarkPath(MapTile[] tiles, bool[] isPath, int width, int x, int y)
    {
        if ((uint)x >= (uint)width || y < 0)
        {
            return;
        }

        int i = y * width + x;
        if ((uint)i < (uint)tiles.Length && PathWalkable(tiles[i]))
        {
            isPath[i] = true;
        }
    }

    private static void PaintPathFloors(
        MapTile[] tiles,
        bool[] isPath,
        ushort path,
        IReadOnlyList<ushort> likes,
        Random rng,
        bool keepWalls)
    {
        _ = likes;
        _ = rng;
        for (int i = 0; i < tiles.Length; i++)
        {
            if (!isPath[i])
            {
                continue;
            }

            MapTile tile = tiles[i];
            if (tile.Floor == 0)
            {
                continue;
            }

            if (keepWalls && (tile.LeftWall != 0 || tile.RightWall != 0))
            {
                continue;
            }

            tiles[i] = new MapTile(path, keepWalls ? tile.LeftWall : (ushort)0, keepWalls ? tile.RightWall : (ushort)0);
        }
    }

    private static bool Fits(int width, int height, int x0, int y0, int x1, int y1, bool[] isPath, bool[] blocked)
    {
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                if ((uint)x >= (uint)width || (uint)y >= (uint)height)
                {
                    return false;
                }

                int i = y * width + x;
                if (blocked[i] || (isPath.Length > i && isPath[i]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void MarkRect(bool[] blocked, int width, int x0, int y0, int x1, int y1)
    {
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                if (x < 0 || y < 0)
                {
                    continue;
                }

                int i = y * width + x;
                if (i >= 0 && i < blocked.Length)
                {
                    blocked[i] = true;
                }
            }
        }
    }

    private static void ClearFoliage(MapTile[] tiles, int width, int height)
    {
        var seen = new bool[tiles.Length];
        int[] dx = [1, -1, 0, 0, 1, 1, -1, -1];
        int[] dy = [0, 0, 1, -1, 1, -1, 1, -1];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int start = y * width + x;
                if (seen[start] || !HasWall(tiles[start]))
                {
                    continue;
                }

                var cells = new List<int>();
                var queue = new Queue<int>();
                queue.Enqueue(start);
                seen[start] = true;
                int minX = x, maxX = x, minY = y, maxY = y;
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue();
                    cells.Add(i);
                    int cx = i % width;
                    int cy = i / width;
                    minX = Math.Min(minX, cx);
                    maxX = Math.Max(maxX, cx);
                    minY = Math.Min(minY, cy);
                    maxY = Math.Max(maxY, cy);
                    for (int d = 0; d < 8; d++)
                    {
                        int nx = cx + dx[d];
                        int ny = cy + dy[d];
                        if ((uint)nx >= (uint)width || (uint)ny >= (uint)height)
                        {
                            continue;
                        }

                        int ni = ny * width + nx;
                        if (seen[ni] || !HasWall(tiles[ni]))
                        {
                            continue;
                        }

                        seen[ni] = true;
                        queue.Enqueue(ni);
                    }
                }

                int w = maxX - minX + 1;
                int h = maxY - minY + 1;
                if (!IsFoliageComponent(tiles, width, cells, w, h, minX, minY, maxX, maxY))
                {
                    continue;
                }

                foreach (int i in cells)
                {
                    MapTile tile = tiles[i];
                    tiles[i] = new MapTile(tile.Floor, 0, 0);
                }
            }
        }
    }

    private static bool IsFoliageComponent(
        MapTile[] tiles,
        int width,
        List<int> cells,
        int w,
        int h,
        int minX,
        int minY,
        int maxX,
        int maxY)
    {
        if (w > 4 || h > 4 || cells.Count > 10)
        {
            return false;
        }

        int edgeTiles = 0;
        int edgeWalls = 0;
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                bool edge = x == minX || y == minY || x == maxX || y == maxY;
                if (!edge)
                {
                    continue;
                }

                edgeTiles++;
                if (HasWall(tiles[y * width + x]))
                {
                    edgeWalls++;
                }
            }
        }

        bool enclosed = w >= 3 && h >= 2 && cells.Count >= 6 && edgeTiles > 0 && edgeWalls >= edgeTiles * 0.55;
        return !enclosed;
    }

    private static bool IsFoliageBlocked(MapTile tile, ushort ground, FloorFamily? groundFamily, ushort pathFloor)
    {
        if (tile.Floor == 0 || HasWall(tile))
        {
            return true;
        }

        if (pathFloor > 1 && tile.Floor == pathFloor)
        {
            return true;
        }

        if (tile.Floor == ground)
        {
            return false;
        }

        if (groundFamily is FloorFamily family)
        {
            return !family.Contains(tile.Floor);
        }

        return Math.Abs(tile.Floor - ground) > 2;
    }

    private static bool TryFoliageOrigin(int width, int height, StructureStamp stamp, Random rng, out int x0, out int y0)
    {
        int maxX = width - stamp.Width;
        int maxY = height - stamp.Height;
        if (maxX < 0 || maxY < 0)
        {
            x0 = 0;
            y0 = 0;
            return false;
        }

        x0 = rng.Next(maxX + 1);
        y0 = rng.Next(maxY + 1);
        return true;
    }

    private static bool FoliageFits(
        MapTile[] tiles,
        bool[] forbidden,
        bool[] occupied,
        int width,
        int height,
        StructureStamp stamp,
        int x0,
        int y0,
        ushort ground,
        FloorFamily? groundFamily,
        ushort pathFloor)
    {
        bool any = false;
        for (int y = 0; y < stamp.Height; y++)
        {
            for (int x = 0; x < stamp.Width; x++)
            {
                MapTile cell = stamp.Cells[y * stamp.Width + x];
                if (cell.LeftWall == 0 && cell.RightWall == 0)
                {
                    continue;
                }

                int mx = x0 + x;
                int my = y0 + y;
                if ((uint)mx >= (uint)width || (uint)my >= (uint)height)
                {
                    return false;
                }

                int i = my * width + mx;
                if (forbidden[i] || occupied[i] || IsFoliageBlocked(tiles[i], ground, groundFamily, pathFloor))
                {
                    return false;
                }

                any = true;
            }
        }

        return any;
    }

    private static void MarkFoliage(bool[] occupied, int width, int height, StructureStamp stamp, int x0, int y0)
    {
        for (int y = -1; y <= stamp.Height; y++)
        {
            for (int x = -1; x <= stamp.Width; x++)
            {
                int mx = x0 + x;
                int my = y0 + y;
                if ((uint)mx >= (uint)width || (uint)my >= (uint)height)
                {
                    continue;
                }

                occupied[my * width + mx] = true;
            }
        }
    }

    public static void PlaceStamp(
        MapTile[] tiles,
        int mapWidth,
        int originX,
        int originY,
        StructureStamp stamp,
        WallStyle style,
        IReadOnlyCollection<int> knownWalls,
        ushort floor,
        IReadOnlyList<ushort> likes,
        Random rng,
        bool keepFloors,
        bool preserveWalls = false)
    {
        likes ??= [];
        for (int y = 0; y < stamp.Height; y++)
        {
            for (int x = 0; x < stamp.Width; x++)
            {
                MapTile cell = stamp.Cells[y * stamp.Width + x];
                if (cell.Floor == 0 && cell.LeftWall == 0 && cell.RightWall == 0)
                {
                    continue;
                }

                if (!stamp.CopyFloors && cell.LeftWall == 0 && cell.RightWall == 0)
                {
                    continue;
                }

                int mx = originX + x;
                int my = originY + y;
                if ((uint)mx >= (uint)mapWidth)
                {
                    continue;
                }

                int i = my * mapWidth + mx;
                if ((uint)i >= (uint)tiles.Length)
                {
                    continue;
                }

                ushort ground = keepFloors
                    ? tiles[i].Floor
                    : likes.Count > 0 ? likes[rng.Next(likes.Count)] : floor;
                if (stamp.CopyFloors && cell.Floor != 0)
                {
                    ground = cell.Floor;
                }

                ushort left = preserveWalls
                    ? cell.LeftWall
                    : Restyle(cell.LeftWall, style, knownWalls, left: true, cell.RightWall != 0);
                ushort right = preserveWalls
                    ? cell.RightWall
                    : Restyle(cell.RightWall, style, knownWalls, left: false, cell.LeftWall != 0);
                tiles[i] = new MapTile(ground, left, right);
            }
        }
    }

    public static (int X, int Y) DoorAnchor(int originX, int originY, StructureStamp stamp, int mapWidth, int mapHeight)
    {
        for (int y = stamp.Height - 1; y >= 0; y--)
        {
            for (int x = 0; x < stamp.Width; x++)
            {
                MapTile cell = stamp.Cells[y * stamp.Width + x];
                if (cell.LeftWall != 0 || cell.RightWall != 0)
                {
                    continue;
                }

                int dx = Math.Clamp(originX + x, 0, mapWidth - 1);
                int dy = Math.Clamp(originY + y + 1, 0, mapHeight - 1);
                return (dx, dy);
            }
        }

        return (
            Math.Clamp(originX + stamp.Width / 2, 0, mapWidth - 1),
            Math.Clamp(originY + stamp.Height, 0, mapHeight - 1));
    }

    private static ushort Restyle(ushort id, WallStyle style, IReadOnlyCollection<int> known, bool left, bool corner)
    {
        if (id == 0)
        {
            return 0;
        }

        if (known.Count == 0 || known.Contains(id))
        {
            return id;
        }

        if (corner)
        {
            return left ? style.CornerLeft : style.CornerRight;
        }

        return left ? style.EdgeLeft : style.EdgeRight;
    }

    public static StructureStamp CreateKit(WallStyle walls, int width, int height)
    {
        width = Math.Clamp(width, 3, 8);
        height = Math.Clamp(height, 2, 6);
        var tiles = new MapTile[width * height];
        for (int i = 0; i < tiles.Length; i++)
        {
            tiles[i] = new MapTile(1, 0, 0);
        }

        int doorX = Math.Max(1, width / 2);
        StampBuilding(tiles, width, 0, 0, width - 1, height - 1, doorX, walls, keepFloors: true);
        for (int i = 0; i < tiles.Length; i++)
        {
            tiles[i] = new MapTile(0, tiles[i].LeftWall, tiles[i].RightWall);
        }

        return new StructureStamp(
            $"kit-{walls.CornerLeft}-{walls.CornerRight}-{width}x{height}",
            $"Kit L{walls.CornerLeft}/R{walls.CornerRight}  {width}×{height}",
            width,
            height,
            tiles,
            0);
    }

    public static void StampBuilding(
        MapTile[] tiles,
        int width,
        int x0,
        int y0,
        int x1,
        int y1,
        int doorX,
        WallStyle walls,
        ushort floor = 1,
        IReadOnlyList<ushort>? likes = null,
        Random? rng = null,
        bool keepFloors = false)
    {
        likes ??= [];
        rng ??= Random.Shared;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                int i = y * width + x;
                ushort ground = keepFloors
                    ? tiles[i].Floor
                    : likes.Count > 0 ? likes[rng.Next(likes.Count)] : floor;
                bool west = x == x0;
                bool east = x == x1;
                bool north = y == y0;
                bool south = y == y1;
                bool door = south && x == doorX && x > x0 && x < x1;
                int delta = north || walls.BodyDelta == 0 ? 0 : walls.BodyDelta;
                ushort spanA = OffsetWall(walls.SpanLeft, delta);
                ushort spanB = OffsetWall(walls.SpanLeft2, delta);
                ushort pairL = OffsetWall(walls.CornerLeft, delta);
                ushort pairR = OffsetWall(walls.CornerRight, delta);
                ushort left;
                ushort right;
                if (door)
                {
                    left = 0;
                    right = 0;
                }
                else if (west || east)
                {
                    if (north || south)
                    {
                        left = pairL;
                        right = pairR;
                    }
                    else
                    {
                        left = 0;
                        right = walls.EdgeRight;
                    }
                }
                else
                {
                    int phase = (x - x0) % 3;
                    if (phase == 0)
                    {
                        left = spanA;
                        right = 0;
                    }
                    else if (phase == 1)
                    {
                        left = spanB;
                        right = 0;
                    }
                    else
                    {
                        left = pairL;
                        right = pairR;
                    }
                }

                tiles[i] = new MapTile(ground, left, right);
            }
        }
    }

    private static ushort OffsetWall(ushort id, int delta)
    {
        int next = id + delta;
        return next is > 2 and < 65536 ? (ushort)next : id;
    }
}
