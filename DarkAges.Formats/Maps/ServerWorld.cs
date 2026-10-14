using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DarkAges.Formats.Maps;

public enum ServerKind
{
    None,
    Hybrasyl,
    Chaos,
    Mixed
}

public sealed class ServerMap
{
    public int MapId { get; set; }
    public string Name { get; set; } = "";
    public string InstanceKey { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int Music { get; set; }
    public string Flags { get; set; } = "";
    public string LightType { get; set; } = "";
    public bool DynamicLighting { get; set; }
    public string Source { get; set; } = "";
    public ServerKind Kind { get; set; }
    public List<MapWarp> Warps { get; } = [];

    public bool HasMeta =>
        Music != 0
        || !string.IsNullOrWhiteSpace(Flags)
        || !string.IsNullOrWhiteSpace(LightType)
        || DynamicLighting
        || Width != 0
        || Height != 0;
}

public sealed class ServerWorld
{
    public static ServerWorld Empty { get; } = new();

    private static readonly Regex PointText = new(
        @"^\(?\s*(-?\d+)\s*[, ]\s*(-?\d+)\s*\)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NamedPoint = new(
        @"^(?<key>[^:]+):\s*\(?\s*(?<x>-?\d+)\s*[, ]\s*(?<y>-?\d+)\s*\)?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly Dictionary<int, ServerMap> _maps = [];
    private readonly Dictionary<string, int> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _instances = new(StringComparer.OrdinalIgnoreCase);

    public string Source { get; private set; } = "";
    public ServerKind Kind { get; private set; }
    public int MapCount => _maps.Count;
    public int WarpCount => _maps.Values.Sum(m => m.Warps.Count);
    public bool IsEmpty => _maps.Count == 0;

    public IReadOnlyList<ServerMap> Maps() =>
        _maps.Values.OrderBy(m => m.MapId).ToList();

    public ServerMap? Get(int mapId) =>
        mapId > 0 && _maps.TryGetValue(mapId, out ServerMap? map) ? map : null;

    public IReadOnlyList<MapWarp> Warps(int mapId) =>
        Get(mapId)?.Warps ?? [];

    public IReadOnlyList<MapWarp> Warps(string? mapName) =>
        WarpTable.TryParseMapId(mapName, out int id) ? Warps(id) : [];

    public IReadOnlyList<MapWarp> Arrivals(int mapId)
    {
        var list = new List<MapWarp>();
        foreach (ServerMap map in _maps.Values)
        {
            foreach (MapWarp warp in map.Warps)
            {
                if (warp.DestMapId == mapId && warp.HasLanding)
                {
                    list.Add(warp);
                }
            }
        }

        return list;
    }

    public IReadOnlyList<MapWarp> Arrivals(string? mapName) =>
        WarpTable.TryParseMapId(mapName, out int id) ? Arrivals(id) : [];

    public MapWarp? At(int mapId, int x, int y)
    {
        foreach (MapWarp warp in Warps(mapId))
        {
            if (warp.Covers(x, y))
            {
                return warp;
            }
        }

        return null;
    }

    public MapWarp? At(string? mapName, int x, int y) =>
        WarpTable.TryParseMapId(mapName, out int id) ? At(id, x, y) : null;

    public MapWarp? ArrivalAt(int mapId, int x, int y)
    {
        foreach (MapWarp warp in Arrivals(mapId))
        {
            if (warp.DestX == x && warp.DestY == y)
            {
                return warp;
            }
        }

        return null;
    }

    public MapWarp? ArrivalAt(string? mapName, int x, int y) =>
        WarpTable.TryParseMapId(mapName, out int id) ? ArrivalAt(id, x, y) : null;

    public void Clear()
    {
        _maps.Clear();
        _names.Clear();
        _instances.Clear();
        Source = "";
        Kind = ServerKind.None;
    }

    public void CopyFrom(ServerWorld other)
    {
        Clear();
        if (other is null)
        {
            return;
        }

        Source = other.Source;
        Kind = other.Kind;
        foreach (ServerMap map in other._maps.Values)
        {
            ServerMap copy = Clone(map);
            _maps[copy.MapId] = copy;
            RememberName(copy.MapId, copy.Name);
            if (!string.IsNullOrWhiteSpace(copy.InstanceKey))
            {
                _instances[copy.InstanceKey] = copy.MapId;
            }
        }

        foreach ((string key, int id) in other._instances)
        {
            _instances[key] = id;
        }
    }

    public static ServerWorld Load(string path)
    {
        var world = new ServerWorld();
        if (string.IsNullOrWhiteSpace(path))
        {
            return world;
        }

        string full = Path.GetFullPath(path);
        world.Source = full;
        if (File.Exists(full))
        {
            world.IngestFile(full, reactors: false);
            world.IngestFile(full, reactors: true);
        }
        else if (Directory.Exists(full))
        {
            string[] files = EnumerateDataFiles(full).ToArray();
            foreach (string file in files)
            {
                world.IngestFile(file, reactors: false);
            }

            foreach (string file in files)
            {
                world.IngestFile(file, reactors: true);
            }
        }

        world.ResolveDestinations();
        world.Kind = world.DetectKind();
        return world;
    }

    public void LoadFrom(string path) => CopyFrom(Load(path));

    private void IngestFile(string path, bool reactors)
    {
        string ext = Path.GetExtension(path);
        if (ext.Equals(".xml", StringComparison.OrdinalIgnoreCase))
        {
            if (reactors)
            {
                return;
            }

            try
            {
                IngestXml(File.ReadAllText(path), path);
            }
            catch
            {
                // skip unreadable XML
            }

            return;
        }

        if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                IngestJson(File.ReadAllText(path), path, reactors);
            }
            catch
            {
                // skip unreadable JSON
            }
        }
    }

