using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DarkAges.Formats.Data;

namespace DarkAges.Formats.Maps;

public sealed class WarpImportResult
{
    public string Format { get; init; } = "unknown";
    public string Source { get; init; } = "";
    public IReadOnlyList<MapWarp> Warps { get; init; } = [];
}

public static class WarpImport
{
    private static readonly string[] MapNames =
    [
        "mapid", "sourcemapid", "sourcemap", "srcmap", "frommap", "source_map", "map", "fieldnumber",
        "sourcefield", "sourcefieldnumber", "areaid", "area", "mapnumber", "mapno", "field", "currentmap",
        "activationmapid", "activationmap", "number", "no"
    ];

    private static readonly string[] X1Names =
        ["x1", "sourcex", "srcx", "fromx", "currentx", "pointx", "sx", "x"];

    private static readonly string[] Y1Names =
        ["y1", "sourcey", "srcy", "fromy", "currenty", "pointy", "sy", "y"];

    private static readonly string[] X2Names = ["x2", "endx", "maxx", "rightx"];
    private static readonly string[] Y2Names = ["y2", "endy", "maxy", "bottomy"];

    private static readonly string[] DestMapNames =
    [
        "destmapid", "destinationmapid", "destinationmap", "destmap", "tomap", "targetmap", "dstmap",
        "destinationfieldnumber", "destinationfield", "targetfield", "tofield", "destarea", "destareaid"
    ];

    private static readonly string[] DestXNames =
        ["destx", "destinationx", "targetx", "tox", "dstx", "dx"];

    private static readonly string[] DestYNames =
        ["desty", "destinationy", "targety", "toy", "dsty", "dy"];

    private static readonly string[] LabelNames = ["destination", "dest", "name", "label", "to", "mapname", "destname"];
    private static readonly string[] FlagNames = ["flag", "facing", "direction", "portal", "warptype"];
    private static readonly string[] ListNames = ["warps", "exits", "portals", "teleports", "teleporters", "worldmaps", "items"];
    private static readonly string[] SourceObjNames = ["source", "from", "location", "origin", "src"];
    private static readonly string[] DestObjNames = ["destination", "dest", "target", "to", "dst"];

    public static IReadOnlyList<MapWarp> FromFile(string path) => FromPath(path).Warps;

    public static IReadOnlyList<MapWarp> FromText(string text) => ParseText(text, "").Warps;

    public static WarpImportResult FromPath(string path)
    {
        if (Directory.Exists(path))
        {
            return FromDirectory(path);
        }

        return FromSingle(path);
    }

