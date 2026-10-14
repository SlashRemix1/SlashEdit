using System.Collections.Concurrent;
using System.IO;
using DarkAges.Formats.Maps;

namespace DarkAges.Viewer.Maps;

public sealed class MapCatalogStats
{
    private static readonly object Gate = new();
    private static MapCatalogStats? _cache;
    private static string _cacheKey = "";

    private readonly Dictionary<(ushort L, ushort R), int> _pairs = new();
    private readonly Dictionary<ushort, int> _leftOnly = new();
    private readonly Dictionary<ushort, int> _rightOnly = new();
    private readonly Dictionary<(ushort A, ushort B), int> _floorAdj = new();
    private readonly Dictionary<ushort, int> _floors = new();
    private readonly Dictionary<int, ushort> _pathTileByFamily = new();
    private List<FloorFamily> _families = FloorFamilies.Seeds.ToList();
    private readonly Dictionary<string, StructureAcc> _structureAcc = new();
    private readonly Dictionary<(ushort L, ushort R), int> _treeSprites = new();
    private readonly Dictionary<(ushort L, ushort R), (ushort SpanA, ushort SpanB, int Count)> _spans = new();

    public int MapsScanned { get; private set; }
    public int MapsSkipped { get; private set; }
    public int PairKinds => _pairs.Count;
    public IReadOnlyList<FloorFamily> FloorFamiliesFound => _families;

    public FloorFamily? FamilyOf(int floorId) => FloorFamilies.Find(floorId, _families);

    public string DescribeFloors(int groundId, int pathId) =>
        FloorFamilies.Describe(groundId, pathId, _families);

    /// <summary>
    /// Path tile whose family most often borders this ground family on scanned maps.
    /// </summary>
    public ushort ResolveFamilyPath(int groundId, int floorCount)
    {
        FloorFamily? ground = FamilyOf(groundId);
        if (ground is null)
        {
            return 0;
        }

        if (_pathTileByFamily.TryGetValue(ground.Value.Min, out ushort tile)
            && tile != groundId
            && tile > 1
            && (floorCount == 0 || tile <= floorCount))
        {
            return tile;
        }

        return 0;
    }
    public IReadOnlyList<StructureStamp> Structures { get; private set; } = [];
    public IReadOnlyList<(ushort Left, ushort Right, int Count)> TreeSprites { get; private set; } = [];

