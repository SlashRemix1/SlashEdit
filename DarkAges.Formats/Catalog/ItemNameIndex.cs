using DarkAges.Formats.Data;

namespace DarkAges.Formats.Catalog;

public sealed class ItemNameIndex
{
    private readonly Dictionary<int, List<string>> _names = [];
    private readonly Dictionary<int, string> _descriptions = [];
    private readonly Dictionary<string, EncyclopediaRow> _encyclopedia = new(StringComparer.OrdinalIgnoreCase);

    public int NamedSpriteCount => _names.Count;
    public int EncyclopediaCount => _encyclopedia.Count;

    public static ItemNameIndex Load(IEnumerable<IArchive> archives)
    {
        var index = new ItemNameIndex();
        foreach (IArchive archive in archives)
        {
            foreach (ArchiveEntry entry in archive.Entries)
            {
                string stem = Path.GetFileName(entry.Name);
                try
                {
                    if (!MetaFile.LooksLike(entry))
                    {
                        continue;
                    }

                    if (stem.StartsWith("ItemDesc", StringComparison.OrdinalIgnoreCase))
                    {
                        index.AddDescriptions(MetaFile.Load(entry, archive.SourcePath));
                    }
                    else if (stem.StartsWith("ItemInfo", StringComparison.OrdinalIgnoreCase))
                    {
                        index.AddEncyclopedia(MetaFile.Load(entry, archive.SourcePath));
                    }
                }
                catch
                {
                    // skip unreadable meta
                }
            }
        }

        return index;
    }

    public IReadOnlyList<string> Names(int graphicId) =>
        _names.TryGetValue(graphicId, out List<string>? list) ? list : [];

    public string PrimaryName(int graphicId)
    {
        IReadOnlyList<string> names = Names(graphicId);
        return names.Count == 0 ? "" : names[0];
    }

    public string Description(int graphicId) =>
        _descriptions.TryGetValue(graphicId, out string? text) ? text : "";

    public bool TryEncyclopedia(string? name, out EncyclopediaRow row)
    {
        row = default;
        return !string.IsNullOrWhiteSpace(name) && _encyclopedia.TryGetValue(name.Trim(), out row);
    }

    private void AddDescriptions(MetaFile meta)
    {
        foreach (MetaNode node in meta.Nodes)
        {
            if (node.Properties.Count == 0 || !int.TryParse(node.Properties[0], out int id) || id <= 0)
            {
                continue;
            }

            string name = node.Name.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!_names.TryGetValue(id, out List<string>? list))
            {
                list = [];
                _names[id] = list;
            }

            if (!list.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(name);
            }

            if (node.Properties.Count > 1 && !_descriptions.ContainsKey(id))
            {
                string desc = node.Properties[1].Trim();
                if (desc.Length > 0)
                {
                    _descriptions[id] = desc;
                }
            }
        }
    }

    private void AddEncyclopedia(MetaFile meta)
    {
        foreach (MetaNode node in meta.Nodes)
        {
            string name = node.Name.Trim();
            if (string.IsNullOrWhiteSpace(name) || int.TryParse(name, out _))
            {
                continue;
            }

            IReadOnlyList<string> p = node.Properties;
            _encyclopedia[name] = new EncyclopediaRow(
                name,
                At(p, 0),
                At(p, 1),
                At(p, 2),
                At(p, 3),
                At(p, 4));
        }
    }

    private static string At(IReadOnlyList<string> properties, int i) =>
        i < properties.Count ? properties[i] : "";
}

public readonly record struct EncyclopediaRow(
    string Name,
    string Level,
    string Class,
    string Weight,
    string Category,
    string Description);