    public static WarpImportResult FromDirectory(string path)
    {
        var list = new List<MapWarp>();
        var formats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories);
        }
        catch
        {
            return new WarpImportResult { Format = "folder", Source = path, Warps = [] };
        }

        foreach (string file in files)
        {
            string ext = Path.GetExtension(file);
            string name = Path.GetFileName(file);
            if (ext is not (".json" or ".csv" or ".txt" or ".tsv" or ".xml" or ".sql" or "")
                && !name.StartsWith("TMap", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                WarpImportResult one = FromSingle(file);
                if (one.Warps.Count == 0)
                {
                    continue;
                }

                list.AddRange(one.Warps);
                formats.Add(one.Format);
            }
            catch
            {
                // skip unreadable dump copies
            }
        }

        string format = formats.Count == 0 ? "folder" : "folder (" + string.Join(", ", formats.OrderBy(s => s)) + ")";
        return new WarpImportResult { Format = format, Source = path, Warps = list };
    }

    public static WarpImportResult ParseText(string text, string source)
    {
        text = text.TrimStart('\uFEFF').Trim();
        if (text.Length == 0)
        {
            return new WarpImportResult { Format = "empty", Source = source, Warps = [] };
        }

        if (text.StartsWith('<'))
        {
            return Result("xml", source, FromXml(text));
        }

        if (text.StartsWith('{') || text.StartsWith('['))
        {
            return Result("json", source, FromJson(text));
        }

        if (LooksLikeSql(text))
        {
            return Result("sql", source, FromSql(text));
        }

        IReadOnlyList<MapWarp> table = FromTable(text);
        return Result(table.Count > 0 ? "table" : "unknown", source, table);
    }

    private static WarpImportResult FromSingle(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (MetaFile.LooksLike(data))
        {
            IReadOnlyList<MapWarp> tmap = WarpTable.ParseTMap(MetaFile.Load(data, Path.GetFileName(path), path));
            return Result("tmap", path, tmap);
        }

        return ParseText(Decode(data), path);
    }

    private static WarpImportResult Result(string format, string source, IReadOnlyList<MapWarp> warps) =>
        new() { Format = format, Source = source, Warps = warps };

    private static string Decode(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            return System.Text.Encoding.UTF8.GetString(data, 3, data.Length - 3);
        }

        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            return System.Text.Encoding.Unicode.GetString(data, 2, data.Length - 2);
        }

        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            return System.Text.Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        }

        return System.Text.Encoding.UTF8.GetString(data);
    }

    private static bool LooksLikeSql(string text)
    {
        string head = text.Length > 4000 ? text[..4000] : text;
        return head.Contains("INSERT", StringComparison.OrdinalIgnoreCase)
            && head.Contains("VALUES", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<MapWarp> FromJson(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var list = new List<MapWarp>();
        WalkJson(doc.RootElement, 0, list, 0);
        return list;
    }

    private static void WalkJson(JsonElement el, int inheritedMap, List<MapWarp> list, int depth)
    {
        if (depth > 16)
        {
            return;
        }

        switch (el.ValueKind)
        {
            case JsonValueKind.Array:
                if (TryNumberArray(el, inheritedMap, out MapWarp row))
                {
                    list.Add(row);
                    return;
                }

                foreach (JsonElement item in el.EnumerateArray())
                {
                    WalkJson(item, inheritedMap, list, depth + 1);
                }

                return;
            case JsonValueKind.Object:
                if (TryActivationWarp(el, list))
                {
                    return;
                }

                int mapId = MapIdFromObject(el, inheritedMap);
                bool added = TryJsonWarp(el, mapId, out MapWarp warp);
                if (added)
                {
                    list.Add(warp);
                }

                foreach (JsonProperty prop in el.EnumerateObject())
                {
                    string key = Normalize(prop.Name);
                    if (ListNames.Contains(key))
                    {
                        WalkJson(prop.Value, mapId, list, depth + 1);
                        continue;
                    }

                    if (int.TryParse(prop.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int keyed)
                        && keyed > 0
                        && prop.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                    {
                        WalkJson(prop.Value, keyed, list, depth + 1);
                        continue;
                    }

                    if (!added && prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        WalkJson(prop.Value, mapId, list, depth + 1);
                    }
                }

                return;
        }
    }

    private static bool TryNumberArray(JsonElement el, int inheritedMap, out MapWarp warp)
    {
        warp = default;
        if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() < 4)
        {
            return false;
        }

        var nums = new List<int>();
        foreach (JsonElement item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int n))
            {
                nums.Add(n);
            }
            else if (item.ValueKind == JsonValueKind.String
                && int.TryParse(item.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
            {
                nums.Add(n);
            }
            else
            {
                return false;
            }
        }

        return TryFromNumbers(nums, "", inheritedMap, out warp);
    }

    private static int MapIdFromObject(JsonElement obj, int inherited)
    {
        int explicitMap = IntProp(obj, 0, MapNames);
        if (explicitMap > 0)
        {
            return explicitMap;
        }

        if (inherited > 0)
        {
            return inherited;
        }

        if (HasAny(obj, DestMapNames))
        {
            return 0;
        }

        return IntProp(obj, 0, "number", "no");
    }

    private static bool TryActivationWarp(JsonElement obj, List<MapWarp> list)
    {
        if (!TryProperty(obj, out JsonElement activations, "activations")
            || activations.ValueKind != JsonValueKind.Array
            || !TryProperty(obj, out JsonElement to, "to")
            || to.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        int destMap = AreaId(to);
        (int destX, int destY) = HasLocation(to) ? ReadLocation(to) : (-1, -1);
        string name = StrProp(obj, "name", "description");
        string flag = StrProp(obj, FlagNames);
        int before = list.Count;
        foreach (JsonElement act in activations.EnumerateArray())
        {
            if (act.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            int mapId = AreaId(act);
            if (mapId <= 0)
            {
                mapId = IntProp(obj, 0, "activationmapid", "activationmap");
            }

            (int x, int y) = ReadLocation(act);
            if (mapId <= 0)
            {
                continue;
            }

            list.Add(new MapWarp(mapId, name, x, y, x, y, flag, destMap, destX, destY, WarpOrigin.Import));
        }

        return list.Count > before;
    }

    private static int AreaId(JsonElement obj)
    {
        int id = IntProp(obj, 0, "areaid", "activationmapid", "mapid", "map");
        if (id > 0)
        {
            return id;
        }

        if (TryProperty(obj, out JsonElement loc, "location", "position", "point")
            && loc.ValueKind == JsonValueKind.Object)
        {
            return IntProp(loc, 0, "areaid", "mapid", "map");
        }

        return 0;
    }

    private static bool HasLocation(JsonElement obj)
    {
        if (HasAny(obj, X1Names) || HasAny(obj, Y1Names))
        {
            return true;
        }

        if (TryProperty(obj, out JsonElement loc, "location", "position", "point")
            && loc.ValueKind == JsonValueKind.Object)
        {
            return HasAny(loc, X1Names) || HasAny(loc, Y1Names);
        }

        return false;
    }

    private static (int X, int Y) ReadLocation(JsonElement obj)
    {
        if (HasAny(obj, X1Names) || HasAny(obj, Y1Names))
        {
            return (FirstInt(obj, 0, X1Names), FirstInt(obj, 0, Y1Names));
        }

        if (TryProperty(obj, out JsonElement loc, "location", "position", "point")
            && loc.ValueKind == JsonValueKind.Object)
        {
            return (FirstInt(loc, 0, X1Names), FirstInt(loc, 0, Y1Names));
        }

        return (0, 0);
    }

    private static bool TryJsonWarp(JsonElement obj, int inheritedMap, out MapWarp warp)
    {
        warp = default;
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        JsonElement source = obj;
        if (TryProperty(obj, out JsonElement nestedSource, SourceObjNames)
            && nestedSource.ValueKind == JsonValueKind.Object)
        {
            source = nestedSource;
        }

        JsonElement destObj = obj;
        bool hasDestObj = TryProperty(obj, out JsonElement nestedDest, DestObjNames)
            && nestedDest.ValueKind == JsonValueKind.Object;
        if (hasDestObj)
        {
            destObj = nestedDest;
        }

        int mapId = IntProp(source, 0, MapNames);
        if (mapId <= 0)
        {
            mapId = inheritedMap;
        }

        (int x1, int y1) = ReadLocation(source);
        if (x1 == 0 && y1 == 0)
        {
            (x1, y1) = ReadLocation(obj);
        }

        int x2 = HasAny(source, X2Names) ? FirstInt(source, x1, X2Names) : x1;
        int y2 = HasAny(source, Y2Names) ? FirstInt(source, y1, Y2Names) : y1;
        int destMap = IntProp(destObj, 0, DestMapNames);
        if (destMap <= 0 && hasDestObj)
        {
            destMap = AreaId(destObj);
        }

        int destX = hasDestObj ? FirstInt(destObj, int.MinValue, DestXNames) : FirstInt(obj, int.MinValue, DestXNames);
        int destY = hasDestObj ? FirstInt(destObj, int.MinValue, DestYNames) : FirstInt(obj, int.MinValue, DestYNames);
        if (destX == int.MinValue || destY == int.MinValue)
        {
            (int lx, int ly) = ReadLocation(hasDestObj ? destObj : obj);
            if (destX == int.MinValue)
            {
                destX = hasDestObj ? lx : -1;
            }

            if (destY == int.MinValue)
            {
                destY = hasDestObj ? ly : -1;
            }
        }
        string dest = StrProp(obj, LabelNames);
        if (string.IsNullOrWhiteSpace(dest) && hasDestObj)
        {
            dest = StrProp(destObj, LabelNames);
        }

        string flag = StrProp(obj, FlagNames);
        if (mapId <= 0)
        {
            return false;
        }

        if (!HasAny(source, X1Names) && !HasAny(obj, X1Names) && !hasDestObj)
        {
            return false;
        }

        if (destMap <= 0)
        {
            destMap = WarpTable.GuessMapId(dest);
        }

        warp = new MapWarp(mapId, dest, x1, y1, x2, y2, flag, destMap, destX, destY, WarpOrigin.Import);
        return true;
    }

    private static IReadOnlyList<MapWarp> FromXml(string text)
    {
        var list = new List<MapWarp>();
        XDocument doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        if (doc.Root is null)
        {
            return list;
        }

        WalkXml(doc.Root, 0, list);
        return list;
    }

    private static void WalkXml(XElement el, int inheritedMap, List<MapWarp> list)
    {
        int mapId = AttrInt(el, 0, MapNames);
        if (mapId <= 0)
        {
            mapId = inheritedMap;
        }

        if (TryXmlWarp(el, mapId, out MapWarp warp))
        {
            list.Add(warp);
        }

        foreach (XElement child in el.Elements())
        {
            WalkXml(child, mapId, list);
        }
    }

    private static bool TryXmlWarp(XElement el, int inheritedMap, out MapWarp warp)
    {
        warp = default;
        XElement? destEl = el.Elements().FirstOrDefault(c =>
            DestObjNames.Contains(Normalize(c.Name.LocalName)) || Normalize(c.Name.LocalName) is "destination");
        int mapId = AttrInt(el, inheritedMap, MapNames);
        int x1 = AttrInt(el, 0, X1Names);
        int y1 = AttrInt(el, 0, Y1Names);
        int x2 = AttrInt(el, x1, X2Names);
        int y2 = AttrInt(el, y1, Y2Names);
        int destMap = AttrInt(el, 0, DestMapNames);
        int destX = AttrInt(el, -1, DestXNames);
        int destY = AttrInt(el, -1, DestYNames);
        if (destEl is not null)
        {
            if (destMap <= 0)
            {
                destMap = AttrInt(destEl, 0, DestMapNames.Concat(MapNames).Append("id").ToArray());
            }

            if (destX < 0)
            {
                destX = AttrInt(destEl, -1, DestXNames.Concat(X1Names).ToArray());
            }

            if (destY < 0)
            {
                destY = AttrInt(destEl, -1, DestYNames.Concat(Y1Names).ToArray());
            }
        }

        bool named = Normalize(el.Name.LocalName) is "warp" or "exit" or "portal" or "teleport" or "teleporter";
        if (mapId <= 0 || (!named && destMap <= 0 && destEl is null))
        {
            return false;
        }

        if (!named && !HasAttr(el, X1Names) && destEl is null)
        {
            return false;
        }

        string dest = AttrStr(el, LabelNames);
        warp = new MapWarp(mapId, dest, x1, y1, x2, y2, AttrStr(el, FlagNames), destMap, destX, destY, WarpOrigin.Import);
        return true;
    }

    private static IReadOnlyList<MapWarp> FromSql(string text)
    {
        var list = new List<MapWarp>();
        foreach (Match insert in Regex.Matches(
                     text,
                     @"INSERT\s+INTO\s+[^\s(]+\s*\((.*?)\)\s*VALUES\s*(.+?);",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            string[] columns = SplitSqlList(insert.Groups[1].Value)
                .Select(Normalize)
                .ToArray();
            if (columns.Length == 0)
            {
                continue;
            }

            foreach (string tuple in SplitSqlTuples(insert.Groups[2].Value))
            {
                string[] cells = SplitSqlList(tuple);
                if (TryMapped(columns, cells, 0, out MapWarp warp))
                {
                    list.Add(warp);
                }
            }
        }

        if (list.Count == 0)
        {
            list.AddRange(FromTable(text));
        }

        return list;
    }

    private static IReadOnlyList<MapWarp> FromTable(string text)
    {
        var list = new List<MapWarp>();
        string[] lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        string[]? header = null;
        char delimiter = ',';
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//") || line.StartsWith("--"))
            {
                continue;
            }

            if (header is null && LooksLikeHeader(line))
            {
                delimiter = GuessDelimiter(line);
                header = SplitRow(line, delimiter).Select(Normalize).ToArray();
                continue;
            }

            char rowDelim = header is null ? GuessDelimiter(line) : delimiter;
            string[] cells = SplitRow(line, rowDelim);
            if (header is not null && cells.Length == 1 && rowDelim != ' ')
            {
                cells = SplitRow(line, ' ');
            }

            if (header is not null)
            {
                if (TryMapped(header, cells, 0, out MapWarp mapped))
                {
                    list.Add(mapped);
                }

                continue;
            }

            if (!TryNumbers(cells, out List<int> nums, out string label) || !TryFromNumbers(nums, label, 0, out MapWarp warp))
            {
                continue;
            }

            list.Add(warp);
        }

        return list;
    }

    private static bool LooksLikeHeader(string line)
    {
        string[] cells = SplitRow(line, GuessDelimiter(line));
        if (cells.Length == 0)
        {
            return false;
        }

        string first = Normalize(cells[0]);
        return first.Length > 0
            && !char.IsDigit(first[0])
            && (MapNames.Contains(first) || first.Contains("map") || first.Contains("field") || first.Contains("area")
                || X1Names.Contains(first));
    }

    private static bool TryMapped(string[] columns, string[] cells, int inheritedMap, out MapWarp warp)
    {
        warp = default;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        int n = Math.Min(columns.Length, cells.Length);
        for (int i = 0; i < n; i++)
        {
            values[columns[i]] = cells[i].Trim().Trim('"');
        }

        int MapOf(string[] names, int missing)
        {
            foreach (string name in names)
            {
                if (values.TryGetValue(name, out string? text)
                    && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                {
                    return v;
                }
            }

            return missing;
        }

        string StrOf(string[] names)
        {
            foreach (string name in names)
            {
                if (values.TryGetValue(name, out string? text) && !int.TryParse(text, out _))
                {
                    return text;
                }
            }

            return "";
        }

        int mapId = MapOf(MapNames, inheritedMap);
        int x1 = MapOf(X1Names, 0);
        int y1 = MapOf(Y1Names, 0);
        int x2 = MapOf(X2Names, x1);
        int y2 = MapOf(Y2Names, y1);
        int destMap = MapOf(DestMapNames, 0);
        int destX = MapOf(DestXNames, -1);
        int destY = MapOf(DestYNames, -1);
        string dest = StrOf(LabelNames);
        if (mapId <= 0)
        {
            return false;
        }

        if (destMap <= 0)
        {
            destMap = WarpTable.GuessMapId(dest);
        }

        warp = new MapWarp(mapId, dest, x1, y1, x2, y2, StrOf(FlagNames), destMap, destX, destY, WarpOrigin.Import);
        return true;
    }

    private static bool TryFromNumbers(List<int> nums, string label, int inheritedMap, out MapWarp warp)
    {
        warp = default;
        if (nums.Count < 4)
        {
            return false;
        }

        if (inheritedMap > 0 && nums[0] != inheritedMap && nums.Count >= 3 && nums.Count <= 5)
        {
            nums = [inheritedMap, .. nums];
        }

        MapWarp? parsed = nums.Count switch
        {
            >= 8 => new MapWarp(nums[0], label, nums[1], nums[2], nums[3], nums[4], "", nums[5], nums[6], nums[7], WarpOrigin.Import),
            7 => new MapWarp(nums[0], label, nums[1], nums[2], nums[1], nums[2], "", nums[3], nums[4], nums[5], WarpOrigin.Import),
            6 => new MapWarp(nums[0], label, nums[1], nums[2], nums[1], nums[2], "", nums[3], nums[4], nums[5], WarpOrigin.Import),
            5 => new MapWarp(nums[0], label, nums[1], nums[2], nums[1], nums[2], "", nums[3], nums[4], -1, WarpOrigin.Import),
            4 => new MapWarp(nums[0], label, nums[1], nums[2], nums[1], nums[2], "", nums[3], -1, -1, WarpOrigin.Import),
            _ => null
        };
        if (parsed is not { MapId: > 0 } item)
        {
            return false;
        }

        warp = item;
        return true;
    }

    private static bool TryNumbers(string[] parts, out List<int> nums, out string label)
    {
        nums = [];
        var names = new List<string>();
        foreach (string part in parts)
        {
            if (int.TryParse(part.Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                nums.Add(n);
            }
            else
            {
                names.Add(part.Trim('"'));
            }
        }

        label = string.Join(" ", names);
        return nums.Count >= 4;
    }

    private static char GuessDelimiter(string line)
    {
        int commas = line.Count(c => c == ',');
        int tabs = line.Count(c => c == '\t');
        int semis = line.Count(c => c == ';');
        if (tabs >= commas && tabs >= semis && tabs > 0)
        {
            return '\t';
        }

        if (semis > commas && semis > 0)
        {
            return ';';
        }

        return commas > 0 ? ',' : ' ';
    }

    private static string[] SplitRow(string line, char delimiter)
    {
        if (delimiter == ' ')
        {
            return line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        }

        var cells = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quote = false;
        foreach (char c in line)
        {
            if (c == '"')
            {
                quote = !quote;
                continue;
            }

            if (c == delimiter && !quote)
            {
                cells.Add(cur.ToString());
                cur.Clear();
                continue;
            }

            cur.Append(c);
        }

        cells.Add(cur.ToString());
        return cells.ToArray();
    }

    private static IEnumerable<string> SplitSqlTuples(string values)
    {
        var cur = new System.Text.StringBuilder();
        int depth = 0;
        foreach (char c in values)
        {
            if (c == '(')
            {
                depth++;
                if (depth == 1)
                {
                    cur.Clear();
                    continue;
                }
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    yield return cur.ToString();
                    cur.Clear();
                    continue;
                }
            }

            if (depth > 0)
            {
                cur.Append(c);
            }
        }
    }

    private static string[] SplitSqlList(string text)
    {
        var cells = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quote = false;
        foreach (char c in text)
        {
            if (c is '\'' or '"')
            {
                quote = !quote;
                continue;
            }

            if (c == ',' && !quote)
            {
                cells.Add(cur.ToString().Trim().Trim('`', '[', ']'));
                cur.Clear();
                continue;
            }

            cur.Append(c);
        }

        if (cur.Length > 0)
        {
            cells.Add(cur.ToString().Trim().Trim('`', '[', ']'));
        }

        return cells.ToArray();
    }

    private static string Normalize(string name)
    {
        var chars = name.Where(char.IsLetterOrDigit).ToArray();
        return new string(chars).ToLowerInvariant();
    }

    private static bool TryProperty(JsonElement obj, out JsonElement value, params string[] names)
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

    private static bool HasAny(JsonElement obj, params string[] names)
    {
        foreach (JsonProperty p in obj.EnumerateObject())
        {
            if (names.Contains(Normalize(p.Name)))
            {
                return true;
            }
        }

        return false;
    }

    private static int FirstInt(JsonElement obj, int missing, params string[] names) => IntProp(obj, missing, names);

    private static int IntProp(JsonElement obj, int missing, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return missing;
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

        return missing;
    }

    private static string StrProp(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        foreach (JsonProperty p in obj.EnumerateObject())
        {
            if (names.Contains(Normalize(p.Name)) && p.Value.ValueKind == JsonValueKind.String)
            {
                return p.Value.GetString() ?? "";
            }
        }

        return "";
    }

    private static bool HasAttr(XElement el, params string[] names) =>
        el.Attributes().Any(a => names.Contains(Normalize(a.Name.LocalName)));

    private static int AttrInt(XElement el, int missing, params string[] names)
    {
        foreach (XAttribute attr in el.Attributes())
        {
            if (names.Contains(Normalize(attr.Name.LocalName))
                && int.TryParse(attr.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                return n;
            }
        }

        return missing;
    }

    private static string AttrStr(XElement el, params string[] names)
    {
        foreach (XAttribute attr in el.Attributes())
        {
            if (names.Contains(Normalize(attr.Name.LocalName)))
            {
                return attr.Value;
            }
        }

        return "";
    }
}