    public static MapCatalogStats Load(IEnumerable<string> folders)
    {
        string key = string.Join("|", folders.Select(Path.GetFullPath).OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
        lock (Gate)
        {
            if (_cache is not null && _cacheKey == key)
            {
                return _cache;
            }
        }

        var stats = new MapCatalogStats();
        stats.Scan(folders);
        stats.CompileStructures();
        lock (Gate)
        {
            _cache = stats;
            _cacheKey = key;
        }

        return stats;
    }

    public WallStyle ResolveWalls(int seedWall, IReadOnlyCollection<int> knownWalls)
    {
        ushort seed = (ushort)Math.Max(0, seedWall);
        (ushort l, ushort r) = BestPairFor(seed);
        return StyleFromPair(l, r, knownWalls);
    }

    public IReadOnlyList<WallStyle> PickWallStyles(
        int seedWall,
        IReadOnlyCollection<int> knownWalls,
        Random rng,
        int count)
    {
        count = Math.Max(1, count);
        var scored = new List<((ushort L, ushort R) Pair, double Score)>();
        ushort seed = (ushort)Math.Max(0, seedWall);
        foreach (KeyValuePair<(ushort L, ushort R), int> entry in _pairs)
        {
            if (!IsUsableWall(entry.Key.L) || !IsUsableWall(entry.Key.R))
            {
                continue;
            }

            if (knownWalls.Count > 0 &&
                (!knownWalls.Contains(entry.Key.L) || !knownWalls.Contains(entry.Key.R)))
            {
                continue;
            }

            int dist = seed <= 2
                ? 0
                : Math.Min(Math.Abs(entry.Key.L - seed), Math.Abs(entry.Key.R - seed));
            double family = dist < 48 ? 2.4 : 1.0;
            double jitter = 0.7 + rng.NextDouble() * 0.6;
            scored.Add((entry.Key, entry.Value * family * jitter));
        }

        var picked = new List<(ushort L, ushort R)>();
        foreach (((ushort L, ushort R) Pair, double Score) item in scored.OrderByDescending(s => s.Score))
        {
            if (picked.Any(p => Math.Abs(p.L - item.Pair.L) < 48 && Math.Abs(p.R - item.Pair.R) < 48))
            {
                continue;
            }

            picked.Add(item.Pair);
            if (picked.Count >= count)
            {
                break;
            }
        }

        if (picked.Count < count)
        {
            foreach (((ushort L, ushort R) Pair, double Score) item in scored.OrderByDescending(s => s.Score))
            {
                if (picked.Contains(item.Pair))
                {
                    continue;
                }

                picked.Add(item.Pair);
                if (picked.Count >= count)
                {
                    break;
                }
            }
        }

        if (picked.Count == 0)
        {
            if (knownWalls.Count > 0)
            {
                List<int> ids = knownWalls.Where(IsUsableWall).OrderBy(_ => rng.Next()).Take(count).ToList();
                if (ids.Count == 0)
                {
                    return [ResolveWalls(seedWall, knownWalls)];
                }

                return ids.Select(id => ResolveWalls(id, knownWalls)).ToList();
            }

            return [ResolveWalls(seedWall, knownWalls)];
        }

        return picked.Select(p => StyleFromPair(p.L, p.R, knownWalls)).ToList();
    }

    public IReadOnlyList<WallStyle> ListPartnerStyles(IReadOnlyCollection<int> knownWalls, int max = 96)
    {
        max = Math.Clamp(max, 8, 160);
        var known = knownWalls.Count > 0
            ? knownWalls.Where(IsUsableWall).ToHashSet()
            : [];
        var leftUses = new Dictionary<ushort, int>(_leftOnly);
        var rightUses = new Dictionary<ushort, int>(_rightOnly);
        foreach (KeyValuePair<(ushort L, ushort R), int> entry in _pairs)
        {
            leftUses[entry.Key.L] = leftUses.GetValueOrDefault(entry.Key.L) + entry.Value;
            rightUses[entry.Key.R] = rightUses.GetValueOrDefault(entry.Key.R) + entry.Value;
        }

        var scored = new Dictionary<(ushort L, ushort R), int>();
        void Consider(ushort left, ushort right, int score)
        {
            if (!IsUsableWall(left) || !IsUsableWall(right) || left == right)
            {
                return;
            }

            if (known.Count > 0 && (!known.Contains(left) || !known.Contains(right)))
            {
                return;
            }

            (ushort L, ushort R) key = (left, right);
            scored[key] = Math.Max(scored.GetValueOrDefault(key), score);
        }

        foreach (KeyValuePair<(ushort L, ushort R), int> entry in _pairs)
        {
            Consider(entry.Key.L, entry.Key.R, 100 + entry.Value);
        }

        foreach (KeyValuePair<ushort, int> left in _leftOnly)
        {
            ushort right = (ushort)(left.Key + 1);
            int rightScore = rightUses.GetValueOrDefault(right);
            if (rightScore > 0 || known.Contains(right))
            {
                Consider(left.Key, right, 40 + left.Value + rightScore);
            }
        }

        var claimed = new HashSet<int>();
        foreach (int id in known.OrderBy(n => n))
        {
            if (claimed.Contains(id))
            {
                continue;
            }

            int next = id + 1;
            if (!known.Contains(next))
            {
                continue;
            }

            int leftA = leftUses.GetValueOrDefault((ushort)id);
            int rightA = rightUses.GetValueOrDefault((ushort)id);
            int leftB = leftUses.GetValueOrDefault((ushort)next);
            int rightB = rightUses.GetValueOrDefault((ushort)next);
            if (rightA > leftA * 2 && rightA >= 3)
            {
                continue;
            }

            if (leftB > rightB * 2 && leftB >= 3)
            {
                continue;
            }

            Consider((ushort)id, (ushort)next, 12 + leftA + rightB);
            claimed.Add(id);
            claimed.Add(next);
        }

        return scored
            .OrderByDescending(e => e.Value)
            .ThenBy(e => e.Key.L)
            .Take(max)
            .Select(e => StyleFromPair(e.Key.L, e.Key.R, knownWalls))
            .ToList();
    }

    private WallStyle StyleFromPair(ushort l, ushort r, IReadOnlyCollection<int> knownWalls)
    {
        if (!IsUsableWall(l) || (knownWalls.Count > 0 && !knownWalls.Contains(l)))
        {
            l = FirstKnown(knownWalls, 4209, 745, 5048);
        }

        if (!IsUsableWall(r) || (knownWalls.Count > 0 && !knownWalls.Contains(r)))
        {
            r = (ushort)(knownWalls.Contains(l + 1) ? l + 1 : l);
        }

        ushort edgeL = PickRelated(_leftOnly, l, l);
        ushort edgeR = PickRelated(_rightOnly, r, r);
        ushort spanA = PickRelated(_leftOnly, (ushort)Math.Max(1, l - 2), edgeL);
        ushort spanB = edgeL;
        if (_spans.TryGetValue((l, r), out (ushort SpanA, ushort SpanB, int Count) span) && span.Count > 0)
        {
            spanA = span.SpanA;
            spanB = span.SpanB;
        }
        else if (spanA == spanB)
        {
            spanA = (ushort)(l > 2 ? l - 2 : l);
        }

        if (knownWalls.Count > 0)
        {
            if (!knownWalls.Contains(edgeL))
            {
                edgeL = l;
                spanB = l;
            }

            if (!knownWalls.Contains(edgeR))
            {
                edgeR = r;
            }

            if (!knownWalls.Contains(spanA))
            {
                spanA = spanB;
            }
        }

        int body = 0;
        foreach (int delta in new[] { 8, 6, 10, 4, 12 })
        {
            if (_pairs.ContainsKey(((ushort)(l + delta), (ushort)(r + delta))))
            {
                body = delta;
                break;
            }
        }

        return new WallStyle(l, r, edgeL, edgeR, spanA, spanB, body);
    }

    public ushort ResolvePathFloor(int seedFloor, int floorCount)
    {
        ushort seed = (ushort)Math.Max(1, seedFloor);
        ushort best = 0;
        int bestScore = 0;
        foreach (KeyValuePair<(ushort A, ushort B), int> entry in _floorAdj)
        {
            if (entry.Key.A != seed && entry.Key.B != seed)
            {
                continue;
            }

            ushort other = entry.Key.A == seed ? entry.Key.B : entry.Key.A;
            if (other == seed || other == 0 || (floorCount > 0 && other > floorCount))
            {
                continue;
            }

            if (entry.Value > bestScore)
            {
                bestScore = entry.Value;
                best = other;
            }
        }

        if (best != 0)
        {
            return best;
        }

        int next = seed + 1;
        return (ushort)(floorCount == 0 || next <= floorCount ? next : seed);
    }

    public IReadOnlyList<ushort> LikeFloors(int seedFloor, int floorCount, int take = 6)
    {
        ushort seed = (ushort)Math.Max(1, seedFloor);
        var scored = new List<(ushort Id, int Score)>();
        foreach (KeyValuePair<(ushort A, ushort B), int> entry in _floorAdj)
        {
            if (entry.Key.A != seed && entry.Key.B != seed)
            {
                continue;
            }

            ushort other = entry.Key.A == seed ? entry.Key.B : entry.Key.A;
            if (other == 0 || other == seed || (floorCount > 0 && other > floorCount))
            {
                continue;
            }

            scored.Add((other, entry.Value));
        }

        List<ushort> ids = scored.OrderByDescending(s => s.Score).Select(s => s.Id).Distinct().Take(take).ToList();
        if (!ids.Contains(seed))
        {
            ids.Insert(0, seed);
        }

        for (int d = 1; d <= 3; d++)
        {
            int lo = seed - d;
            int hi = seed + d;
            if (lo >= 1 && (floorCount == 0 || lo <= floorCount) && !ids.Contains((ushort)lo))
            {
                ids.Add((ushort)lo);
            }

            if (floorCount == 0 || hi <= floorCount)
            {
                if (!ids.Contains((ushort)hi))
                {
                    ids.Add((ushort)hi);
                }
            }
        }

        return ids;
    }

    private (ushort L, ushort R) BestPairFor(ushort seed)
    {
        int best = 0;
        (ushort L, ushort R) pair = (0, 0);
        foreach (KeyValuePair<(ushort L, ushort R), int> entry in _pairs)
        {
            if (seed != 0 && entry.Key.L != seed && entry.Key.R != seed)
            {
                continue;
            }

            if (!IsUsableWall(entry.Key.L) || !IsUsableWall(entry.Key.R))
            {
                continue;
            }

            if (entry.Value > best)
            {
                best = entry.Value;
                pair = entry.Key;
            }
        }

        if (best > 0)
        {
            return pair;
        }

        if (IsUsableWall(seed))
        {
            return (seed, (ushort)(seed + 1));
        }

        foreach (KeyValuePair<(ushort L, ushort R), int> entry in _pairs.OrderByDescending(p => p.Value))
        {
            if (IsUsableWall(entry.Key.L) && IsUsableWall(entry.Key.R))
            {
                return entry.Key;
            }
        }

        return (4209, 4210);
    }

    private static ushort PickRelated(Dictionary<ushort, int> counts, ushort near, ushort fallback)
    {
        ushort best = fallback;
        double bestScore = 0;
        foreach (KeyValuePair<ushort, int> entry in counts)
        {
            if (!IsUsableWall(entry.Key))
            {
                continue;
            }

            double score = entry.Value / (1.0 + Math.Abs(entry.Key - near));
            if (score > bestScore)
            {
                bestScore = score;
                best = entry.Key;
            }
        }

        return best;
    }

    private static ushort FirstKnown(IReadOnlyCollection<int> known, params int[] preferred)
    {
        foreach (int id in preferred)
        {
            if (known.Contains(id))
            {
                return (ushort)id;
            }
        }

        foreach (int id in known)
        {
            if (IsUsableWall(id))
            {
                return (ushort)id;
            }
        }

        return 4209;
    }

    public static bool IsUsableWall(int id) => id > 2 && id % 10000 > 1;

    /// <summary>
    /// Storage / joke maps that plaster the same 3–5 wall ids across most of the grid.
    /// Those clusters are not usable building presets.
    /// </summary>
    public static bool IsLowDiversityWallSpam(MapFile map)
    {
        var counts = new Dictionary<ushort, int>();
        int wallTiles = 0;
        int minX = map.Width;
        int minY = map.Height;
        int maxX = -1;
        int maxY = -1;
        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                MapTile tile = map[x, y];
                bool hit = false;
                if (IsUsableWall(tile.LeftWall))
                {
                    counts[tile.LeftWall] = counts.GetValueOrDefault(tile.LeftWall) + 1;
                    hit = true;
                }

                if (IsUsableWall(tile.RightWall))
                {
                    counts[tile.RightWall] = counts.GetValueOrDefault(tile.RightWall) + 1;
                    hit = true;
                }

                if (!hit)
                {
                    continue;
                }

                wallTiles++;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }

        if (wallTiles < 50 || counts.Count == 0 || counts.Count > 5)
        {
            return false;
        }

        int placed = counts.Values.Sum();
        int top4 = counts.Values.OrderByDescending(v => v).Take(4).Sum();
        if (top4 < placed * 0.90)
        {
            return false;
        }

        int bbox = (maxX - minX + 1) * (maxY - minY + 1);
        int area = Math.Max(1, map.Width * map.Height);
        return bbox >= area * 0.40;
    }

