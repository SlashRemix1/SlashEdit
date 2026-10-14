using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DarkAges.Formats.Maps;

public sealed class AreaImportResult
{
    public string Format { get; init; } = "unknown";
    public string Source { get; init; } = "";
    public IReadOnlyList<AreaName> Areas { get; init; } = [];
}

public static class AreaImport
{
    private static readonly string[] IdNames =
        ["id", "areaid", "mapid", "map", "number", "no", "fieldnumber", "field", "mapnumber", "mapno", "templatekey"];

    private static readonly string[] NameNames =
        ["name", "displayname", "title", "mapname", "areaname", "label", "description"];

    private static readonly string[] ListNames =
        ["areas", "maps", "worlds", "fields", "locations", "entries"];

    private static readonly string[] WarpKeys =
        ["activations", "warps", "exits", "portals", "teleports", "teleporters"];

    private static readonly string[] SkipNestKeys =
        ["areaobjectcache", "reaper", "objects", "monsters", "npcs", "spawns", "mobs", "items"];

    public static IReadOnlyList<AreaName> FromFile(string path) => FromPath(path).Areas;

    public static IReadOnlyList<AreaName> FromText(string text) => ParseText(text, "").Areas;

    public static AreaImportResult FromPath(string path)
    {
        if (Directory.Exists(path))
        {
            return FromDirectory(path);
        }

        return ParseText(Decode(File.ReadAllBytes(path)), path);
    }

    public static bool LooksLikeWarpLabel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        string value = text.Trim();
        return value.Contains("warp", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(value, @"\d+[_\s]+\d+\s+to\s+\d+", RegexOptions.IgnoreCase);
    }

