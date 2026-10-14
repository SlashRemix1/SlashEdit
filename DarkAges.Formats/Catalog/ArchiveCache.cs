using DarkAges.Formats.Data;

namespace DarkAges.Formats.Catalog;

public sealed class ArchiveCache : IDisposable
{
    private readonly Dictionary<string, IArchive> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IArchive> _raw = new(StringComparer.OrdinalIgnoreCase);

    public IArchive Get(CatalogEntry entry)
    {
        string key = CacheKey(entry);
        if (_open.TryGetValue(key, out IArchive? archive))
        {
            return archive;
        }

        if (entry.IsFolder)
        {
            archive = entry.Open();
        }
        else if (LrarArchive.IsLrarPath(entry.SourcePath))
        {
            archive = LrarArchive.Open(entry.SourcePath, entry.Role);
        }
        else
        {
            archive = FilteredArchive.WrapIfNeeded(GetRawDat(entry.SourcePath), entry.Role);
        }

        _open[key] = archive;
        return archive;
    }

    public void Evict(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            full = path;
        }

        var keys = _open
            .Where(p => PathsEqual(p.Value.SourcePath, full))
            .Select(p => p.Key)
            .ToList();
        foreach (string key in keys)
        {
            if (_open.Remove(key, out IArchive? archive)
                && archive is not FilteredArchive
                && !_raw.ContainsValue(archive))
            {
                archive.Dispose();
            }
        }

        if (_raw.Remove(full, out IArchive? raw))
        {
            raw.Dispose();
        }
        else
        {
            foreach (string rawKey in _raw.Keys.Where(k => PathsEqual(k, full)).ToList())
            {
                if (_raw.Remove(rawKey, out IArchive? extra))
                {
                    extra.Dispose();
                }
            }
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Dispose()
    {
        foreach (IArchive archive in _open.Values)
        {
            if (archive is FilteredArchive || _raw.ContainsValue(archive))
            {
                continue;
            }

            archive.Dispose();
        }

        foreach (IArchive archive in _raw.Values)
        {
            archive.Dispose();
        }

        _open.Clear();
        _raw.Clear();
    }

    private IArchive GetRawDat(string path)
    {
        string full = Path.GetFullPath(path);
        if (_raw.TryGetValue(full, out IArchive? raw))
        {
            return raw;
        }

        raw = new DataArchive(full);
        _raw[full] = raw;
        return raw;
    }

    private static string CacheKey(CatalogEntry entry) =>
        $"{entry.SourcePath}\0{(int)entry.Role}\0{entry.IsFolder}";
}
