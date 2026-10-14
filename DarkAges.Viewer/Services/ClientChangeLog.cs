using System.IO;
using System.Text.Json;

namespace DarkAges.Viewer.Services;

public readonly record struct ChangeLogEntry(DateTimeOffset Time, string Kind, string Detail);

public static class ClientChangeLog
{
    public static void Append(ClientProfile? client, string kind, string detail)
    {
        string? path = FilePath(client);
        if (path is null || string.IsNullOrWhiteSpace(detail))
        {
            return;
        }

        try
        {
            string line = JsonSerializer.Serialize(
                new ChangeLogEntry(DateTimeOffset.Now, kind, detail.Trim()),
                ClientData.Json);
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch
        {
            // keep editing even if the log folder is locked
        }
    }

    public static IReadOnlyList<ChangeLogEntry> Load(ClientProfile? client, int take = 400)
    {
        string? path = FilePath(client);
        if (path is null || !File.Exists(path))
        {
            return [];
        }

        try
        {
            var list = new List<ChangeLogEntry>();
            foreach (string line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    ChangeLogEntry? entry = JsonSerializer.Deserialize<ChangeLogEntry>(line, ClientData.Json);
                    if (entry is ChangeLogEntry row && !string.IsNullOrWhiteSpace(row.Detail))
                    {
                        list.Add(row);
                    }
                }
                catch
                {
                    // skip a broken line
                }
            }

            if (list.Count > take)
            {
                list = list.Skip(list.Count - take).ToList();
            }

            list.Reverse();
            return list;
        }
        catch
        {
            return [];
        }
    }

    public static string? FilePath(ClientProfile? client)
    {
        string? folder = ClientData.Folder(client, "changelog");
        return folder is null ? null : Path.Combine(folder, "changes.jsonl");
    }

    public static string? FolderPath(ClientProfile? client) => ClientData.Folder(client, "changelog");
}