    private static AreaImportResult FromDirectory(string path)
    {
        var list = new List<AreaName>();
        var formats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories);
        }
        catch
        {
            return new AreaImportResult { Format = "folder", Source = path, Areas = [] };
        }

        foreach (string file in files)
        {
            string ext = Path.GetExtension(file);
            if (ext is not (".json" or ".csv" or ".txt" or ".tsv" or ".xml" or ".sql" or ""))
            {
                continue;
            }

            try
            {
                AreaImportResult one = ParseText(Decode(File.ReadAllBytes(file)), file);
                if (one.Areas.Count == 0)
                {
                    continue;
                }

                list.AddRange(one.Areas);
                formats.Add(one.Format);
            }
            catch
            {
                // skip unreadable copies
            }
        }

        string format = formats.Count == 0 ? "folder" : "folder (" + string.Join(", ", formats.OrderBy(s => s)) + ")";
        return Result(format, path, Dedup(list));
    }

    private static AreaImportResult ParseText(string text, string source)
    {
        text = text.TrimStart('\uFEFF').Trim();
        if (text.Length == 0)
        {
            return Result("empty", source, []);
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

        IReadOnlyList<AreaName> table = FromTable(text);
        return Result(table.Count > 0 ? "table" : "unknown", source, table);
    }

    private static AreaImportResult Result(string format, string source, IReadOnlyList<AreaName> areas) =>
        new() { Format = format, Source = source, Areas = areas };

    private static IReadOnlyList<AreaName> FromJson(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var list = new List<AreaName>();
        WalkJson(doc.RootElement, list, 0);
        return Dedup(list);
    }

    private static void WalkJson(JsonElement el, List<AreaName> list, int depth)
    {
        if (depth > 16)
        {
            return;
        }

        switch (el.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (JsonElement item in el.EnumerateArray())
                {
                    WalkJson(item, list, depth + 1);
                }

                return;
            case JsonValueKind.Object:
                if (LooksLikeWarpObject(el))
                {
                    return;
                }

                if (TryArea(el, out AreaName area))
                {
                    list.Add(area);
                    return;
                }

                string tableKind = KindOf(el);
                bool unityAreas = tableKind is "areas" or "maps" or "worlds" or "fields" or "locations" or "entries";

                foreach (JsonProperty prop in el.EnumerateObject())
                {
                    if (int.TryParse(prop.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int keyed)
                        && keyed > 0)
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String)
                        {
                            string named = prop.Value.GetString() ?? "";
                            if (!LooksLikeWarpLabel(named))
                            {
                                list.Add(new AreaName(keyed, named.Trim()));
                            }

                            continue;
                        }

                        if (prop.Value.ValueKind == JsonValueKind.Object
                            && !TryArea(prop.Value, out _)
                            && !LooksLikeWarpObject(prop.Value))
                        {
                            string nested = StrProp(prop.Value, NameNames);
                            if (!LooksLikeWarpLabel(nested))
                            {
                                list.Add(new AreaName(keyed, nested.Trim()));
                                continue;
                            }
                        }
                    }

                    string key = Normalize(prop.Name);
                    if ((SkipNestKeys.Contains(key) && !(unityAreas && key == "items"))
                        || key.StartsWith("reaper", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (ListNames.Contains(key) || prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        WalkJson(prop.Value, list, depth + 1);
                    }
                }

                return;
        }
    }

    private static bool TryArea(JsonElement obj, out AreaName area)
    {
        area = default;
        int id = IntProp(obj, 0, IdNames);
        string name = StrProp(obj, NameNames);
        if (id <= 0 || string.IsNullOrWhiteSpace(name) || LooksLikeWarpLabel(name))
        {
            return false;
        }

        area = new AreaName(
            id,
            name.Trim(),
            IntProp(obj, 0, "music", "bgm", "song"),
            IntProp(obj, 0, "flags", "flag"),
            IntProp(obj, 0, "rows", "height"),
            IntProp(obj, 0, "cols", "columns", "width"),
            IntProp(obj, 0, "levelrequired", "minlevel", "level"),
            BoolProp(obj, "cantwarp", "nowarp"),
            StrProp(obj, "scriptkey", "script"),
            IntProp(obj, 0, "world"),
            StrProp(obj, "lighttype", "light", "lighting"),
            BoolProp(obj, "dynamiclighting", "dynamiclight"));
        return true;
    }

    private static bool LooksLikeWarpObject(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (JsonProperty prop in obj.EnumerateObject())
        {
            string key = Normalize(prop.Name);
            if (WarpKeys.Contains(key))
            {
                return true;
            }

            if (key is "to" or "destination" or "target" && HasLocation(prop.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasLocation(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (JsonProperty prop in el.EnumerateObject())
        {
            if (Normalize(prop.Name) is "location" or "x" or "y")
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<AreaName> FromXml(string text)
    {
        var list = new List<AreaName>();
        XDocument doc = XDocument.Parse(text);
        foreach (XElement el in doc.Descendants())
        {
            int id = AttrOrChildInt(el, IdNames);
            string name = AttrOrChildStr(el, NameNames);
            if (id > 0 && !string.IsNullOrWhiteSpace(name) && !LooksLikeWarpLabel(name))
            {
                list.Add(new AreaName(id, name.Trim()));
            }
        }

        return Dedup(list);
    }

    private static IReadOnlyList<AreaName> FromTable(string text)
    {
        string[] lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return [];
        }

        char delimiter = GuessDelimiter(lines[0]);
        string[] header = SplitRow(lines[0], delimiter).Select(Normalize).ToArray();
        bool named = header.Any(h => IdNames.Contains(h)) && header.Any(h => NameNames.Contains(h));
        if (named && header.Any(h => h.Contains("dest") || h is "x" or "y" or "x1" or "y1"))
        {
            return [];
        }

        var list = new List<AreaName>();
        int start = named ? 1 : 0;
        for (int i = start; i < lines.Length; i++)
        {
            string[] cells = SplitRow(lines[i], delimiter);
            if (named)
            {
                int id = CellInt(header, cells, IdNames);
                string name = CellStr(header, cells, NameNames);
                if (id > 0 && !LooksLikeWarpLabel(name) && name.Any(char.IsLetter))
                {
                    list.Add(new AreaName(id, name.Trim()));
                }

                continue;
            }

            int numbers = cells.Count(c =>
                int.TryParse(c.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _));
            if (numbers >= 4
                || cells.Length < 2
                || !int.TryParse(cells[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int mapId)
                || mapId <= 0)
            {
                continue;
            }

            string label = cells[1].Trim().Trim('"');
            if (!LooksLikeWarpLabel(label) && label.Any(char.IsLetter))
            {
                list.Add(new AreaName(mapId, label));
            }
        }

        return Dedup(list);
    }

    private static IReadOnlyList<AreaName> FromSql(string text)
    {
        var list = new List<AreaName>();
        foreach (Match insert in Regex.Matches(text, @"insert\s+into\s+[^\s(]+(?:\s*\(([^)]*)\))?\s*values\s*(.+?);",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            string[] columns = SplitSqlList(insert.Groups[1].Value).Select(Normalize).ToArray();
            foreach (string tuple in SplitSqlTuples(insert.Groups[2].Value))
            {
                string[] cells = SplitSqlList(tuple);
                int id;
                string name;
                if (columns.Length > 0 && columns.Any(c => IdNames.Contains(c)))
                {
                    id = CellInt(columns, cells, IdNames);
                    name = CellStr(columns, cells, NameNames);
                }
                else if (cells.Length >= 2
                    && int.TryParse(cells[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                {
                    name = cells[1];
                }
                else
                {
                    continue;
                }

                if (id > 0 && !string.IsNullOrWhiteSpace(name) && !LooksLikeWarpLabel(name))
                {
                    list.Add(new AreaName(id, name.Trim()));
                }
            }
        }

        return Dedup(list);
    }

    private static List<AreaName> Dedup(List<AreaName> list)
    {
        var names = new Dictionary<int, AreaName>();
        foreach (AreaName area in list)
        {
            if (area.MapId > 0 && !string.IsNullOrWhiteSpace(area.Name))
            {
                names[area.MapId] = area;
            }
        }

        return names.OrderBy(p => p.Key).Select(p => p.Value).ToList();
    }

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

        return System.Text.Encoding.UTF8.GetString(data);
    }

    private static bool LooksLikeSql(string text)
    {
        string head = text.Length > 4000 ? text[..4000] : text;
        return head.Contains("INSERT", StringComparison.OrdinalIgnoreCase)
            && head.Contains("VALUES", StringComparison.OrdinalIgnoreCase);
    }

    private static char GuessDelimiter(string line)
    {
        int commas = line.Count(c => c == ',');
        int tabs = line.Count(c => c == '\t');
        if (tabs > commas)
        {
            return '\t';
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

    private static int CellInt(string[] header, string[] cells, string[] names)
    {
        for (int i = 0; i < header.Length && i < cells.Length; i++)
        {
            if (names.Contains(header[i])
                && int.TryParse(cells[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                return n;
            }
        }

        return 0;
    }

    private static string CellStr(string[] header, string[] cells, string[] names)
    {
        for (int i = 0; i < header.Length && i < cells.Length; i++)
        {
            if (names.Contains(header[i]))
            {
                return cells[i].Trim().Trim('"');
            }
        }

        return "";
    }

    private static string KindOf(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        foreach (JsonProperty prop in obj.EnumerateObject())
        {
            if (Normalize(prop.Name) == "kind" && prop.Value.ValueKind == JsonValueKind.String)
            {
                return Normalize(prop.Value.GetString() ?? "");
            }
        }

        return "";
    }

    private static string Normalize(string name)
    {
        var chars = name.Where(char.IsLetterOrDigit).ToArray();
        return new string(chars).ToLowerInvariant();
    }

    private static int IntProp(JsonElement obj, int missing, params string[] names)
    {
        foreach (string name in names)
        {
            foreach (JsonProperty p in obj.EnumerateObject())
            {
                if (Normalize(p.Name) != name)
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
        }

        return missing;
    }

    private static bool BoolProp(JsonElement obj, params string[] names)
    {
        foreach (string name in names)
        {
            foreach (JsonProperty p in obj.EnumerateObject())
            {
                if (Normalize(p.Name) != name)
                {
                    continue;
                }

                if (p.Value.ValueKind is JsonValueKind.True)
                {
                    return true;
                }

                if (p.Value.ValueKind is JsonValueKind.False)
                {
                    return false;
                }

                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int n))
                {
                    return n != 0;
                }

                if (p.Value.ValueKind == JsonValueKind.String)
                {
                    string text = p.Value.GetString() ?? "";
                    if (text.Equals("true", StringComparison.OrdinalIgnoreCase)
                        || text.Equals("yes", StringComparison.OrdinalIgnoreCase)
                        || text == "1")
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static string StrProp(JsonElement obj, params string[] names)
    {
        foreach (string name in names)
        {
            foreach (JsonProperty p in obj.EnumerateObject())
            {
                if (Normalize(p.Name) == name && p.Value.ValueKind == JsonValueKind.String)
                {
                    return p.Value.GetString() ?? "";
                }
            }
        }

        return "";
    }

    private static int AttrOrChildInt(XElement el, string[] names)
    {
        foreach (XAttribute attr in el.Attributes())
        {
            if (names.Contains(Normalize(attr.Name.LocalName))
                && int.TryParse(attr.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                return n;
            }
        }

        foreach (XElement child in el.Elements())
        {
            if (names.Contains(Normalize(child.Name.LocalName))
                && int.TryParse(child.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                return n;
            }
        }

        return 0;
    }

    private static string AttrOrChildStr(XElement el, string[] names)
    {
        foreach (XAttribute attr in el.Attributes())
        {
            if (names.Contains(Normalize(attr.Name.LocalName)))
            {
                return attr.Value;
            }
        }

        foreach (XElement child in el.Elements())
        {
            if (names.Contains(Normalize(child.Name.LocalName)))
            {
                return child.Value;
            }
        }

        return "";
    }
}
