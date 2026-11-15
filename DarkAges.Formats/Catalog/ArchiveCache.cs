using DarkAges.Formats.Data;
using DarkAges.Formats.Unity;

namespace DarkAges.Formats.Catalog;

public sealed class ArchiveCache : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IArchive> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IArchive> _raw = new(StringComparer.OrdinalIgnoreCase);

    public bool Contains(CatalogEntry entry)
    {
        lock (_gate)
        {
            return _open.ContainsKey(CacheKey(entry));
        }
    }

    public IArchive Get(CatalogEntry entry)
    {
        string key = CacheKey(entry);
        lock (_gate)
        {
            if (_open.TryGetValue(key, out IArchive? cached))
            {
                return cached;
            }
        }

        IArchive archive;

        if (entry.IsFolder)
        {
            archive = entry.Open();
        }
        else if (LrarArchive.IsLrarPath(entry.SourcePath))
        {
            archive = LrarArchive.Open(entry.SourcePath, entry.Role);
        }
        else if (UnityBundleArchive.LooksLike(entry.SourcePath))
        {
            archive = new UnityBundleArchive(entry.SourcePath);
        }
        else
        {
            archive = FilteredArchive.WrapIfNeeded(GetRawDat(entry.SourcePath), entry.Role);
        }

        lock (_gate)
        {
            if (_open.TryGetValue(key, out IArchive? raced))
            {
                if (!ReferenceEquals(raced, archive)
                    && archive is not FilteredArchive
                    && !_raw.ContainsValue(archive))
                {
                    archive.Dispose();
                }

                return raced;
            }

            _open[key] = archive;
            return archive;
        }
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

        lock (_gate)
        {
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
        lock (_gate)
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
    }

    private IArchive GetRawDat(string path)
    {
        string full = Path.GetFullPath(path);
        lock (_gate)
        {
            if (_raw.TryGetValue(full, out IArchive? cached))
            {
                return cached;
            }
        }

        var created = new DataArchive(full);
        lock (_gate)
        {
            if (_raw.TryGetValue(full, out IArchive? raced))
            {
                created.Dispose();
                return raced;
            }

            _raw[full] = created;
            return created;
        }
    }

    private static string CacheKey(CatalogEntry entry) =>
        $"{entry.SourcePath}\0{(int)entry.Role}\0{entry.IsFolder}";
}
