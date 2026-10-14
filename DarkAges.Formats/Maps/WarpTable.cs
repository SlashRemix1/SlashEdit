using System.Globalization;
using System.Text.RegularExpressions;
using DarkAges.Formats.Data;

namespace DarkAges.Formats.Maps;

public enum WarpOrigin
{
    TMap,
    Sidecar,
    Import,
    Dump,
    Server
}

public sealed class WarpTable
{
    private static readonly Regex MapIdName = new(
        @"^(?:lod|map[_-]?)?0*(\d+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static WarpTable Empty { get; } = new();

    private readonly Dictionary<int, List<MapWarp>> _byMap = [];
    private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);

    public int MapCount => _byMap.Count;
    public int WarpCount => _byMap.Values.Sum(list => list.Count);

    public static WarpTable Load(
        IEnumerable<string> metaFolders,
        string? sidecarPath = null,
        IEnumerable<string>? dumpFolders = null)
    {
        var table = new WarpTable();
        foreach (string folder in metaFolders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder);
            }
            catch
            {
                continue;
            }

            foreach (string path in files)
            {
                string stem = Path.GetFileName(path);
                if (!stem.StartsWith("TMap", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    byte[] data = File.ReadAllBytes(path);
                    if (!MetaFile.LooksLike(data))
                    {
                        continue;
                    }

                    table.AddRange(ParseTMap(MetaFile.Load(data, stem, path)), replace: false);
                }
                catch
                {
                    // skip unreadable TMap copies
                }
            }
        }

        if (dumpFolders is not null)
        {
            foreach (string folder in dumpFolders)
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                {
                    continue;
                }

                try
                {
                    IReadOnlyList<MapWarp> dumped = WarpImport.FromDirectory(folder).Warps;
                    table.AddRange(dumped.Select(w => w with { Origin = WarpOrigin.Dump }), replace: true);
                }
                catch
                {
                    // skip unreadable dump folders
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(sidecarPath) && File.Exists(sidecarPath))
        {
            try
            {
                WarpFile file = WarpFile.Load(sidecarPath);
                table.HideRange(file.Hidden);
                table.AddRange(file.Warps.Select(w => w.ToWarp(WarpOrigin.Sidecar)), replace: true);
            }
            catch
            {
                // keep TMap data if the sidecar is bad
            }
        }

        return table;
    }

    public IReadOnlyList<MapWarp> For(string? mapName) =>
        TryParseMapId(mapName, out int id) ? For(id) : [];

    public IReadOnlyList<MapWarp> For(int mapId) =>
        _byMap.TryGetValue(mapId, out List<MapWarp>? list) ? list : [];

    public IReadOnlyList<MapWarp> Arrivals(string? mapName) =>
        TryParseMapId(mapName, out int id) ? Arrivals(id) : [];

    public IReadOnlyList<MapWarp> Arrivals(int mapId)
    {
        var list = new List<MapWarp>();
        foreach (MapWarp warp in All())
        {
            if (warp.DestMapId == mapId && warp.HasLanding)
            {
                list.Add(warp);
            }
        }

        return list;
    }

    public MapWarp? ArrivalAt(string? mapName, int x, int y)
    {
        foreach (MapWarp warp in Arrivals(mapName))
        {
            if (warp.DestX == x && warp.DestY == y)
            {
                return warp;
            }
        }

        return null;
    }

    public IEnumerable<MapWarp> All() => _byMap.Values.SelectMany(list => list);

    public MapWarp? At(string? mapName, int x, int y)
    {
        foreach (MapWarp warp in For(mapName))
        {
            if (warp.Covers(x, y))
            {
                return warp;
            }
        }

        return null;
    }

    public void Add(MapWarp warp, bool replace = true)
    {
        _hidden.Remove(HideKey(warp));
        if (!_byMap.TryGetValue(warp.MapId, out List<MapWarp>? list))
        {
            list = [];
            _byMap[warp.MapId] = list;
        }

        if (replace)
        {
            int i = list.FindIndex(w => SameRect(w, warp));
            if (i >= 0)
            {
                warp = MergeDest(list[i], warp);
                list.RemoveAt(i);
            }
        }

        list.Add(warp);
    }

    public void AddRange(IEnumerable<MapWarp> warps, bool replace)
    {
        foreach (MapWarp warp in warps)
        {
            Add(warp, replace);
        }
    }

    private static MapWarp MergeDest(MapWarp previous, MapWarp incoming)
    {
        int destMap = incoming.DestMapId > 0 ? incoming.DestMapId : previous.DestMapId;
        int destX = incoming.HasLanding ? incoming.DestX : previous.DestX;
        int destY = incoming.HasLanding ? incoming.DestY : previous.DestY;
        string dest = string.IsNullOrWhiteSpace(incoming.Destination) ? previous.Destination : incoming.Destination;
        return incoming with { DestMapId = destMap, DestX = destX, DestY = destY, Destination = dest ?? "" };
    }

