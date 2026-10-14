using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace DarkAges.Formats.IO;

public sealed class TableDump
{
    public TableDump(string table, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        Table = SanitizeIdent(table, "Dump");
        Columns = columns.Select(c => SanitizeIdent(c, "Col")).ToList();
        Rows = rows.Select(r => (IReadOnlyList<object?>)Pad(r, Columns.Count)).ToList();
    }

    public string Table { get; }
    public IReadOnlyList<string> Columns { get; }
    public IReadOnlyList<IReadOnlyList<object?>> Rows { get; }

    public static TableDump Create(string table, IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<object?>> rows) =>
        new(table, columns, rows.Select(r => (IReadOnlyList<object?>)r.ToList()).ToList());

    public string Render(string? pathOrExtension)
    {
        string ext = Path.GetExtension(pathOrExtension ?? "").ToLowerInvariant();
        if (string.IsNullOrEmpty(ext) && pathOrExtension is not null && pathOrExtension.StartsWith('.'))
        {
            ext = pathOrExtension.ToLowerInvariant();
        }

        return ext switch
        {
            ".csv" => ToCsv(),
            ".sql" => ToSql(),
            _ => ToJson()
        };
    }

    public void Write(string path) => File.WriteAllText(path, Render(path));

    public string ToJson()
    {
        var items = new JsonArray();
        foreach (IReadOnlyList<object?> row in Rows)
        {
            var obj = new JsonObject();
            for (int i = 0; i < Columns.Count; i++)
            {
                obj[Camel(Columns[i])] = JsonOf(i < row.Count ? row[i] : null);
            }

            items.Add(obj);
        }

        var columns = new JsonArray();
        foreach (string column in Columns)
        {
            columns.Add(Camel(column));
        }

        var root = new JsonObject
        {
            ["version"] = 1,
            ["kind"] = Camel(Table),
            ["count"] = Rows.Count,
            ["columns"] = columns,
            ["items"] = items
        };
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true }))
        {
            root.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string ToCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Columns.Select(Csv)));
        foreach (IReadOnlyList<object?> row in Rows)
        {
            var cells = new string[Columns.Count];
            for (int i = 0; i < Columns.Count; i++)
            {
                cells[i] = Csv(Cell(i < row.Count ? row[i] : null));
            }

            sb.AppendLine(string.Join(",", cells));
        }

        return sb.ToString();
    }

    public string ToSql()
    {
        var sb = new StringBuilder();
        sb.Append("-- ").Append(Table).Append(' ').Append(Rows.Count).AppendLine(" row(s)");
        if (Rows.Count == 0)
        {
            return sb.ToString();
        }

        string colList = string.Join(", ", Columns);
        const int batch = 250;
        for (int start = 0; start < Rows.Count; start += batch)
        {
            int end = Math.Min(start + batch, Rows.Count);
            sb.Append("INSERT INTO ").Append(Table).Append(" (").Append(colList).AppendLine(") VALUES");
            for (int i = start; i < end; i++)
            {
                IReadOnlyList<object?> row = Rows[i];
                sb.Append("  (");
                for (int c = 0; c < Columns.Count; c++)
                {
                    if (c > 0)
                    {
                        sb.Append(", ");
                    }

                    sb.Append(SqlLiteral(c < row.Count ? row[c] : null));
                }

                sb.Append(i + 1 == end ? ");" : "),").AppendLine();
            }
        }

        return sb.ToString();
    }

    private static List<object?> Pad(IReadOnlyList<object?> row, int count)
    {
        var list = new List<object?>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(i < row.Count ? row[i] : null);
        }

        return list;
    }

    private static string SanitizeIdent(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var sb = new StringBuilder(name.Length);
        foreach (char ch in name.Trim())
        {
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }

        string ident = sb.ToString().Trim('_');
        if (ident.Length == 0)
        {
            return fallback;
        }

        return char.IsDigit(ident[0]) ? "_" + ident : ident;
    }

    private static string Camel(string ident)
    {
        if (string.IsNullOrEmpty(ident))
        {
            return ident;
        }

        if (ident.Length == 1)
        {
            return ident.ToLowerInvariant();
        }

        return char.ToLowerInvariant(ident[0]) + ident[1..];
    }

    private static JsonNode? JsonOf(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        byte or sbyte or short or ushort or int or uint or long or ulong => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        float or double or decimal => JsonValue.Create(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture))
    };

    private static string Cell(object? value) => value switch
    {
        null => "",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };

    private static string Csv(string value)
    {
        if (value.IndexOfAny(['"', ',', '\r', '\n']) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string SqlLiteral(object? value) => value switch
    {
        null => "NULL",
        bool b => b ? "1" : "0",
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
            Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL",
        _ => "'" + Cell(value).Replace("'", "''") + "'"
    };
}
