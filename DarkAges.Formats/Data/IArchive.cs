namespace DarkAges.Formats.Data;

public interface IArchive : IDisposable, IEnumerable<ArchiveEntry>
{
    string SourcePath { get; }
    string DisplayName { get; }
    IReadOnlyList<ArchiveEntry> Entries { get; }
    ArchiveEntry? GetEntry(string name);
}
