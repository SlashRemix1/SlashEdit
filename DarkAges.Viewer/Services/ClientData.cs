using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DarkAges.Viewer.Services;

public static class ClientData
{
    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DarkAgesViewer");

    public static string? Key(ClientProfile? client)
    {
        if (client is null || string.IsNullOrWhiteSpace(client.Path))
        {
            return null;
        }

        string hash = Convert.ToHexString(SHA1.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(client.Path).ToLowerInvariant())))[..12];
        string safe = string.Join("_", (client.Name ?? "client").Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "client";
        }

        return $"{safe}-{hash}";
    }

    public static string? Folder(ClientProfile? client, string leaf)
    {
        string? key = Key(client);
        if (key is null)
        {
            return null;
        }

        string path = Path.Combine(Root, leaf, key);
        Directory.CreateDirectory(path);
        return path;
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
