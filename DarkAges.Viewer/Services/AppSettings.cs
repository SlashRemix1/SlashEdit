using System.IO;
using System.Text.Json;

namespace DarkAges.Viewer.Services;

public sealed class AppSettings
{
    public string RootFolder { get; set; } = "";
    public List<string> ExtraFolders { get; set; } = [];
    public List<ClientProfile> Clients { get; set; } = [];
    public string ActiveClient { get; set; } = "";
    public string FfmpegPath { get; set; } = "";
    public string RadVideoPath { get; set; } = "";

    private static string PathName =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DarkAgesViewer", "settings.json");

    public void MigrateClients()
    {
        foreach (string? path in new[] { RootFolder, ActiveClient }.Concat(ExtraFolders).Concat(Clients.Select(c => c.Path)))
        {
            TryAddClient(path);
        }

        if (Clients.Count == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ActiveClient) || !Directory.Exists(ActiveClient))
        {
            ActiveClient = Clients[0].Path;
        }

        RootFolder = ActiveClient;
    }

    public bool TryAddClient(string? path, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        string full = Path.GetFullPath(path);
        ClientProfile? existing = Clients.FirstOrDefault(c =>
            string.Equals(Path.GetFullPath(c.Path), full, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                existing.Name = name;
            }

            return false;
        }

        Clients.Add(new ClientProfile
        {
            Name = string.IsNullOrWhiteSpace(name) ? ClientProfile.NameFromPath(full) : name,
            Path = full
        });
        return true;
    }

    public IEnumerable<string> AllRoots()
    {
        MigrateClients();
        if (!string.IsNullOrWhiteSpace(ActiveClient) && Directory.Exists(ActiveClient))
        {
            yield return Path.GetFullPath(ActiveClient);
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(RootFolder) && Directory.Exists(RootFolder))
        {
            yield return Path.GetFullPath(RootFolder);
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(PathName))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathName)) ?? new AppSettings();
            }
        }
        catch
        {
            // ignore
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            MigrateClients();
            ExtraFolders = ExtraFolders
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Directory.Exists)
                .ToList();
            Clients = Clients
                .Where(c => !string.IsNullOrWhiteSpace(c.Path) && Directory.Exists(c.Path))
                .GroupBy(c => Path.GetFullPath(c.Path), StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    ClientProfile keep = g.First();
                    keep.Favorites ??= [];
                    keep.PaintMacros ??= [];
                    keep.ItemNames ??= [];
                    return keep;
                })
                .ToList();
            File.WriteAllText(PathName, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // ignore
        }
    }
}
