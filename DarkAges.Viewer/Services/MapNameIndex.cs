using System.IO;
using DarkAges.Formats.Data;
using DarkAges.Formats.Maps;

namespace DarkAges.Viewer.Services;

public sealed class MapNameIndex
{
    private readonly Dictionary<int, string> _files = [];
    private readonly Dictionary<int, string> _friendly = [];
    private readonly Dictionary<int, AreaName> _areas = [];

    public static MapNameIndex FromWorkspace(AssetWorkspace? workspace)
    {
        var index = new MapNameIndex();
        if (workspace is null)
        {
            return index;
        }

        index.AddFolders(workspace.MapFolders);
        index.AddFriendly(AreaTable.Load(workspace.AreaFolders, AreaStore.FileFor(workspace.ActiveClient)).All());
        return index;
    }

    public void AddFolders(IEnumerable<string> folders)
    {
        foreach (string folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*.map", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (string path in files)
            {
                Add(path);
            }
        }
    }

    public void AddArchives(IEnumerable<IArchive> archives)
    {
        foreach (IArchive archive in archives)
        {
            foreach (ArchiveEntry entry in archive.Entries)
            {
                if (entry.Extension.Equals(".map", StringComparison.OrdinalIgnoreCase))
                {
                    Add(entry.Name);
                }
            }
        }
    }

    public void Add(string mapName)
    {
        int id = WarpTable.TryParseMapId(mapName, out int parsed) ? parsed : WarpTable.GuessMapId(mapName);
        if (id <= 0)
        {
            return;
        }

        string stem = Path.GetFileNameWithoutExtension(mapName.Trim());
        if (string.IsNullOrWhiteSpace(stem))
        {
            return;
        }

        if (!_files.TryGetValue(id, out string? current) || PreferFile(stem, current))
        {
            _files[id] = stem;
        }
    }

    public void AddFriendly(IEnumerable<AreaName> areas)
    {
        foreach (AreaName area in areas)
        {
            if (area.MapId <= 0 || string.IsNullOrWhiteSpace(area.Name) || AreaImport.LooksLikeWarpLabel(area.Name))
            {
                continue;
            }

            _friendly[area.MapId] = area.Name.Trim();
            _areas[area.MapId] = area;
        }
    }

    public AreaName Area(int mapId) =>
        mapId > 0 && _areas.TryGetValue(mapId, out AreaName area) ? area : default;

    public IReadOnlyList<AreaName> Areas() =>
        _areas.Values.OrderBy(a => a.MapId).ToList();

    public string FileName(int mapId) =>
        mapId > 0 && _files.TryGetValue(mapId, out string? name) ? name : "";

    public string FriendlyName(int mapId) =>
        mapId > 0 && _friendly.TryGetValue(mapId, out string? name) ? name : "";

    public string Name(int mapId)
    {
        string friendly = FriendlyName(mapId);
        return string.IsNullOrWhiteSpace(friendly) ? FileName(mapId) : friendly;
    }

    public string Label(int mapId, string? extra = null)
    {
        string friendly = FriendlyName(mapId);
        string file = FileName(mapId);
        if (!string.IsNullOrWhiteSpace(friendly) && !string.IsNullOrWhiteSpace(file)
            && !friendly.Equals(file, StringComparison.OrdinalIgnoreCase))
        {
            return $"{friendly} ({file})";
        }

        if (!string.IsNullOrWhiteSpace(friendly))
        {
            return friendly;
        }

        if (!string.IsNullOrWhiteSpace(file))
        {
            return file;
        }

        if (string.IsNullOrWhiteSpace(extra) || AreaImport.LooksLikeWarpLabel(extra))
        {
            return "";
        }

        return extra.Trim();
    }

    public IReadOnlyList<int> Ids() =>
        _files.Keys.Union(_friendly.Keys).Where(id => id > 0).Distinct().ToList();

    public IReadOnlyList<MapChoice> Choices()
    {
        return _files.Keys.Union(_friendly.Keys)
            .OrderBy(id => id)
            .Select(id => new MapChoice { Id = id, Label = ChoiceLabel(id) })
            .ToList();
    }

    public string ChoiceLabel(int mapId)
    {
        string friendly = FriendlyName(mapId);
        string file = FileName(mapId);
        if (!string.IsNullOrWhiteSpace(friendly) && !string.IsNullOrWhiteSpace(file)
            && !friendly.Equals(file, StringComparison.OrdinalIgnoreCase))
        {
            return $"{mapId}  {friendly}  ({file})";
        }

        if (!string.IsNullOrWhiteSpace(friendly))
        {
            return $"{mapId}  {friendly}";
        }

        if (!string.IsNullOrWhiteSpace(file))
        {
            return $"{mapId}  {file}";
        }

        return mapId > 0 ? mapId.ToString() : "";
    }

    private static bool PreferFile(string candidate, string current)
    {
        bool candLod = candidate.StartsWith("lod", StringComparison.OrdinalIgnoreCase);
        bool curLod = current.StartsWith("lod", StringComparison.OrdinalIgnoreCase);
        if (candLod != curLod)
        {
            return candLod;
        }

        return candidate.Length < current.Length;
    }
}
