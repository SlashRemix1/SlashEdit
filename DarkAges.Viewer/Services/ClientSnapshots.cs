using System.IO;
using DarkAges.Formats.Maps;

namespace DarkAges.Viewer.Services;

public static class ClientSnapshots
{
    private const int KeepPerMap = 20;
    private const int KeepWarps = 20;

    public static string? Folder(ClientProfile? client) => ClientData.Folder(client, "snapshots");

    public static void SaveMap(ClientProfile? client, string mapName, byte[] data)
    {
        string? root = Folder(client);
        if (root is null || data.Length == 0)
        {
            return;
        }

        try
        {
            string maps = Path.Combine(root, "maps");
            Directory.CreateDirectory(maps);
            string stem = SafeStem(mapName);
            string path = Path.Combine(maps, $"{stem}.{Stamp()}.map");
            File.WriteAllBytes(path, data);
            Prune(maps, stem + ".", ".map", KeepPerMap);
        }
        catch
        {
            // ignore snapshot failures
        }
    }

    public static void SaveWarps(ClientProfile? client, WarpTable table)
    {
        string? root = Folder(client);
        if (root is null)
        {
            return;
        }

        try
        {
            string warps = Path.Combine(root, "warps");
            Directory.CreateDirectory(warps);
            string path = Path.Combine(warps, $"warps.{Stamp()}.json");
            table.ToSidecar(client?.Name ?? "").Save(path);
            Prune(warps, "warps.", ".json", KeepWarps);
        }
        catch
        {
            // ignore
        }
    }

    public static IReadOnlyList<string> List(ClientProfile? client)
    {
        string? root = Folder(client);
        if (root is null || !Directory.Exists(root))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(80)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static void Prune(string folder, string prefix, string ext, int keep)
    {
        List<string> files = Directory.EnumerateFiles(folder)
            .Where(p =>
            {
                string name = Path.GetFileName(p);
                return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(ext, StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
        foreach (string extra in files.Skip(keep))
        {
            try
            {
                File.Delete(extra);
            }
            catch
            {
                // keep going
            }
        }
    }

    private static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss");

    private static string SafeStem(string mapName)
    {
        string stem = Path.GetFileNameWithoutExtension(string.IsNullOrWhiteSpace(mapName) ? "map" : mapName);
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            stem = stem.Replace(c, '_');
        }

        return string.IsNullOrWhiteSpace(stem) ? "map" : stem;
    }
}
