using System.IO;
using DarkAges.Formats.Maps;

namespace DarkAges.Viewer.Services;

public static class WarpStore
{
    public static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DarkAgesViewer", "warps");

    public static string? FileFor(ClientProfile? client)
    {
        if (client is null || string.IsNullOrWhiteSpace(client.Path))
        {
            return null;
        }

        string key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(client.Path).ToLowerInvariant())))[..12];
        string safe = string.Join("_", (client.Name ?? "client").Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "client";
        }

        return Path.Combine(DirectoryPath, $"{safe}-{key}.json");
    }

    public static void Save(ClientProfile? client, WarpTable table)
    {
        string? path = FileFor(client);
        if (path is null)
        {
            throw new InvalidOperationException("Load a client first so warps can be saved per version.");
        }

        table.ToSidecar(client?.Name ?? "").Save(path);
    }
}