    private void Scan(IEnumerable<string> folders)
    {
        foreach (string folder in folders)
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*.map", SearchOption.AllDirectories).ToList();
            }
            catch
            {
                continue;
            }

            var loaded = new ConcurrentBag<MapFile>();
            Parallel.ForEach(files, path =>
            {
                try
                {
                    loaded.Add(MapFile.Load(path));
                }
                catch
                {
                    // skip unreadable copies
                }
            });

            foreach (MapFile map in loaded)
            {
                MapsScanned++;
                if (IsLowDiversityWallSpam(map))
                {
                    MapsSkipped++;
                    continue;
                }

                Ingest(map);
            }
        }
    }

    private void Ingest(MapFile map)
    {
        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                MapTile tile = map[x, y];
                if (tile.Floor != 0)
                {
                    Add(_floors, tile.Floor);
                }

                if (tile.LeftWall != 0 && tile.RightWall != 0)
                {
                    (ushort L, ushort R) key = (tile.LeftWall, tile.RightWall);
                    _pairs[key] = _pairs.GetValueOrDefault(key) + 1;
                    if (x >= 2)
                    {
                        MapTile west2 = map[x - 2, y];
                        MapTile west1 = map[x - 1, y];
                        if (west2.LeftWall != 0 && west2.RightWall == 0
                            && west1.LeftWall != 0 && west1.RightWall == 0)
                        {
                            if (_spans.TryGetValue(key, out (ushort SpanA, ushort SpanB, int Count) existing)
                                && existing.SpanA == west2.LeftWall && existing.SpanB == west1.LeftWall)
                            {
                                _spans[key] = (existing.SpanA, existing.SpanB, existing.Count + 1);
                            }
                            else if (!_spans.ContainsKey(key) || _spans[key].Count <= 1)
                            {
                                _spans[key] = (west2.LeftWall, west1.LeftWall, 1);
                            }
                        }
                    }
                }
                else if (tile.LeftWall != 0)
                {
                    Add(_leftOnly, tile.LeftWall);
                }
                else if (tile.RightWall != 0)
                {
                    Add(_rightOnly, tile.RightWall);
                }

                if (x + 1 < map.Width)
                {
                    AddAdj(tile.Floor, map[x + 1, y].Floor);
                }

                if (y + 1 < map.Height)
                {
                    AddAdj(tile.Floor, map[x, y + 1].Floor);
                }
            }
        }

        IngestStructures(map);
        IngestPaths(map);
    }

    private void IngestStructures(MapFile map)
    {
        var seen = new bool[map.Tiles.Length];
        int[] dx = [1, -1, 0, 0, 1, 1, -1, -1];
        int[] dy = [0, 0, 1, -1, 1, -1, 1, -1];
        string source = Path.GetFileNameWithoutExtension(map.Name ?? "") is { Length: > 0 } name ? name : "map";
        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                int start = y * map.Width + x;
                if (seen[start] || !HasWall(map.Tiles[start]))
                {
                    continue;
                }

                var cells = new List<(int X, int Y)>();
                var queue = new Queue<int>();
                queue.Enqueue(start);
                seen[start] = true;
                int minX = x, maxX = x, minY = y, maxY = y;
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue();
                    int cx = i % map.Width;
                    int cy = i / map.Width;
                    cells.Add((cx, cy));
                    minX = Math.Min(minX, cx);
                    maxX = Math.Max(maxX, cx);
                    minY = Math.Min(minY, cy);
                    maxY = Math.Max(maxY, cy);
                    for (int d = 0; d < 8; d++)
                    {
                        int nx = cx + dx[d];
                        int ny = cy + dy[d];
                        if ((uint)nx >= (uint)map.Width || (uint)ny >= (uint)map.Height)
                        {
                            continue;
                        }

                        int ni = ny * map.Width + nx;
                        if (seen[ni] || !HasWall(map.Tiles[ni]))
                        {
                            continue;
                        }

                        seen[ni] = true;
                        queue.Enqueue(ni);
                    }
                }

                int w = maxX - minX + 1;
                int h = maxY - minY + 1;
                int area = w * h;
                bool treeLike = cells.Count >= 1 && w <= 4 && h <= 4 && cells.Count <= 10;
                bool buildingLike = cells.Count >= 3 && w >= 2 && h >= 2 && w <= 20 && h <= 16
                    && cells.Count <= 220
                    && cells.Count >= area * 0.18
                    && (w < map.Width - 1 || h < map.Height - 1);
                if (!treeLike && !buildingLike)
                {
                    continue;
                }

                var tiles = new MapTile[area];
                var key = new System.Text.StringBuilder(area * 12);
                key.Append(w).Append('x').Append(h).Append(':');
                ushort sampleL = 0;
                ushort sampleR = 0;
                for (int ty = minY; ty <= maxY; ty++)
                {
                    for (int tx = minX; tx <= maxX; tx++)
                    {
                        MapTile tile = map[tx, ty];
                        int i = (ty - minY) * w + (tx - minX);
                        tiles[i] = tile;
                        if (sampleL == 0 && tile.LeftWall != 0)
                        {
                            sampleL = tile.LeftWall;
                        }

                        if (sampleR == 0 && tile.RightWall != 0)
                        {
                            sampleR = tile.RightWall;
                        }

                        key.Append(tile.Floor).Append(',').Append(tile.LeftWall).Append(',').Append(tile.RightWall).Append(';');
                    }
                }

                string sig = key.ToString();
                if (_structureAcc.TryGetValue(sig, out StructureAcc? acc) && acc is not null)
                {
                    acc.Count++;
                }
                else
                {
                    _structureAcc[sig] = new StructureAcc(sig, w, h, tiles, sampleL, sampleR, 1, source);
                }

                if (treeLike)
                {
                    _treeSprites[(sampleL, sampleR)] = _treeSprites.GetValueOrDefault((sampleL, sampleR)) + 1;
                }
            }
        }
    }

    private void IngestPaths(MapFile map)
    {
        ushort ground = MajorityOpenFloor(map);
        if (ground == 0)
        {
            return;
        }

        string source = Path.GetFileNameWithoutExtension(map.Name ?? "") is { Length: > 0 } name ? name : "map";
        int boost = LodInRange(source, 500, 600) ? 5 : 1;
        var seen = new bool[map.Tiles.Length];
        int[] dx = [1, -1, 0, 0];
        int[] dy = [0, 0, 1, -1];
        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                int start = y * map.Width + x;
                MapTile seed = map.Tiles[start];
                if (seen[start] || !IsOpen(seed) || seed.Floor == ground)
                {
                    continue;
                }

                var cells = new List<(int X, int Y)>();
                var walkN = new Dictionary<ushort, int>();
                var queue = new Queue<int>();
                queue.Enqueue(start);
                seen[start] = true;
                int minX = x, maxX = x, minY = y, maxY = y;
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue();
                    int cx = i % map.Width;
                    int cy = i / map.Width;
                    MapTile cur = map.Tiles[i];
                    cells.Add((cx, cy));
                    walkN[cur.Floor] = walkN.GetValueOrDefault(cur.Floor) + 1;
                    minX = Math.Min(minX, cx);
                    maxX = Math.Max(maxX, cx);
                    minY = Math.Min(minY, cy);
                    maxY = Math.Max(maxY, cy);
                    for (int d = 0; d < 4; d++)
                    {
                        int nx = cx + dx[d];
                        int ny = cy + dy[d];
                        if ((uint)nx >= (uint)map.Width || (uint)ny >= (uint)map.Height)
                        {
                            continue;
                        }

                        int ni = ny * map.Width + nx;
                        MapTile next = map.Tiles[ni];
                        if (seen[ni] || !IsOpen(next) || next.Floor == ground)
                        {
                            continue;
                        }

                        seen[ni] = true;
                        queue.Enqueue(ni);
                    }
                }

                int w = maxX - minX + 1;
                int h = maxY - minY + 1;
                int area = w * h;
                bool skinny = Math.Min(w, h) <= 6 && Math.Max(w, h) >= 4
                    && cells.Count >= 8
                    && cells.Count <= 220
                    && cells.Count >= area * 0.22;
                if (!skinny)
                {
                    continue;
                }

                ushort walkMode = walkN.OrderByDescending(e => e.Value).First().Key;
                var family = walkN
                    .Where(e => Math.Abs(e.Key - walkMode) <= 28)
                    .Select(e => e.Key)
                    .ToHashSet();
                int familyCount = walkN.Where(e => family.Contains(e.Key)).Sum(e => e.Value);
                if (family.Count == 0 || family.Count > 10 || familyCount < cells.Count * 0.65)
                {
                    continue;
                }

                int x0 = Math.Max(0, minX - 1);
                int y0 = Math.Max(0, minY - 1);
                int x1 = Math.Min(map.Width - 1, maxX + 1);
                int y1 = Math.Min(map.Height - 1, maxY + 1);
                if (Math.Max(x1 - x0 + 1, y1 - y0 + 1) <= 14)
                {
                    TryAddPathStamp(map, x0, y0, x1, y1, family, ground, source, boost);
                }

                SlicePathWindows(map, x0, y0, x1, y1, family, ground, source, boost);
            }
        }
    }

    private void SlicePathWindows(
        MapFile map,
        int x0,
        int y0,
        int x1,
        int y1,
        HashSet<ushort> family,
        ushort ground,
        string source,
        int boost)
    {
        (int W, int H)[] sizes = [(8, 3), (12, 3), (6, 3), (3, 8), (3, 12), (3, 6), (5, 5), (7, 5)];
        int added = 0;
        foreach ((int sw, int sh) in sizes)
        {
            for (int y = y0; y + sh - 1 <= y1 && added < 10; y++)
            {
                for (int x = x0; x + sw - 1 <= x1 && added < 10; x++)
                {
                    if (!LooksLikeRibbon(map, x, y, sw, sh, family, ground))
                    {
                        continue;
                    }

                    if (TryAddPathStamp(map, x, y, x + sw - 1, y + sh - 1, family, ground, source, boost))
                    {
                        added++;
                    }
                }
            }
        }
    }

    private bool TryAddPathStamp(
        MapFile map,
        int x0,
        int y0,
        int x1,
        int y1,
        HashSet<ushort> family,
        ushort ground,
        string source,
        int boost)
    {
        int w = x1 - x0 + 1;
        int h = y1 - y0 + 1;
        var tiles = new MapTile[w * h];
        var key = new System.Text.StringBuilder(w * h * 8);
        key.Append("path:").Append(w).Append('x').Append(h).Append(':');
        int walkCells = 0;
        int vergeCells = 0;
        for (int ty = y0; ty <= y1; ty++)
        {
            for (int tx = x0; tx <= x1; tx++)
            {
                MapTile tile = map[tx, ty];
                int i = (ty - y0) * w + (tx - x0);
                if (!IsOpen(tile))
                {
                    key.Append("0;");
                    continue;
                }

                if (family.Contains(tile.Floor))
                {
                    tiles[i] = new MapTile(tile.Floor, 0, 0);
                    walkCells++;
                }
                else if (tile.Floor == ground && TouchesFamily(map, tx, ty, family))
                {
                    tiles[i] = new MapTile(tile.Floor, 0, 0);
                    vergeCells++;
                }

                key.Append(tiles[i].Floor).Append(';');
            }
        }

        if (walkCells < 6 || vergeCells < 3)
        {
            return false;
        }

        string sig = key.ToString();
        if (_structureAcc.TryGetValue(sig, out StructureAcc? acc) && acc is not null)
        {
            acc.Count += boost;
            return true;
        }

        _structureAcc[sig] = new StructureAcc(sig, w, h, tiles, 0, 0, boost, source);
        return true;
    }

    private static bool LooksLikeRibbon(
        MapFile map,
        int x0,
        int y0,
        int width,
        int height,
        HashSet<ushort> family,
        ushort ground)
    {
        int walk = 0;
        int verge = 0;
        int open = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                MapTile tile = map[x0 + x, y0 + y];
                if (!IsOpen(tile))
                {
                    continue;
                }

                open++;
                if (family.Contains(tile.Floor))
                {
                    walk++;
                }
                else if (tile.Floor == ground)
                {
                    verge++;
                }
            }
        }

        if (walk < 4 || verge < 3 || walk + verge < open * 0.75)
        {
            return false;
        }

        if (height == 3 && width >= 6)
        {
            return RowMostly(map, x0, y0, width, family)
                && RowMostly(map, x0, y0 + 2, width, [ground])
                || RowMostly(map, x0, y0 + 2, width, family)
                && RowMostly(map, x0, y0, width, [ground])
                || RowMostly(map, x0, y0 + 1, width, family)
                && RowMostly(map, x0, y0, width, [ground])
                && RowMostly(map, x0, y0 + 2, width, [ground]);
        }

        if (width == 3 && height >= 6)
        {
            return ColMostly(map, x0 + 1, y0, height, family)
                && ColMostly(map, x0, y0, height, [ground])
                && ColMostly(map, x0 + 2, y0, height, [ground]);
        }

        return walk >= width && verge >= 4;
    }

    private static bool RowMostly(MapFile map, int x0, int y, int width, HashSet<ushort> floors)
    {
        int hit = 0;
        for (int i = 0; i < width; i++)
        {
            MapTile tile = map[x0 + i, y];
            if (IsOpen(tile) && floors.Contains(tile.Floor))
            {
                hit++;
            }
        }

        return hit >= Math.Max(3, (width * 2) / 3);
    }

    private static bool ColMostly(MapFile map, int x, int y0, int height, HashSet<ushort> floors)
    {
        int hit = 0;
        for (int i = 0; i < height; i++)
        {
            MapTile tile = map[x, y0 + i];
            if (IsOpen(tile) && floors.Contains(tile.Floor))
            {
                hit++;
            }
        }

        return hit >= Math.Max(3, (height * 2) / 3);
    }

    private static bool TouchesFamily(MapFile map, int x, int y, HashSet<ushort> family)
    {
        int[] dx = [1, -1, 0, 0];
        int[] dy = [0, 0, 1, -1];
        for (int d = 0; d < 4; d++)
        {
            int nx = x + dx[d];
            int ny = y + dy[d];
            if ((uint)nx >= (uint)map.Width || (uint)ny >= (uint)map.Height)
            {
                continue;
            }

            MapTile tile = map[nx, ny];
            if (IsOpen(tile) && family.Contains(tile.Floor))
            {
                return true;
            }
        }

        return false;
    }

    private static ushort MajorityOpenFloor(MapFile map)
    {
        var counts = new Dictionary<ushort, int>();
        foreach (MapTile tile in map.Tiles)
        {
            if (!IsOpen(tile))
            {
                continue;
            }

            counts[tile.Floor] = counts.GetValueOrDefault(tile.Floor) + 1;
        }

        return counts.Count == 0 ? (ushort)0 : counts.OrderByDescending(e => e.Value).First().Key;
    }

    private static bool LodInRange(string stem, int lo, int hi)
    {
        if (!stem.StartsWith("lod", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return int.TryParse(stem.AsSpan(3), out int id) && id >= lo && id <= hi;
    }

    private static bool IsOpen(MapTile tile) =>
        tile.Floor != 0 && tile.LeftWall == 0 && tile.RightWall == 0;

    private void CompileFloorFamilies()
    {
        _families = FloorFamilies.Cluster(_floors.Where(e => e.Value >= 4).Select(e => (int)e.Key));
        var border = new Dictionary<(int Ground, int Path), int>();
        var tileVotes = new Dictionary<(int Ground, ushort PathTile), int>();
        foreach (KeyValuePair<(ushort A, ushort B), int> entry in _floorAdj)
        {
            if (entry.Value < 2)
            {
                continue;
            }

            FloorFamily? left = FamilyOf(entry.Key.A);
            FloorFamily? right = FamilyOf(entry.Key.B);
            if (left is null || right is null || left.Value.Min == right.Value.Min)
            {
                continue;
            }

            AddBorder(border, tileVotes, left.Value.Min, right.Value.Min, entry.Key.B, entry.Value);
            AddBorder(border, tileVotes, right.Value.Min, left.Value.Min, entry.Key.A, entry.Value);
        }

        _pathTileByFamily.Clear();
        foreach (int ground in border.Select(e => e.Key.Ground).Distinct())
        {
            FloorFamily? intended = FloorFamilies.PathFor(ground);
            bool forced = intended is FloorFamily want && want.Min != ground;
            (int Ground, int Path) bestKey = default;
            int best = 0;
            if (forced && border.TryGetValue((ground, intended!.Value.Min), out int forcedScore) && forcedScore > 0)
            {
                best = forcedScore;
                bestKey = (ground, intended.Value.Min);
            }
            else
            {
                foreach (KeyValuePair<(int Ground, int Path), int> entry in border)
                {
                    if (entry.Key.Ground == ground && entry.Value > best)
                    {
                        best = entry.Value;
                        bestKey = entry.Key;
                    }
                }
            }

            if (best < (forced ? 1 : 12))
            {
                continue;
            }

            ushort tile = 0;
            int tileScore = 0;
            foreach (KeyValuePair<(int Ground, ushort PathTile), int> vote in tileVotes)
            {
                if (vote.Key.Ground != ground || FamilyOf(vote.Key.PathTile)?.Min != bestKey.Path)
                {
                    continue;
                }

                if (vote.Value > tileScore)
                {
                    tileScore = vote.Value;
                    tile = vote.Key.PathTile;
                }
            }

            if (tile > 1)
            {
                _pathTileByFamily[ground] = tile;
            }
        }
    }

    private static void AddBorder(
        Dictionary<(int Ground, int Path), int> border,
        Dictionary<(int Ground, ushort PathTile), int> tileVotes,
        int groundFamily,
        int pathFamily,
        ushort pathTile,
        int count)
    {
        (int Ground, int Path) key = (groundFamily, pathFamily);
        border[key] = border.GetValueOrDefault(key) + count;
        (int Ground, ushort PathTile) tileKey = (groundFamily, pathTile);
        tileVotes[tileKey] = tileVotes.GetValueOrDefault(tileKey) + count;
    }

    private void CompileStructures()
    {
        CompileFloorFamilies();
        TreeSprites = _treeSprites
            .OrderByDescending(e => e.Value)
            .Take(40)
            .Select(e => (e.Key.L, e.Key.R, e.Value))
            .ToList();

        var stamps = _structureAcc.Values
            .Select(s =>
            {
                StampCategory category = StampCategories.Classify(s.Cells, s.Width, s.Height);
                string noun = StampCategories.Noun(category);
                return new StructureStamp(
                    s.Key,
                    $"{noun}  {s.Source}  L{s.SampleLeft}/R{s.SampleRight}  {s.Width}×{s.Height}  ×{s.Count}",
                    s.Width,
                    s.Height,
                    s.Cells,
                    s.Count,
                    CopyFloors: true,
                    Category: category);
            })
            .ToList();

        Structures = StampCategories.All
            .SelectMany(category => stamps
                .Where(s => s.Category == category)
                .OrderByDescending(s => s.Uses)
                .ThenBy(s => s.Width * s.Height)
                .Take(category switch
                {
                    StampCategory.Building => 280,
                    StampCategory.Pair => 80,
                    StampCategory.Path => 160,
                    _ => 120
                }))
            .ToList();
    }

    private static bool HasWall(MapTile tile) => tile.LeftWall != 0 || tile.RightWall != 0;

    private sealed class StructureAcc
    {
        public StructureAcc(string key, int width, int height, MapTile[] cells, ushort sampleLeft, ushort sampleRight, int count, string source)
        {
            Key = key;
            Width = width;
            Height = height;
            Cells = cells;
            SampleLeft = sampleLeft;
            SampleRight = sampleRight;
            Count = count;
            Source = string.IsNullOrWhiteSpace(source) ? "map" : source;
        }

        public string Key { get; }
        public int Width { get; }
        public int Height { get; }
        public MapTile[] Cells { get; }
        public ushort SampleLeft { get; }
        public ushort SampleRight { get; }
        public int Count { get; set; }
        public string Source { get; }
    }

    private void AddAdj(ushort a, ushort b)
    {
        if (a == 0 || b == 0 || a == b)
        {
            return;
        }

        if (a > b)
        {
            (a, b) = (b, a);
        }

        (ushort A, ushort B) key = (a, b);
        _floorAdj[key] = _floorAdj.GetValueOrDefault(key) + 1;
    }

    private static void Add(Dictionary<ushort, int> counts, ushort id) =>
        counts[id] = counts.GetValueOrDefault(id) + 1;
}

public readonly record struct StructureStamp(
    string Key,
    string Label,
    int Width,
    int Height,
    MapTile[] Cells,
    int Uses,
    bool CopyFloors = false,
    StampCategory Category = StampCategory.Building);

public readonly record struct WallStyle(
    ushort CornerLeft,
    ushort CornerRight,
    ushort EdgeLeft,
    ushort EdgeRight,
    ushort SpanLeft,
    ushort SpanLeft2,
    int BodyDelta);