    public void FitAfterPad(int mapId, int dx, int dy, int newWidth, int newHeight)
    {
        if (mapId <= 0 || newWidth < 1 || newHeight < 1)
        {
            return;
        }

        foreach (int id in _byMap.Keys.ToList())
        {
            List<MapWarp> list = _byMap[id];
            for (int i = 0; i < list.Count; i++)
            {
                MapWarp warp = list[i];
                if (warp.DestMapId != mapId || !warp.HasLanding)
                {
                    continue;
                }

                int nx = warp.DestX + dx;
                int ny = warp.DestY + dy;
                list[i] = InBounds(nx, ny, newWidth, newHeight)
                    ? warp with { DestX = nx, DestY = ny }
                    : warp with { DestX = -1, DestY = -1 };
            }
        }

        if (!_byMap.TryGetValue(mapId, out List<MapWarp>? sources))
        {
            return;
        }

        var next = new List<MapWarp>(sources.Count);
        foreach (MapWarp warp in sources)
        {
            if (!TryIntersectRect(
                    warp.X1 + dx,
                    warp.Y1 + dy,
                    warp.X2 + dx,
                    warp.Y2 + dy,
                    newWidth,
                    newHeight,
                    out int x1,
                    out int y1,
                    out int x2,
                    out int y2))
            {
                continue;
            }

            next.Add(warp with { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 });
        }

        if (next.Count == 0)
        {
            _byMap.Remove(mapId);
        }
        else
        {
            _byMap[mapId] = next;
        }
    }

    private static bool InBounds(int x, int y, int width, int height) =>
        (uint)x < (uint)width && (uint)y < (uint)height;

    private static bool TryIntersectRect(
        int x1,
        int y1,
        int x2,
        int y2,
        int width,
        int height,
        out int nx1,
        out int ny1,
        out int nx2,
        out int ny2)
    {
        int xa = Math.Min(x1, x2);
        int xb = Math.Max(x1, x2);
        int ya = Math.Min(y1, y2);
        int yb = Math.Max(y1, y2);
        xa = Math.Max(0, xa);
        ya = Math.Max(0, ya);
        xb = Math.Min(width - 1, xb);
        yb = Math.Min(height - 1, yb);
        nx1 = xa;
        ny1 = ya;
        nx2 = xb;
        ny2 = yb;
        return xa <= xb && ya <= yb;
    }

    public void Remove(MapWarp warp)
    {
        if (_byMap.TryGetValue(warp.MapId, out List<MapWarp>? list))
        {
            list.RemoveAll(w => SameRect(w, warp));
            if (list.Count == 0)
            {
                _byMap.Remove(warp.MapId);
            }
        }

        if (warp.Origin is WarpOrigin.TMap or WarpOrigin.Dump)
        {
            _hidden.Add(HideKey(warp));
        }
    }

    public void HideRange(IEnumerable<string> keys)
    {
        foreach (string key in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            _hidden.Add(key);
            string[] parts = key.Split(':');
            if (parts.Length == 5
                && int.TryParse(parts[0], out int mapId)
                && int.TryParse(parts[1], out int x1)
                && int.TryParse(parts[2], out int y1)
                && int.TryParse(parts[3], out int x2)
                && int.TryParse(parts[4], out int y2)
                && _byMap.TryGetValue(mapId, out List<MapWarp>? list))
            {
                list.RemoveAll(w => w.X1 == x1 && w.Y1 == y1 && w.X2 == x2 && w.Y2 == y2);
                if (list.Count == 0)
                {
                    _byMap.Remove(mapId);
                }
            }
        }
    }

    public WarpFile ToSidecar(string client)
    {
        var file = new WarpFile { Client = client };
        file.Hidden.AddRange(_hidden.OrderBy(k => k, StringComparer.Ordinal));
        file.Warps.AddRange(All()
            .Where(w => w.Origin is WarpOrigin.Sidecar or WarpOrigin.Import)
            .Select(WarpRecord.FromWarp));
        return file;
    }