    private void IngestXml(string text, string path)
    {
        XDocument doc = XDocument.Parse(text, LoadOptions.SetLineInfo);
        if (doc.Root is null)
        {
            return;
        }

        foreach (XElement el in MapsIn(doc.Root))
        {
            int id = IntAttr(el, "id", "mapid", "number");
            if (id <= 0)
            {
                continue;
            }

            var map = Ensure(
                id,
                StrAttr(el, "name", "title"),
                path,
                ServerKind.Hybrasyl);
            map.Width = FirstPositive(map.Width, IntAttr(el, "x", "width", "cols"));
            map.Height = FirstPositive(map.Height, IntAttr(el, "y", "height", "rows"));
            map.Music = FirstPositive(map.Music, IntAttr(el, "music", "bgm", "song"));
            map.Flags = FirstText(map.Flags, StrAttr(el, "flags", "flag"));
            map.LightType = FirstText(map.LightType, StrAttr(el, "lighttype", "light", "lighting"));
            map.DynamicLighting = map.DynamicLighting || BoolAttr(el, "dynamiclighting", "dynamiclight");

            foreach (XElement warpEl in el.Elements().Where(c => Local(c) == "warp"))
            {
                int x = IntAttr(warpEl, "x");
                int y = IntAttr(warpEl, "y");
                XElement? target = warpEl.Elements().FirstOrDefault(c => Local(c) == "maptarget");
                XElement? world = warpEl.Elements().FirstOrDefault(c => Local(c) == "worldmaptarget");
                string destName = target is not null
                    ? (target.Value ?? "").Trim()
                    : world is not null
                        ? (world.Value ?? "").Trim()
                        : StrAttr(warpEl, "destination", "dest", "target");
                int destX = target is not null ? IntAttr(target, "x") : IntAttr(warpEl, "destx", "targetx");
                int destY = target is not null ? IntAttr(target, "y") : IntAttr(warpEl, "desty", "targety");
                int destMap = IntAttr(warpEl, "destmapid", "destinationmapid", "targetmap");
                if (destMap <= 0)
                {
                    destMap = WarpTable.GuessMapId(destName);
                }

                bool worldMap = world is not null || MapWarp.LooksLikeWorldMap(destName);
                map.Warps.Add(new MapWarp(
                    id,
                    destName,
                    x,
                    y,
                    x,
                    y,
                    worldMap ? "World" : "",
                    destMap,
                    destX,
                    destY,
                    WarpOrigin.Server));
            }

            RememberName(id, map.Name);
        }
    }

