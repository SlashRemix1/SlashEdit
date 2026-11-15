using DarkAges.Formats.Data;
using DarkAges.Formats.Unity;

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

    private static bool IsXorTableFolder(string path)
    {
        try
        {
            return Path.GetFileName(path).Equals("Table", StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(path)
                && Directory.EnumerateFiles(path, "*.bin").Any();
        }
        catch
        {
            return false;
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

            if (UnityBundleArchive.LooksLike(SourcePath))
            {
                return new UnityBundleArchive(SourcePath);
            }

            return FilteredArchive.WrapIfNeeded(new DataArchive(SourcePath), Role);
        }

        if (IsXorTableFolder(SourcePath))
        {
            return new Xor55FolderArchive(SourcePath);
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
