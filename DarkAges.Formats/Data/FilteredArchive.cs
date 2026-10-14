using System.Collections;
using DarkAges.Formats.Catalog;

namespace DarkAges.Formats.Data;

/// <summary>
/// Role slice of a combined DAT (one file holding floors, walls, mobs, items, …).
/// <see cref="GetEntry"/> still sees the full archive so palettes resolve.
/// </summary>
public sealed class FilteredArchive : IArchive
{
    private readonly IArchive _inner;
    private readonly Dictionary<string, ArchiveEntry> _byName;

    private FilteredArchive(IArchive inner, ArchiveRole role, IReadOnlyList<ArchiveEntry> entries)
    {
        _inner = inner;
        Role = role;
        SourcePath = inner.SourcePath;
        DisplayName = inner.DisplayName;
        Entries = entries;
        _byName = new Dictionary<string, ArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (ArchiveEntry entry in entries)
        {
            _byName.TryAdd(entry.Name, entry);
        }
    }

    public ArchiveRole Role { get; }
    public IArchive Inner => _inner;
    public string SourcePath { get; }
    public string DisplayName { get; }
    public IReadOnlyList<ArchiveEntry> Entries { get; }

    public ArchiveEntry? GetEntry(string name)
    {
        if (_byName.TryGetValue(name, out ArchiveEntry? entry))
        {
            return entry;
        }

        ArchiveEntry? inner = _inner.GetEntry(name);
        if (inner is null)
        {
            return null;
        }

        string ext = inner.Extension;
        if (ext.Equals(".pal", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".tbl", StringComparison.OrdinalIgnoreCase))
        {
            return inner;
        }

        return null;
    }

    public IEnumerator<ArchiveEntry> GetEnumerator() => Entries.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() { }

    public static IArchive WrapIfNeeded(IArchive archive, ArchiveRole role)
    {
        if (role is ArchiveRole.Unknown || !ArchiveClassifier.IsCombined(archive))
        {
            return archive;
        }

        List<ArchiveEntry> entries = archive.Entries.Where(e => ArchiveClassifier.MatchesRole(e.Name, role)).ToList();
        if (entries.Count == 0 || entries.Count == archive.Entries.Count)
        {
            return archive;
        }

        return new FilteredArchive(archive, role, entries);
    }
}