    public static bool TryParseMapId(string? mapName, out int mapId)
    {
        mapId = 0;
        if (string.IsNullOrWhiteSpace(mapName))
        {
            return false;
        }

        string stem = Path.GetFileNameWithoutExtension(mapName.Trim());
        Match match = MapIdName.Match(stem);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out mapId);
    }

    public static int GuessMapId(string? text)
    {
        if (TryParseMapId(text, out int id))
        {
            return id;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var digits = new string(text.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) ? id : 0;
    }

    public static IReadOnlyList<MapWarp> ParseTMap(MetaFile meta)
    {
        var list = new List<MapWarp>();
        foreach (MetaNode node in meta.Nodes)
        {
            if (!int.TryParse(node.Name.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int mapId))
            {
                continue;
            }

            foreach (string property in node.Properties)
            {
                if (TryParseWarp(mapId, property, out MapWarp warp))
                {
                    list.Add(warp);
                }
            }
        }

        return list;
    }

    public static bool SameRect(MapWarp a, MapWarp b) =>
        a.MapId == b.MapId && a.X1 == b.X1 && a.Y1 == b.Y1 && a.X2 == b.X2 && a.Y2 == b.Y2;

    public static string HideKey(MapWarp warp) => $"{warp.MapId}:{warp.X1}:{warp.Y1}:{warp.X2}:{warp.Y2}";

    private static bool TryParseWarp(int mapId, string text, out MapWarp warp)
    {
        warp = default;
        string[] parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 6)
        {
            return false;
        }

        string flag = parts[^1].Trim('"');
        if (!int.TryParse(parts[^5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x1)
            || !int.TryParse(parts[^4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y1)
            || !int.TryParse(parts[^3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x2)
            || !int.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y2))
        {
            return false;
        }

        string dest = string.Join(" ", parts.Take(parts.Length - 5)).Trim().Trim('"');
        if (dest is "`" or "\"`\"")
        {
            dest = "";
        }

        warp = new MapWarp(mapId, dest, x1, y1, x2, y2, flag, GuessMapId(dest), -1, -1, WarpOrigin.TMap);
        return true;
    }
}

public readonly record struct MapWarp(
    int MapId,
    string Destination,
    int X1,
    int Y1,
    int X2,
    int Y2,
    string Flag,
    int DestMapId = 0,
    int DestX = -1,
    int DestY = -1,
    WarpOrigin Origin = WarpOrigin.TMap)
{
    public bool HasLanding => DestX >= 0 && DestY >= 0;

    /// <summary>
    /// Opens the overworld picker (WarpType World / “to world map”). No dest map or X/Y by design.
    /// </summary>
    public bool IsWorldMap =>
        LooksLikeWorldMap(Destination) || LooksLikeWorldMap(Flag)
        || string.Equals(Flag, "World", StringComparison.OrdinalIgnoreCase);

    public static bool LooksLikeWorldMap(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && (text.Contains("to world map", StringComparison.OrdinalIgnoreCase)
            || text.Contains("worldmap", StringComparison.OrdinalIgnoreCase));

    public bool Covers(int x, int y)
    {
        int x0 = Math.Min(X1, X2);
        int x1 = Math.Max(X1, X2);
        int y0 = Math.Min(Y1, Y2);
        int y1 = Math.Max(Y1, Y2);
        return x >= x0 && x <= x1 && y >= y0 && y <= y1;
    }

    public IEnumerable<(int X, int Y)> Tiles()
    {
        int x0 = Math.Min(X1, X2);
        int x1 = Math.Max(X1, X2);
        int y0 = Math.Min(Y1, Y2);
        int y1 = Math.Max(Y1, Y2);
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                yield return (x, y);
            }
        }
    }
}

public sealed class WarpFile
{
    public string Client { get; set; } = "";
    public List<WarpRecord> Warps { get; set; } = [];
    public List<string> Hidden { get; set; } = [];

    public static WarpFile Load(string path) =>
        System.Text.Json.JsonSerializer.Deserialize<WarpFile>(
            File.ReadAllText(path),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new WarpFile();

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(this, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed class WarpRecord
{
    public int MapId { get; set; }
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int X2 { get; set; }
    public int Y2 { get; set; }
    public int DestMapId { get; set; }
    public int DestX { get; set; } = -1;
    public int DestY { get; set; } = -1;
    public string Destination { get; set; } = "";
    public string Flag { get; set; } = "";

    public static WarpRecord FromWarp(MapWarp warp) => new()
    {
        MapId = warp.MapId,
        X1 = warp.X1,
        Y1 = warp.Y1,
        X2 = warp.X2,
        Y2 = warp.Y2,
        DestMapId = warp.DestMapId,
        DestX = warp.DestX,
        DestY = warp.DestY,
        Destination = warp.Destination,
        Flag = warp.Flag
    };

    public MapWarp ToWarp(WarpOrigin origin) =>
        new(MapId, Destination ?? "", X1, Y1, X2, Y2, Flag ?? "", DestMapId, DestX, DestY, origin);
}
