using DarkAges.Formats.Data;

namespace DarkAges.Formats.Catalog;

public sealed class ItemNameIndex
{
    private readonly Dictionary<int, List<string>> _names = [];
    private readonly Dictionary<int, string> _descriptions = [];
    private readonly Dictionary<string, EncyclopediaRow> _encyclopedia = new(StringComparer.OrdinalIgnoreCase);

    public int NamedSpriteCount => _names.Count;
    public int EncyclopediaCount => _encyclopedia.Count;

    public static ItemNameIndex Load(IEnumerable<IArchive> archives) =>
        Load(archives, progress: null, ct: default);

    public static ItemNameIndex Load(
        IEnumerable<IArchive> archives,
        IProgress<(int Done, int Total, string Name)>? progress,
        CancellationToken ct = default)
    {
        var list = archives as IList<IArchive> ?? archives.ToList();
        var index = new ItemNameIndex();
        int total = Math.Max(1, list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            IArchive archive = list[i];
            string label = Path.GetFileName(archive.SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(label))
            {
                label = archive.DisplayName;
            }

            progress?.Report((i, total, label));
            foreach (ArchiveEntry entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                string stem = Path.GetFileNameWithoutExtension(entry.Name);
                // Name gate first — MetaFile.LooksLike opens every file (very slow on fat meta folders).
                bool itemDesc = stem.StartsWith("ItemDesc", StringComparison.OrdinalIgnoreCase);
                bool itemInfo = stem.StartsWith("ItemInfo", StringComparison.OrdinalIgnoreCase);
                if (!itemDesc && !itemInfo)
                {
                    continue;
                }

                try
                {
                    if (!MetaFile.LooksLike(entry))
                    {
                        continue;
                    }

                    if (itemDesc)
                    {
                        index.AddDescriptions(MetaFile.Load(entry, archive.SourcePath));
                    }
                    else
                    {
                        index.AddEncyclopedia(MetaFile.Load(entry, archive.SourcePath));
                    }
                }
                catch
                {
                    // skip unreadable meta
                }
            }

            progress?.Report((i + 1, total, label));
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

    public IReadOnlyList<ItemTextHit> SearchText(string query, int limit = 80)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        string q = query.Trim();
        var hits = new List<ItemTextHit>();
        foreach ((int id, List<string> names) in _names)
        {
            if (hits.Count >= limit)
            {
                break;
            }

            string? name = names.FirstOrDefault(n => SearchLexicon.Matches(n, q));
            string desc = Description(id);
            bool descHit = SearchLexicon.Matches(desc, q);
            if (name is null && !descHit)
            {
                continue;
            }

            hits.Add(new ItemTextHit(id, name ?? names[0], descHit ? desc : ""));
        }

        foreach ((string name, EncyclopediaRow row) in _encyclopedia)
        {
            if (hits.Count >= limit)
            {
                break;
            }

            if (SearchLexicon.Matches(name, q)
                || SearchLexicon.Matches(row.Description, q)
                || SearchLexicon.Matches(row.Category, q))
            {
                if (hits.Any(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                hits.Add(new ItemTextHit(0, name, row.Description));
            }
        }

        return hits;
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

public readonly record struct ItemTextHit(int GraphicId, string Name, string Description);
