using System.Text;

namespace DarkAges.Formats.Unity;

public sealed class LodTableFile
{
    public IReadOnlyList<string> Headers { get; init; } = [];
    public IReadOnlyList<string[]> Rows { get; init; } = [];
    public string Notes { get; init; } = "";

    public static bool LooksLike(byte[] data)
    {
        if (data.Length < 8)
        {
            return false;
        }

        byte[] decoded = NeedsXor(data) ? Xor55Stream.Apply(data) : data;
        int fields = BitConverter.ToInt32(decoded, 0);
        if (fields is < 1 or > 256)
        {
            return false;
        }

        int pos = 4;
        for (int i = 0; i < Math.Min(fields, 8); i++)
        {
            if (pos >= decoded.Length)
            {
                return false;
            }

            int len = decoded[pos++];
            if (len is < 1 or > 80 || pos + len > decoded.Length)
            {
                return false;
            }

            for (int c = 0; c < len; c++)
            {
                byte b = decoded[pos + c];
                if (b is < 32 or > 126)
                {
                    return false;
                }
            }

            pos += len;
        }

        return true;
    }

    public static LodTableFile Load(byte[] data)
    {
        try
        {
            return LoadCore(data);
        }
        catch (Exception ex)
        {
            return new LodTableFile { Notes = "Failed to parse: " + ex.Message };
        }
    }

