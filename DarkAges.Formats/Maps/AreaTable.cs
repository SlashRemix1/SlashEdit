using System.Text.Json;
using System.Text.Json.Serialization;

namespace DarkAges.Formats.Maps;

public readonly record struct AreaName(
    int MapId,
    string Name,
    int Music = 0,
    int Flags = 0,
    int Rows = 0,
    int Cols = 0,
    int LevelRequired = 0,
    bool CantWarp = false,
    string ScriptKey = "",
    int World = 0,
    string LightType = "",
    bool DynamicLighting = false)
{
    public bool HasMeta =>
        Music != 0 || Flags != 0 || Rows != 0 || Cols != 0 || LevelRequired != 0
        || CantWarp || !string.IsNullOrWhiteSpace(ScriptKey) || World != 0
        || !string.IsNullOrWhiteSpace(LightType) || DynamicLighting;
}

public sealed class AreaTable
{
    private readonly Dictionary<int, AreaName> _areas = [];

    public int Count => _areas.Count;

    public IReadOnlyList<AreaName> All() =>
        _areas.OrderBy(p => p.Key).Select(p => p.Value).ToList();

    public AreaName Get(int mapId) =>
        mapId > 0 && _areas.TryGetValue(mapId, out AreaName area) ? area : default;

    public string Name(int mapId) => Get(mapId).Name ?? "";

    public static AreaTable Load(IEnumerable<string>? dumpFolders, string? sidecarPath = null)
    {
        var table = new AreaTable();
        if (dumpFolders is not null)
        {
            foreach (string folder in dumpFolders)
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                {
                    continue;
                }

                try
                {
                    table.AddRange(AreaImport.FromPath(folder).Areas);
                }
                catch
                {
                    // skip unreadable area dumps
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(sidecarPath) && File.Exists(sidecarPath))
        {
            try
            {
                table.AddRange(AreaImport.FromPath(sidecarPath).Areas);
            }
            catch
            {
                // keep dump names if sidecar is broken
            }
        }

        return table;
    }

    public void Add(AreaName area)
    {
        if (area.MapId <= 0 || string.IsNullOrWhiteSpace(area.Name) || AreaImport.LooksLikeWarpLabel(area.Name))
        {
            return;
        }

        AreaName next = area with { Name = area.Name.Trim() };
        if (_areas.TryGetValue(next.MapId, out AreaName prev))
        {
            next = new AreaName(
                next.MapId,
                next.Name,
                next.Music != 0 ? next.Music : prev.Music,
                next.Flags != 0 ? next.Flags : prev.Flags,
                next.Rows != 0 ? next.Rows : prev.Rows,
                next.Cols != 0 ? next.Cols : prev.Cols,
                next.LevelRequired != 0 ? next.LevelRequired : prev.LevelRequired,
                next.CantWarp || prev.CantWarp,
                string.IsNullOrWhiteSpace(next.ScriptKey) ? prev.ScriptKey : next.ScriptKey,
                next.World != 0 ? next.World : prev.World,
                string.IsNullOrWhiteSpace(next.LightType) ? prev.LightType : next.LightType,
                next.DynamicLighting || prev.DynamicLighting);
        }

        _areas[next.MapId] = next;
    }

    public void AddRange(IEnumerable<AreaName> areas)
    {
        foreach (AreaName area in areas)
        {
            Add(area);
        }
    }

    public void Save(string path, string client)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var payload = new AreaSidecar
        {
            Client = client,
            Areas = All().ToList()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, SidecarJson.Options));
    }

    private sealed class AreaSidecar
    {
        public string Client { get; set; } = "";
        public List<AreaName> Areas { get; set; } = [];
    }
}

file static class SidecarJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
