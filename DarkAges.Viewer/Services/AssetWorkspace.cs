using System.IO;
using DarkAges.Formats.Catalog;

namespace DarkAges.Viewer.Services;

public sealed class AssetWorkspace
{
    public AssetWorkspace(AppSettings settings)
    {
        Settings = settings;
    }

    public AppSettings Settings { get; }

    public IReadOnlyList<string> Roots => Settings.AllRoots().ToList();

    public IReadOnlyList<ClientProfile> Clients
    {
        get
        {
            Settings.MigrateClients();
            return Settings.Clients
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public ClientProfile? ActiveClient
    {
        get
        {
            string path = Settings.ActiveClient;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Settings.RootFolder;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                string full = Path.GetFullPath(path);
                return Clients.FirstOrDefault(c =>
                    string.Equals(Path.GetFullPath(c.Path), full, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return null;
            }
        }
    }

    public IReadOnlyList<string> MapFolders => DatCatalog.DiscoverMapFolders(Roots);

    public IReadOnlyList<string> MetaFolders => DatCatalog.DiscoverMetaFolders(Roots);

    public IReadOnlyList<string> WarpFolders => DatCatalog.DiscoverWarpFolders(Roots);

    public IReadOnlyList<string> AreaFolders => DatCatalog.DiscoverAreaFolders(Roots);

    public bool AddFolder(string path) => AddClient(path);

    public bool AddClient(string path, string? name = null)
    {
        if (!Settings.TryAddClient(path, name))
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return false;
            }
        }

        SetRoot(path);
        return true;
    }

    public void SetRoot(string path)
    {
        string full = Path.GetFullPath(path);
        Settings.TryAddClient(full);
        Settings.ActiveClient = full;
        Settings.RootFolder = full;
        Settings.Save();
    }
}