    private void IngestJson(string text, string path, bool reactors)
    {
        using var doc = JsonDocument.Parse(text);
        JsonElement root = doc.RootElement;
        string file = Path.GetFileName(path);
        bool reactorFile = file.Equals("reactors.json", StringComparison.OrdinalIgnoreCase) || LooksLikeReactors(root);
        if (reactors)
        {
            if (reactorFile)
            {
                IngestReactors(root, path);
            }

            return;
        }

        if (reactorFile)
        {
            return;
        }

        if (file.Equals("instance.json", StringComparison.OrdinalIgnoreCase)
            || LooksLikeInstance(root))
        {
            IngestInstance(root, path);
            return;
        }

        if (LooksLikeTemplate(root))
        {
            IngestTemplate(root, path);
            return;
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in root.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && LooksLikeTemplate(item))
                {
                    IngestTemplate(item, path);
                }
            }
        }
    }

    private void IngestTemplate(JsonElement obj, string path)
    {
        int id = TemplateKey(obj);
        if (id <= 0)
        {
            return;
        }

        ServerMap map = Ensure(id, StrProp(obj, "name", "displayname"), path, ServerKind.Chaos);
        map.Width = FirstPositive(map.Width, IntProp(obj, "width", "cols"));
        map.Height = FirstPositive(map.Height, IntProp(obj, "height", "rows"));
        map.LightType = FirstText(map.LightType, StrProp(obj, "lighttype", "light", "lighting"));
        RememberName(id, map.Name);
    }

    private void IngestInstance(JsonElement obj, string path)
    {
        int id = TemplateKey(obj);
        if (id <= 0)
        {
            id = IntProp(obj, "mapid", "id");
        }

        if (id <= 0)
        {
            return;
        }

        string instance = FirstText(StrProp(obj, "instanceid", "instance", "key"), Path.GetFileName(Path.GetDirectoryName(path) ?? ""));
        ServerMap map = Ensure(id, StrProp(obj, "name", "displayname"), path, ServerKind.Chaos);
        map.InstanceKey = FirstText(map.InstanceKey, instance);
        map.Music = FirstPositive(map.Music, IntProp(obj, "music", "bgm"));
        map.Flags = FirstText(map.Flags, StrProp(obj, "flags", "flag"));
        map.LightType = FirstText(map.LightType, StrProp(obj, "lighttype", "light"));
        if (!string.IsNullOrWhiteSpace(instance))
        {
            _instances[instance] = id;
        }

        RememberName(id, map.Name);
    }

    private void IngestReactors(JsonElement root, string path)
    {
        int inherited = 0;
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir) && _instances.TryGetValue(Path.GetFileName(dir), out int mapped))
        {
            inherited = mapped;
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in root.EnumerateArray())
            {
                TryReactorWarp(item, inherited, path);
            }

            return;
        }

        if (root.ValueKind == JsonValueKind.Object
            && TryProp(root, out JsonElement list, "reactors", "items", "warps")
            && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in list.EnumerateArray())
            {
                TryReactorWarp(item, inherited, path);
            }
        }
    }

    private void TryReactorWarp(JsonElement obj, int inheritedMap, string path)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        bool warp = ScriptKeys(obj).Any(k =>
            k.Equals("warp", StringComparison.OrdinalIgnoreCase)
            || k.Contains("warp", StringComparison.OrdinalIgnoreCase));
        bool world = ScriptKeys(obj).Any(k =>
            k.Contains("worldmap", StringComparison.OrdinalIgnoreCase));
        if (!warp && !world)
        {
            return;
        }

        if (!TryPointProp(obj, "source", "point", "location", out int x, out int y)
            && !TryPoint(StrProp(obj, "source"), out x, out y))
        {
            x = IntProp(obj, "x", "sourcex");
            y = IntProp(obj, "y", "sourcey");
        }

        int mapId = inheritedMap;
        if (mapId <= 0)
        {
            mapId = IntProp(obj, "mapid", "templatekey");
        }

        if (mapId <= 0)
        {
            return;
        }

        string destText = WarpDestination(obj);
        string destName = destText;
        int destMap = 0;
        int destX = -1;
        int destY = -1;
        if (TryNamedPoint(destText, out string key, out destX, out destY))
        {
            destName = key;
            if (_instances.TryGetValue(key, out int instMap))
            {
                destMap = instMap;
            }
            else
            {
                destMap = WarpTable.GuessMapId(key);
            }
        }
        else if (world)
        {
            destName = FirstText(StrProp(obj, "worldmapkey"), destText);
        }

        ServerMap map = Ensure(mapId, "", path, ServerKind.Chaos);
        map.Warps.Add(new MapWarp(
            mapId,
            destName,
            x,
            y,
            x,
            y,
            world ? "World" : "",
            destMap,
            destX,
            destY,
            WarpOrigin.Server));
    }

    private void ResolveDestinations()
    {
        foreach (ServerMap map in _maps.Values)
        {
            for (int i = 0; i < map.Warps.Count; i++)
            {
                MapWarp warp = map.Warps[i];
                int dest = warp.DestMapId;
                if (dest <= 0 && !string.IsNullOrWhiteSpace(warp.Destination))
                {
                    if (_names.TryGetValue(warp.Destination.Trim(), out int named))
                    {
                        dest = named;
                    }
                    else if (_instances.TryGetValue(warp.Destination.Trim(), out int inst))
                    {
                        dest = inst;
                    }
                    else
                    {
                        dest = WarpTable.GuessMapId(warp.Destination);
                    }
                }

                if (dest != warp.DestMapId)
                {
                    map.Warps[i] = warp with { DestMapId = dest };
                }
            }
        }
    }

    private ServerKind DetectKind()
    {
        bool hyb = _maps.Values.Any(m => m.Kind == ServerKind.Hybrasyl);
        bool chaos = _maps.Values.Any(m => m.Kind == ServerKind.Chaos);
        if (hyb && chaos)
        {
            return ServerKind.Mixed;
        }

        if (hyb)
        {
            return ServerKind.Hybrasyl;
        }

        return chaos ? ServerKind.Chaos : ServerKind.None;
    }

    private ServerMap Ensure(int id, string name, string path, ServerKind kind)
    {
        if (!_maps.TryGetValue(id, out ServerMap? map))
        {
            map = new ServerMap
            {
                MapId = id,
                Name = name?.Trim() ?? "",
                Source = path,
                Kind = kind
            };
            _maps[id] = map;
            return map;
        }

        if (string.IsNullOrWhiteSpace(map.Name) && !string.IsNullOrWhiteSpace(name))
        {
            map.Name = name.Trim();
        }

        if (string.IsNullOrWhiteSpace(map.Source))
        {
            map.Source = path;
        }

        if (map.Kind == ServerKind.None)
        {
            map.Kind = kind;
        }

        return map;
    }

    private void RememberName(int id, string name)
    {
        if (id > 0 && !string.IsNullOrWhiteSpace(name))
        {
            _names[name.Trim()] = id;
        }
    }

    private static ServerMap Clone(ServerMap map)
    {
        var copy = new ServerMap
        {
            MapId = map.MapId,
            Name = map.Name,
            InstanceKey = map.InstanceKey,
            Width = map.Width,
            Height = map.Height,
            Music = map.Music,
            Flags = map.Flags,
            LightType = map.LightType,
            DynamicLighting = map.DynamicLighting,
            Source = map.Source,
            Kind = map.Kind
        };
        copy.Warps.AddRange(map.Warps);
        return copy;
    }

    private static IEnumerable<string> EnumerateDataFiles(string root)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
        }
        catch
        {
            yield break;
        }

        foreach (string file in files)
        {
            string ext = Path.GetExtension(file);
            if (ext is not (".xml" or ".json"))
            {
                continue;
            }

            string full = file.Replace('\\', '/');
            if (full.Contains("/.git/", StringComparison.OrdinalIgnoreCase)
                || full.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                || full.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                || full.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return file;
        }
    }

    private static IEnumerable<XElement> MapsIn(XElement root)
    {
        if (Local(root) == "map")
        {
            yield return root;
        }

        foreach (XElement el in root.Descendants().Where(e => Local(e) == "map"))
        {
            yield return el;
        }
    }

    private static bool LooksLikeTemplate(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        bool key = HasProp(obj, "templatekey", "mapid");
        bool size = HasProp(obj, "width", "height");
        bool light = HasProp(obj, "lighttype", "light");
        return key && (size || light) && !HasProp(obj, "instanceid");
    }

    private static bool LooksLikeInstance(JsonElement obj) =>
        obj.ValueKind == JsonValueKind.Object
        && (HasProp(obj, "instanceid") || (HasProp(obj, "templatekey") && HasProp(obj, "name", "music", "flags")));

    private static bool LooksLikeReactors(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
        {
            JsonElement first = root[0];
            return first.ValueKind == JsonValueKind.Object
                && (HasProp(first, "scriptkeys") || HasProp(first, "scriptvars") || HasProp(first, "source"));
        }

        return root.ValueKind == JsonValueKind.Object && HasProp(root, "reactors");
    }

    private static int TemplateKey(JsonElement obj)
    {
        int id = IntProp(obj, "templatekey", "mapid", "id");
        if (id > 0)
        {
            return id;
        }

        string text = StrProp(obj, "templatekey");
        return WarpTable.GuessMapId(text);
    }

    private static IEnumerable<string> ScriptKeys(JsonElement obj)
    {
        if (!TryProp(obj, out JsonElement keys, "scriptkeys", "scriptkey"))
        {
            yield break;
        }

        if (keys.ValueKind == JsonValueKind.String)
        {
            yield return keys.GetString() ?? "";
            yield break;
        }

        if (keys.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in keys.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    yield return item.GetString() ?? "";
                }
            }
        }
    }

    private static string WarpDestination(JsonElement obj)
    {
        if (TryProp(obj, out JsonElement vars, "scriptvars") && vars.ValueKind == JsonValueKind.Object)
        {
            if (TryProp(vars, out JsonElement warp, "warp") && warp.ValueKind == JsonValueKind.Object)
            {
                string dest = StrProp(warp, "destination", "dest", "target");
                if (!string.IsNullOrWhiteSpace(dest))
                {
                    return dest;
                }
            }

            if (TryProp(vars, out JsonElement world, "showworldmap") && world.ValueKind == JsonValueKind.Object)
            {
                string worldKey = StrProp(world, "worldmapkey", "destination", "key");
                if (!string.IsNullOrWhiteSpace(worldKey))
                {
                    return worldKey;
                }
            }

            string nested = StrProp(vars, "destination", "worldmapkey");
            if (!string.IsNullOrWhiteSpace(nested))
            {
                return nested;
            }
        }

        return FirstText(StrProp(obj, "destination", "worldmapkey"), "");
    }

    private static bool TryPointProp(JsonElement obj, string a, string b, string c, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (!TryProp(obj, out JsonElement point, a, b, c))
        {
            return false;
        }

        if (point.ValueKind == JsonValueKind.String)
        {
            return TryPoint(point.GetString(), out x, out y);
        }

        if (point.ValueKind == JsonValueKind.Object)
        {
            x = IntProp(point, "x");
            y = IntProp(point, "y");
            return true;
        }

        return false;
    }

    private static bool TryPoint(string? text, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        Match match = PointText.Match(text.Trim());
        if (!match.Success)
        {
            return false;
        }

        x = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        y = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryNamedPoint(string? text, out string key, out int x, out int y)
    {
        key = "";
        x = -1;
        y = -1;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        Match match = NamedPoint.Match(text.Trim());
        if (!match.Success)
        {
            return false;
        }

        key = match.Groups["key"].Value.Trim();
        x = int.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture);
        y = int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);
        return key.Length > 0;
    }

    private static string Local(XElement el) => el.Name.LocalName.ToLowerInvariant();

    private static int IntAttr(XElement el, params string[] names)
    {
        foreach (XAttribute attr in el.Attributes())
        {
            if (names.Contains(attr.Name.LocalName.ToLowerInvariant())
                && int.TryParse(attr.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                return n;
            }
        }

        foreach (XElement child in el.Elements())
        {
            if (names.Contains(Local(child))
                && int.TryParse(child.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                return n;
            }
        }

        return 0;
    }

    private static string StrAttr(XElement el, params string[] names)
    {
        foreach (XAttribute attr in el.Attributes())
        {
            if (names.Contains(attr.Name.LocalName.ToLowerInvariant()))
            {
                return attr.Value ?? "";
            }
        }

        foreach (XElement child in el.Elements())
        {
            if (names.Contains(Local(child)))
            {
                return child.Value ?? "";
            }
        }

        return "";
    }

    private static bool BoolAttr(XElement el, params string[] names)
    {
        string text = StrAttr(el, names);
        return text.Equals("true", StringComparison.OrdinalIgnoreCase)
            || text.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || text == "1";
    }

    private static bool TryProp(JsonElement obj, out JsonElement value, params string[] names)
    {
        foreach (JsonProperty p in obj.EnumerateObject())
        {
            string key = Normalize(p.Name);
            if (names.Contains(key))
            {
                value = p.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool HasProp(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (JsonProperty p in obj.EnumerateObject())
        {
            if (names.Contains(Normalize(p.Name)))
            {
                return true;
            }
        }

        return false;
    }

    private static int IntProp(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        foreach (JsonProperty p in obj.EnumerateObject())
        {
            if (!names.Contains(Normalize(p.Name)))
            {
                continue;
            }

            if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int n))
            {
                return n;
            }

            if (p.Value.ValueKind == JsonValueKind.String
                && int.TryParse(p.Value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
            {
                return n;
            }
        }

        return 0;
    }

    private static string StrProp(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        foreach (JsonProperty p in obj.EnumerateObject())
        {
            if (!names.Contains(Normalize(p.Name)))
            {
                continue;
            }

            if (p.Value.ValueKind == JsonValueKind.String)
            {
                return p.Value.GetString() ?? "";
            }

            if (p.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                return p.Value.ToString();
            }
        }

        return "";
    }

    private static string Normalize(string name)
    {
        var chars = name.Where(char.IsLetterOrDigit).ToArray();
        return new string(chars).ToLowerInvariant();
    }

    private static int FirstPositive(int current, int incoming) => current != 0 ? current : incoming;

    private static string FirstText(string current, string incoming) =>
        string.IsNullOrWhiteSpace(current) ? incoming ?? "" : current;
}
