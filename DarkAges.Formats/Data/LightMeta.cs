using System.Globalization;
using System.Text.RegularExpressions;

namespace DarkAges.Formats.Data;

public sealed class LightSlot
{
    public int EnumValue { get; set; }
    public byte StartHour { get; set; }
    public byte EndHour { get; set; }
    public byte A { get; set; }
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }

    public string HexKey => EnumValue.ToString("X", CultureInfo.InvariantCulture);
}

public sealed class LightType
{
    public string Name { get; set; } = "Default";
    public LightSlot[] Slots { get; } = CreateSlots();

    public static LightSlot[] CreateSlots()
    {
        var slots = new LightSlot[12];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = new LightSlot
            {
                EnumValue = i,
                StartHour = (byte)(i * 2),
                EndHour = (byte)((i * 2 + 2) % 24)
            };
        }

        return slots;
    }
}

public sealed class LightMeta
{
    private static readonly Regex SlotName = new(
        @"^(?<name>.+)_(?<slot>[0-9A-Fa-f])$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public string FileName { get; set; } = "Light";
    public List<LightType> Types { get; } = [];
    public Dictionary<int, string> Maps { get; } = [];

    public static bool IsLightName(string? name)
    {
        string stem = Path.GetFileNameWithoutExtension(name ?? "");
        return stem.Equals("light", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("light", StringComparison.OrdinalIgnoreCase);
    }

    public static LightMeta Default(string fileName = "Light")
    {
        var meta = new LightMeta { FileName = fileName };
        meta.Types.Add(new LightType { Name = "Default" });
        return meta;
    }

    public static LightMeta FromMetaFile(MetaFile file)
    {
        var meta = new LightMeta { FileName = file.Name };
        var types = new Dictionary<string, LightType>(StringComparer.OrdinalIgnoreCase);
        foreach (MetaNode node in file.Nodes)
        {
            if (TrySlot(node, out string typeName, out LightSlot slot))
            {
                if (!types.TryGetValue(typeName, out LightType? type))
                {
                    type = new LightType { Name = typeName };
                    types[typeName] = type;
                    meta.Types.Add(type);
                }

                int index = Math.Clamp(slot.EnumValue, 0, 11);
                type.Slots[index] = slot;
                continue;
            }

            if (TryMap(node, out int mapId, out string lightType))
            {
                meta.Maps[mapId] = lightType;
            }
        }

        if (meta.Types.Count == 0)
        {
            meta.Types.Add(new LightType { Name = "Default" });
        }

        return meta;
    }

    public MetaFile ToMetaFile()
    {
        var nodes = new List<MetaNode>();
        foreach (LightType type in Types)
        {
            string name = string.IsNullOrWhiteSpace(type.Name) ? "Default" : type.Name.Trim();
            foreach (LightSlot slot in type.Slots)
            {
                int enumValue = Math.Clamp(slot.EnumValue, 0, 11);
                nodes.Add(new MetaNode(
                    $"{name}_{enumValue:X}",
                    [
                        slot.StartHour.ToString(CultureInfo.InvariantCulture),
                        slot.EndHour.ToString(CultureInfo.InvariantCulture),
                        slot.A.ToString(CultureInfo.InvariantCulture),
                        slot.R.ToString(CultureInfo.InvariantCulture),
                        slot.G.ToString(CultureInfo.InvariantCulture),
                        slot.B.ToString(CultureInfo.InvariantCulture)
                    ]));
            }
        }

        foreach ((int mapId, string type) in Maps.OrderBy(p => p.Key))
        {
            if (mapId <= 0 || string.IsNullOrWhiteSpace(type))
            {
                continue;
            }

            nodes.Add(new MetaNode(mapId.ToString(CultureInfo.InvariantCulture), [type.Trim()]));
        }

        return new MetaFile(FileName, nodes);
    }

    public byte[] Save(string? encodingHint = null) => ToMetaFile().Save(encodingHint ?? FileName);

    public LightType AddType(string name)
    {
        string next = UniqueTypeName(name);
        var type = new LightType { Name = next };
        Types.Add(type);
        return type;
    }

    public void Assign(int mapId, string typeName)
    {
        if (mapId <= 0 || string.IsNullOrWhiteSpace(typeName))
        {
            return;
        }

        Maps[mapId] = typeName.Trim();
    }

    public string TypeFor(int mapId) =>
        mapId > 0 && Maps.TryGetValue(mapId, out string? name) ? name : "";

    private string UniqueTypeName(string name)
    {
        string stem = string.IsNullOrWhiteSpace(name) ? "Light" : name.Trim();
        string candidate = stem;
        int n = 2;
        while (Types.Any(t => t.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{stem}{n++}";
        }

        return candidate;
    }

    private static bool TrySlot(MetaNode node, out string typeName, out LightSlot slot)
    {
        typeName = "";
        slot = new LightSlot();
        Match match = SlotName.Match(node.Name ?? "");
        if (!match.Success || node.Properties.Count < 6)
        {
            return false;
        }

        typeName = match.Groups["name"].Value;
        if (!int.TryParse(match.Groups["slot"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int enumValue))
        {
            return false;
        }

        slot = new LightSlot
        {
            EnumValue = Math.Clamp(enumValue, 0, 11),
            StartHour = ByteAt(node.Properties, 0),
            EndHour = ByteAt(node.Properties, 1),
            A = ByteAt(node.Properties, 2),
            R = ByteAt(node.Properties, 3),
            G = ByteAt(node.Properties, 4),
            B = ByteAt(node.Properties, 5)
        };
        return !string.IsNullOrWhiteSpace(typeName);
    }

    private static bool TryMap(MetaNode node, out int mapId, out string lightType)
    {
        mapId = 0;
        lightType = "";
        if (!int.TryParse((node.Name ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out mapId)
            || mapId <= 0)
        {
            return false;
        }

        lightType = node.Properties.Count > 0 ? node.Properties[0].Trim() : "";
        return !string.IsNullOrWhiteSpace(lightType);
    }

    private static byte ByteAt(IReadOnlyList<string> props, int index)
    {
        if (index >= props.Count
            || !byte.TryParse(props[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte value))
        {
            return 0;
        }

        return value;
    }
}