    private static LodTableFile LoadCore(byte[] data)
    {
        byte[] decoded = NeedsXor(data) ? Xor55Stream.Apply(data) : data;
        using var stream = new MemoryStream(decoded, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        int fieldCount = reader.ReadInt32();
        if (fieldCount is < 1 or > 256)
        {
            return new LodTableFile { Notes = "Not a LoD R table." };
        }

        var headers = new List<string>(fieldCount);
        for (int i = 0; i < fieldCount; i++)
        {
            int len = reader.ReadByte();
            headers.Add(Encoding.ASCII.GetString(reader.ReadBytes(len)));
        }

        if (stream.Position + 4 > stream.Length)
        {
            return new LodTableFile { Headers = headers, Notes = "No rows." };
        }

        int rowCount = reader.ReadInt32();
        if (rowCount is < 0 or > 500_000)
        {
            return new LodTableFile { Headers = headers, Notes = $"Unreadable row count ({rowCount})." };
        }

        var kinds = headers.Select(Classify).ToArray();
        var rows = new List<string[]>(Math.Min(rowCount, 64_000));
        string note = "";
        int resyncs = 0;
        int maxResyncs = Math.Min(2_000, Math.Max(32, rowCount / 4));
        // Avoid throw/catch for normal bad rows — first-chance exceptions freeze debuggers.
        for (int r = 0; r < rowCount && stream.Position < stream.Length; r++)
        {
            long rowStart = stream.Position;
            if (!TryReadRow(reader, headers.Count, kinds, out string[]? row) || row is null)
            {
                if (!TryResyncToString(reader) || stream.Position <= rowStart)
                {
                    note = $"Parsed {rows.Count} of {rowCount} rows (stopped at 0x{rowStart:X}).";
                    break;
                }

                resyncs++;
                if (resyncs > maxResyncs)
                {
                    note = $"Parsed {rows.Count} of {rowCount} rows ({resyncs} resyncs, capped).";
                    break;
                }

                r--;
                continue;
            }

            rows.Add(row);
        }

        long leftover = stream.Length - stream.Position;
        if (string.IsNullOrEmpty(note) && resyncs > 0)
        {
            note = $"{rows.Count} rows ({resyncs} resyncs)";
        }

        if (leftover > 8)
        {
            note = string.IsNullOrEmpty(note)
                ? $"{rows.Count} rows, {leftover} leftover bytes (some list columns may be approximate)."
                : $"{note}, {leftover} leftover bytes.";
        }

        return new LodTableFile
        {
            Headers = headers,
            Rows = rows,
            Notes = string.IsNullOrWhiteSpace(note) ? $"{rows.Count} rows" : note
        };
    }

    private static bool NeedsXor(byte[] data)
    {
        if (data.Length < 4)
        {
            return false;
        }

        int raw = BitConverter.ToInt32(data, 0);
        if (raw is > 0 and <= 256)
        {
            return false;
        }

        var probe = new byte[4];
        Xor55Stream.Apply(data.AsSpan(0, 4), probe);
        int xor = BitConverter.ToInt32(probe, 0);
        return xor is > 0 and <= 256;
    }

    private static FieldKind Classify(string name)
    {
        if (IsStringField(name))
        {
            return FieldKind.String;
        }

        if (name.Equals("NPCList", StringComparison.OrdinalIgnoreCase))
        {
            return FieldKind.NpcList;
        }

        if (name.EndsWith("List", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Values", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Frames", StringComparison.OrdinalIgnoreCase) && !name.Contains("Delay", StringComparison.OrdinalIgnoreCase))
        {
            return FieldKind.List;
        }

        if (name.Equals("Intensity", StringComparison.OrdinalIgnoreCase)
            || name.Equals("MotionRatio", StringComparison.OrdinalIgnoreCase))
        {
            return FieldKind.Float;
        }

        if (name.Equals("Gender", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Equippable", StringComparison.OrdinalIgnoreCase))
        {
            return FieldKind.Flag;
        }

        if (name.StartsWith("Is", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Can", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Notify", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ExchangeType", StringComparison.OrdinalIgnoreCase))
        {
            return FieldKind.Byte;
        }

        return FieldKind.Int32;
    }

    private static bool IsStringField(string name) =>
        name.Equals("ID", StringComparison.OrdinalIgnoreCase)
        || name.Equals("DisplayName", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Name", StringComparison.OrdinalIgnoreCase)
        || name.Equals("PrefabName", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Description", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ColorHexCode", StringComparison.OrdinalIgnoreCase)
        || name.Equals("UIIconName", StringComparison.OrdinalIgnoreCase)
        || name.Equals("UIDescription", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Key", StringComparison.OrdinalIgnoreCase)
        || name.Equals("AnimationState", StringComparison.OrdinalIgnoreCase);

    private static string ReadValue(BinaryReader reader, FieldKind kind) => kind switch
    {
        FieldKind.String => Read7BitString(reader),
        FieldKind.Float => reader.ReadSingle().ToString("0.###"),
        FieldKind.Byte => reader.ReadByte().ToString(),
        FieldKind.Flag => ReadFlag(reader),
        FieldKind.List => ReadList(reader),
        FieldKind.NpcList => ReadNpcList(reader),
        _ => reader.ReadInt32().ToString()
    };

    private static string ReadList(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count is < 0 or > 10_000)
        {
            return count.ToString();
        }

        if (count == 0)
        {
            return "";
        }

        long pos = reader.BaseStream.Position;
        int peek = reader.BaseStream.ReadByte();
        reader.BaseStream.Position = pos;
        var parts = new List<string>(Math.Min(count, 32));
        if (peek is > 0 and < 80)
        {
            for (int i = 0; i < count; i++)
            {
                if (!TryRead7BitString(reader, out string part))
                {
                    break;
                }

                parts.Add(part);
            }
        }
        else
        {
            for (int i = 0; i < count && reader.BaseStream.Position + 4 <= reader.BaseStream.Length; i++)
            {
                parts.Add(reader.ReadInt32().ToString());
            }
        }

        string extra = count > parts.Count ? $" (+{count - parts.Count})" : "";
        return string.Join(", ", parts.Take(32)) + extra;
    }

    private static bool TryReadRow(BinaryReader reader, int columns, FieldKind[] kinds, out string[]? row)
    {
        row = null;
        long start = reader.BaseStream.Position;
        try
        {
            var values = new string[columns];
            for (int c = 0; c < columns; c++)
            {
                if (!TryReadValue(reader, kinds[c], out string? cell) || cell is null)
                {
                    reader.BaseStream.Position = start;
                    return false;
                }

                values[c] = cell;
            }

            if (kinds[0] == FieldKind.String
                && (values[0].Length is < 1 or > 80 || !LooksLikeName(values[0])))
            {
                reader.BaseStream.Position = start;
                return false;
            }

            row = values;
            return true;
        }
        catch
        {
            reader.BaseStream.Position = start;
            return false;
        }
    }

    private static bool TryReadValue(BinaryReader reader, FieldKind kind, out string? value)
    {
        value = null;
        long start = reader.BaseStream.Position;
        try
        {
            value = kind switch
            {
                FieldKind.String => TryRead7BitString(reader, out string s) ? s : null,
                FieldKind.Byte => reader.BaseStream.Position < reader.BaseStream.Length
                    ? reader.ReadByte().ToString()
                    : null,
                FieldKind.Float => reader.BaseStream.Position + 4 <= reader.BaseStream.Length
                    ? reader.ReadSingle().ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : null,
                FieldKind.Flag => ReadFlag(reader),
                FieldKind.List => ReadList(reader),
                FieldKind.NpcList => ReadNpcList(reader),
                _ => reader.BaseStream.Position + 4 <= reader.BaseStream.Length
                    ? reader.ReadInt32().ToString()
                    : null
            };
            return value is not null;
        }
        catch
        {
            reader.BaseStream.Position = start;
            value = null;
            return false;
        }
    }

    private static bool TryResyncToString(BinaryReader reader)
    {
        Stream stream = reader.BaseStream;
        long end = Math.Min(stream.Length, stream.Position + 65_536);
        while (stream.Position < end)
        {
            long pos = stream.Position;
            if (TryRead7BitString(reader, out string value)
                && value.Length is >= 2 and <= 80
                && LooksLikeName(value))
            {
                stream.Position = pos;
                return true;
            }

            stream.Position = pos + 1;
        }

        return false;
    }

    private static bool LooksLikeName(string value)
    {
        int ok = 0;
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c) || c is '(' or ')' or '+' or '-' or '[' or ']' or '_' or ' '
                || c >= 0x1100)
            {
                ok++;
            }
        }

        return ok >= value.Length * 3 / 4;
    }

    private static string ReadFlag(BinaryReader reader)
    {
        long pos = reader.BaseStream.Position;
        byte first = reader.ReadByte();
        if (first == 0)
        {
            return "0";
        }

        if (first == 1 && reader.BaseStream.Position < reader.BaseStream.Length)
        {
            byte next = reader.ReadByte();
            if (next is (byte)'0' or (byte)'1')
            {
                return ((char)next).ToString();
            }

            reader.BaseStream.Position = pos + 1;
            return "1";
        }

        reader.BaseStream.Position = pos;
        return TryRead7BitString(reader, out string text) ? text : "";
    }

    private static string ReadNpcList(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count is < 0 or > 5_000)
        {
            return count.ToString();
        }

        if (count == 0)
        {
            return "";
        }

        var parts = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            if (!TryRead7BitString(reader, out string name)
                || reader.BaseStream.Position + 8 > reader.BaseStream.Length)
            {
                break;
            }

            int a = reader.ReadInt32();
            int b = reader.ReadInt32();
            parts.Add($"{name} ({a},{b})");
        }

        return string.Join("; ", parts);
    }

    private static string Read7BitString(BinaryReader reader) =>
        TryRead7BitString(reader, out string value) ? value : "";

    private static bool TryRead7BitString(BinaryReader reader, out string value)
    {
        value = "";
        Stream stream = reader.BaseStream;
        if (stream.Position >= stream.Length)
        {
            return false;
        }

        int n = 0;
        int shift = 0;
        byte b;
        do
        {
            if (stream.Position >= stream.Length)
            {
                return false;
            }

            b = reader.ReadByte();
            n |= (b & 0x7F) << shift;
            shift += 7;
        }
        while ((b & 0x80) != 0 && shift < 35);

        if (n is < 0 or > 512)
        {
            return false;
        }

        if (n == 0)
        {
            value = "";
            return true;
        }

        if (stream.Position + n > stream.Length)
        {
            return false;
        }

        value = Encoding.UTF8.GetString(reader.ReadBytes(n));
        return true;
    }

    private enum FieldKind
    {
        Int32,
        String,
        Byte,
        Float,
        Flag,
        List,
        NpcList
    }
}
