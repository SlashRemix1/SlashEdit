using DarkAges.Formats.Catalog;

namespace DarkAges.Viewer.Services;

public sealed class FavoriteEntry
{
    public string SourcePath { get; set; } = "";
    public ArchiveRole Role { get; set; }
    public bool IsFolder { get; set; }
    public string EntryName { get; set; } = "";
    public int? FrameIndex { get; set; }
    public string Label { get; set; } = "";

    public bool SameAs(FavoriteEntry other) =>
        string.Equals(SourcePath, other.SourcePath, StringComparison.OrdinalIgnoreCase)
        && Role == other.Role
        && IsFolder == other.IsFolder
        && string.Equals(EntryName, other.EntryName, StringComparison.OrdinalIgnoreCase)
        && FrameIndex == other.FrameIndex;
}
