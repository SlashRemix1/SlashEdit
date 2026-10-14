using System.IO;
using DarkAges.Formats.Data;
using DarkAges.Formats.Maps;

namespace DarkAges.Viewer.Services;

public static class MapSizeIndex
{
    public static Dictionary<int, (int Width, int Height)> Load(
        IEnumerable<string>? folders,
        IEnumerable<IArchive>? packed,
        IProgress<(int Done, int Total, string Name)>? progress = null)
    {
        var jobs = new List<(string Name, Func<byte[]> Read)>();
        if (folders is not null)
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
                    string file = path;
                    jobs.Add((file, () => File.ReadAllBytes(file)));
                }
            }
        }

        if (packed is not null)
        {
            foreach (IArchive archive in packed)
            {
                foreach (ArchiveEntry entry in archive.Entries)
                {
                    if (!entry.Extension.Equals(".map", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    ArchiveEntry captured = entry;
                    jobs.Add((captured.Name, captured.ReadAllBytes));
                }
            }
        }

        var sizes = new Dictionary<int, (int Width, int Height)>();
        for (int i = 0; i < jobs.Count; i++)
        {
            (string name, Func<byte[]> read) = jobs[i];
            progress?.Report((i + 1, jobs.Count, Path.GetFileName(name)));
            TryAdd(sizes, name, read);
        }

        return sizes;
    }

    private static void TryAdd(Dictionary<int, (int Width, int Height)> sizes, string name, Func<byte[]> read)
    {
        int id = WarpTable.TryParseMapId(name, out int parsed) ? parsed : WarpTable.GuessMapId(name);
        if (id <= 0 || sizes.ContainsKey(id))
        {
            return;
        }

        try
        {
            if (MapFile.TryPeekSize(read(), out int width, out int height))
            {
                sizes[id] = (width, height);
            }
        }
        catch
        {
            // skip unreadable maps
        }
    }
}
