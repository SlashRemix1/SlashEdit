using System.IO.Compression;
using System.Text;

namespace DarkAges.Formats.Data;

public sealed class MetaFile
{
    public MetaFile(string name, IReadOnlyList<MetaNode> nodes)
    {
        Name = name;
        Nodes = nodes;
    }

    public string Name { get; set; }
    public IReadOnlyList<MetaNode> Nodes { get; set; }

    public static bool LooksLike(byte[] data) =>
        data.Length >= 2 && data[0] == 0x78 && (data[1] == 0x9C || data[1] == 0x01 || data[1] == 0xDA);

    public static bool IsKnownName(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        return StartsWithAny(
            stem,
            "iteminfo", "itemdesc", "nationdesc", "npcillust", "worldmap",
            "skilldesc", "spelldesc", "metainf", "serverinfo", "eventdesc",
            "tmap", "smonster", "light");
    }

    public static bool IsMapTable(string name) =>
        Path.GetFileNameWithoutExtension(name).StartsWith("tmap", StringComparison.OrdinalIgnoreCase);

    private static bool StartsWithAny(string stem, params string[] prefixes)
    {
        foreach (string prefix in prefixes)
        {
            if (stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool LooksLike(ArchiveEntry entry)
    {
        try
        {
            using Stream stream = entry.Open();
            int a = stream.ReadByte();
            int b = stream.ReadByte();
            return a == 0x78 && b is 0x9C or 0x01 or 0xDA;
        }
        catch
        {
            return false;
        }
    }

    public static MetaFile Load(ArchiveEntry entry, string? encodingHint = null) =>
        Load(entry.ReadAllBytes(), entry.Name, encodingHint);

    public static MetaFile Load(byte[] compressed, string name, string? encodingHint = null)
    {
        byte[] raw = Inflate(compressed);
        Encoding encoding = MetaText.ForPath(encodingHint ?? name);
        if (ReferenceEquals(encoding, MetaText.Latin))
        {
            encoding = MetaText.Best(raw);
        }

        using var stream = new MemoryStream(raw);
        using var reader = new BinaryReader(stream, Encoding.Latin1, leaveOpen: true);
        int count = ReadUInt16Be(reader);
        var nodes = new List<MetaNode>(count);
        for (int i = 0; i < count && stream.Position < stream.Length; i++)
        {
            int nameLen = reader.ReadByte();
            string nodeName = encoding.GetString(reader.ReadBytes(nameLen));
            int props = ReadUInt16Be(reader);
            var properties = new List<string>(props);
            for (int p = 0; p < props && stream.Position < stream.Length; p++)
            {
                int len = ReadUInt16Be(reader);
                int take = Math.Min(len, (int)(stream.Length - stream.Position));
                properties.Add(encoding.GetString(reader.ReadBytes(take)));
            }

            nodes.Add(new MetaNode(nodeName, properties));
        }

        return new MetaFile(name, nodes);
    }

    public IReadOnlyList<string> Headers()
    {
        int fields = Nodes.Count == 0 ? 0 : Nodes.Max(n => n.Properties.Count);
        string stem = Path.GetFileName(Name);
        if (stem.StartsWith("ItemInfo", StringComparison.OrdinalIgnoreCase))
        {
            return Pad(["Name", "Level", "Class", "Weight", "Category", "Description"], fields + 1);
        }

        if (stem.StartsWith("NPCIllust", StringComparison.OrdinalIgnoreCase))
        {
            return Pad(["NPC", "Sprite"], fields + 1);
        }

        if (stem.StartsWith("NationDesc", StringComparison.OrdinalIgnoreCase))
        {
            return Pad(["Key", "Nation"], fields + 1);
        }

        if (stem.StartsWith("SClass", StringComparison.OrdinalIgnoreCase))
        {
            return Pad(["Name", "Req", "Icon", "Stats", "Prereq", "Extra", "Extra2"], fields + 1);
        }

        if (stem.StartsWith("SEvent", StringComparison.OrdinalIgnoreCase))
        {
            return Pad(["Key", "Value"], fields + 1);
        }

        if (stem.StartsWith("Light", StringComparison.OrdinalIgnoreCase))
        {
            return Pad(["Name", "StartHour", "EndHour", "A", "R", "G", "B"], fields + 1);
        }

        if (stem.StartsWith("SMonster", StringComparison.OrdinalIgnoreCase))
        {
            return Pad(["Name", "Sprite", "Level", "Field", "Description"], fields + 1);
        }

        if (stem.StartsWith("TMap", StringComparison.OrdinalIgnoreCase))
        {
            return Pad(["MapId", "Warp"], fields + 1);
        }

        if (stem.StartsWith("SkillSpell", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("SClass", StringComparison.OrdinalIgnoreCase))
        {
            return Pad(["Name", "Req", "Icon", "Stats", "Prereq", "Extra", "Extra2"], fields + 1);
        }

        var headers = new List<string> { "Name" };
        for (int i = 1; i <= fields; i++)
        {
            headers.Add($"Field {i}");
        }

        return headers;
    }

    public IReadOnlyList<string[]> Rows()
    {
        IReadOnlyList<string> headers = Headers();
        var rows = new List<string[]>(Nodes.Count);
        foreach (MetaNode node in Nodes)
        {
            var row = new string[headers.Count];
            row[0] = node.Name;
            for (int i = 0; i < node.Properties.Count && i + 1 < row.Length; i++)
            {
                row[i + 1] = node.Properties[i];
            }

            for (int i = 0; i < row.Length; i++)
            {
                row[i] ??= "";
            }

            rows.Add(row);
        }

        return rows;
    }

    private static string[] Pad(string[] preferred, int columns)
    {
        if (preferred.Length >= columns)
        {
            return preferred;
        }

        var list = preferred.ToList();
        while (list.Count < columns)
        {
            list.Add($"Field {list.Count}");
        }

        return list.ToArray();
    }

    public byte[] Save(string? encodingHint = null)
    {
        Encoding encoding = EncodingForSave(encodingHint ?? Name);
        using var raw = new MemoryStream();
        using (var writer = new BinaryWriter(raw, Encoding.Latin1, leaveOpen: true))
        {
            WriteUInt16Be(writer, Nodes.Count);
            foreach (MetaNode node in Nodes)
            {
                byte[] nameBytes = encoding.GetBytes(node.Name ?? "");
                if (nameBytes.Length > 255)
                {
                    Array.Resize(ref nameBytes, 255);
                }

                writer.Write((byte)nameBytes.Length);
                writer.Write(nameBytes);
                WriteUInt16Be(writer, node.Properties.Count);
                foreach (string property in node.Properties)
                {
                    byte[] prop = encoding.GetBytes(property ?? "");
                    if (prop.Length > 65535)
                    {
                        Array.Resize(ref prop, 65535);
                    }

                    WriteUInt16Be(writer, prop.Length);
                    writer.Write(prop);
                }
            }
        }

        byte[] payload = raw.ToArray();
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(payload);
        }

        return output.ToArray();
    }

    private Encoding EncodingForSave(string hint)
    {
        Encoding hinted = MetaText.ForPath(hint);
        string sample = string.Concat(Nodes.Select(n => n.Name + string.Concat(n.Properties)));
        if (sample.Any(c => c is >= '\uAC00' and <= '\uD7AF'))
        {
            return MetaText.Korean;
        }

        if (sample.Any(c => c is >= '\u3040' and <= '\u30FF'))
        {
            return MetaText.Japanese;
        }

        return hinted;
    }

    private static int ReadUInt16Be(BinaryReader reader) =>
        (reader.ReadByte() << 8) | reader.ReadByte();

    private static void WriteUInt16Be(BinaryWriter writer, int value)
    {
        writer.Write((byte)((value >> 8) & 0xFF));
        writer.Write((byte)(value & 0xFF));
    }

    private static byte[] Inflate(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }
}

public sealed class MetaNode
{
    public MetaNode(string name, IReadOnlyList<string> properties)
    {
        Name = name;
        Properties = properties;
    }

    public string Name { get; set; }
    public IReadOnlyList<string> Properties { get; set; }
}
