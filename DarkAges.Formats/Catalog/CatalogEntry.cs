using DarkAges.Formats.Data;

namespace DarkAges.Formats.Catalog;

public class CatalogEntry
{
    public string SourcePath { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string? Variant { get; init; }
    public ArchiveRole Role { get; init; }
    public bool IsFolder { get; init; }
    public int EntryCount { get; init; }
    public string Title
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Variant))
            {
                return $"{DisplayName} — {Variant}";
            }

            return DisplayName;
        }
    }

    public IArchive Open()
    {
        if (!IsFolder)
        {
            if (LrarArchive.IsLrarPath(SourcePath))
            {
                return LrarArchive.Open(SourcePath, Role);
            }

            return FilteredArchive.WrapIfNeeded(new DataArchive(SourcePath), Role);
        }

        return Role switch
        {
            ArchiveRole.Maps => new FolderArchive(SourcePath, "*.map", SearchOption.AllDirectories),
            ArchiveRole.Cinematics => new FolderArchive(SourcePath, "*.bik", SearchOption.AllDirectories),
            ArchiveRole.Metadata => new FolderArchive(SourcePath, "*"),
            _ => new FolderArchive(SourcePath)
        };
    }
}
