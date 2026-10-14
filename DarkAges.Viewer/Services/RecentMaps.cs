using System.IO;
using System.Text.Json;

namespace DarkAges.Viewer.Services;

public static class RecentMaps
{
    private const int Keep = 12;

    public static IReadOnlyList<int> Load(ClientProfile? client)
    {
        string? path = FilePath(client);
        if (path is null || !File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<int>>(File.ReadAllText(path), ClientData.Json)
                ?.Where(id => id > 0)
                .Distinct()
                .Take(Keep)
                .ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static IReadOnlyList<int> Touch(ClientProfile? client, int mapId)
    {
        if (mapId <= 0)
        {
            return Load(client);
        }

        var list = Load(client).Where(id => id != mapId).Prepend(mapId).Take(Keep).ToList();
        string? path = FilePath(client);
        if (path is not null)
        {
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(list, ClientData.Json));
            }
            catch
            {
                // ignore
            }
        }

        return list;
    }

    private static string? FilePath(ClientProfile? client)
    {
        string? folder = ClientData.Folder(client, "recent");
        return folder is null ? null : Path.Combine(folder, "maps.json");
    }
}
