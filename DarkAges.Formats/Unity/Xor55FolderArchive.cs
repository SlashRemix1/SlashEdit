using System.Collections;
using DarkAges.Formats.Data;
using DarkAges.Formats.IO;

namespace DarkAges.Formats.Unity;

public sealed class Xor55FolderArchive : IArchive
{
    private readonly List<ArchiveEntry> _entries = [];
    private readonly Dictionary<string, ArchiveEntry> _byName = new(StringComparer.OrdinalIgnoreCase);

    public Xor55FolderArchive(string directory, string searchPattern = "*.bin")
    {
        SourcePath = directory;
        DisplayName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly))
        {
            var info = new FileInfo(file);
            if (info.Length > int.MaxValue)
            {
                continue;
            }

            string name = Path.GetFileName(file);
            if (_byName.ContainsKey(name))
            {
                continue;
            }

            string captured = file;
            var entry = new ArchiveEntry(name, (int)info.Length, () => OpenXor(captured));
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

    private static Stream OpenXor(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        return new MemoryStream(Xor55Stream.Apply(raw), writable: false);
    }
}
