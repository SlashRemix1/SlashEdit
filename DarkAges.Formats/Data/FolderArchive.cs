using System.Collections;
using DarkAges.Formats.IO;

namespace DarkAges.Formats.Data;

public sealed class FolderArchive : IArchive
{
    private readonly List<ArchiveEntry> _entries = new();
    private readonly Dictionary<string, ArchiveEntry> _byName = new(StringComparer.OrdinalIgnoreCase);

    public FolderArchive(string directory, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        SourcePath = directory;
        DisplayName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(directory, searchPattern, searchOption))
        {
            if (!MatchesPattern(file, searchPattern))
            {
                continue;
            }

            var info = new FileInfo(file);
            long length = info.Length;
            if (length > int.MaxValue)
            {
                continue;
            }

            string name = RelativeName(directory, file);
            if (_byName.ContainsKey(name))
            {
                continue;
            }

            var entry = new ArchiveEntry(name, (int)length, () => new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read));
            _entries.Add(entry);
            _byName[name] = entry;
        }

        _entries.Sort((a, b) => NaturalStringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
    }

    public string SourcePath { get; }
    public string DisplayName { get; }
    public IReadOnlyList<ArchiveEntry> Entries => _entries;
    public ArchiveEntry? GetEntry(string name)
    {
        _byName.TryGetValue(name, out ArchiveEntry? entry);
        return entry;
    }

    public IEnumerator<ArchiveEntry> GetEnumerator() => _entries.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() { }

    public static bool MatchesPattern(string path, string searchPattern)
    {
        if (string.IsNullOrWhiteSpace(searchPattern) || searchPattern == "*")
        {
            return true;
        }

        if (searchPattern.StartsWith("*.", StringComparison.Ordinal)
            && searchPattern.Length > 2
            && !searchPattern.AsSpan(2).Contains('*')
            && !searchPattern.AsSpan(2).Contains('?'))
        {
            return Path.GetExtension(path).Equals(searchPattern[1..], StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static string RelativeName(string directory, string file)
    {
        string relative = Path.GetRelativePath(directory, file);
        if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith("..", StringComparison.Ordinal))
        {
            return Path.GetFileName(file);
        }

        return relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }
}
