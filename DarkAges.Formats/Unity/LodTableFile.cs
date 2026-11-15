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
        var rows = new List<string[]>(rowCount);
        string note = "";
        int resyncs = 0;
        try
        {
            for (int r = 0; r < rowCount && stream.Position < stream.Length; r++)
            {
                long rowStart = stream.Position;
                try
                {
                    var row = new string[headers.Count];
                    for (int c = 0; c < headers.Count; c++)
                    {
                        row[c] = ReadValue(reader, kinds[c]);
                    }

                    if (kinds[0] == FieldKind.String
                        && (row[0].Length is < 1 or > 80 || !LooksLikeName(row[0])))
                    {
                        throw new InvalidDataException("Unreadable ID");
                    }

                    rows.Add(row);
                }
                catch (Exception ex)
                {
                    if (!TryResyncToString(reader) || stream.Position <= rowStart)
                    {
                        throw new InvalidDataException(
                            $"row {r} at 0x{rowStart:X}: {ex.Message}",
                            ex);
                    }

                    resyncs++;
                    if (resyncs > rowCount)
                    {
                        throw new InvalidDataException(
                            $"row {r} at 0x{rowStart:X}: {ex.Message}",
                            ex);
                    }

                    r--;
                }
            }

            long leftover = stream.Length - stream.Position;
            if (resyncs > 0)
            {
                note = $"{rows.Count} rows ({resyncs} resyncs)";
            }

            if (leftover > 8)
            {
                note = string.IsNullOrEmpty(note)
                    ? $"{rows.Count} rows, {leftover} leftover bytes (some list columns may be approximate)."
                    : $"{note}, {leftover} leftover bytes.";
            }
        }
        catch (Exception ex)
        {
            note = $"Parsed {rows.Count} of {rowCount} rows. {ex.Message}";
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
                parts.Add(Read7BitString(reader));
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                parts.Add(reader.ReadInt32().ToString());
            }
        }

        string extra = count > parts.Count ? $" (+{count - parts.Count})" : "";
        return string.Join(", ", parts.Take(32)) + extra;
    }

    private static bool TryResyncToString(BinaryReader reader)
    {
        Stream stream = reader.BaseStream;
        long end = Math.Min(stream.Length, stream.Position + 65_536);
        while (stream.Position < end)
        {
            long pos = stream.Position;
            try
            {
                string value = Read7BitString(reader);
                if (value.Length is >= 2 and <= 80 && LooksLikeName(value))
                {
                    stream.Position = pos;
                    return true;
                }
            }
            catch
            {
                // keep scanning
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
        return Read7BitString(reader);
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
            string name = Read7BitString(reader);
            int a = reader.ReadInt32();
            int b = reader.ReadInt32();
            parts.Add($"{name} ({a},{b})");
        }

        return string.Join("; ", parts);
    }

    private static string Read7BitString(BinaryReader reader)
    {
        int n = 0;
        int shift = 0;
        byte b;
        do
        {
            b = reader.ReadByte();
            n |= (b & 0x7F) << shift;
            shift += 7;
        }
        while ((b & 0x80) != 0 && shift < 35);

        if (n is < 0 or > 512)
        {
            throw new InvalidDataException($"String length {n}");
        }

        return n == 0 ? "" : Encoding.UTF8.GetString(reader.ReadBytes(n));
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
