using System.IO;
using DarkAges.Formats.Catalog;
using DarkAges.Formats.Data;

namespace DarkAges.Viewer.Services;

public static class DatSave
{
    public static bool IsWritable(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return false;
        }

        if (LrarArchive.IsLrarPath(sourcePath))
        {
            return false;
        }

        return File.Exists(sourcePath) || Directory.Exists(sourcePath);
    }

    public static bool IsWritable(CatalogEntry? catalog) =>
        catalog is not null && IsWritable(catalog.SourcePath);

    public static int Write(ArchiveCache? cache, string sourcePath, IReadOnlyList<(string Name, byte[] Data)> files)
    {
        if (files.Count == 0)
        {
            throw new InvalidOperationException("Nothing to save.");
        }

        if (LrarArchive.IsLrarPath(sourcePath))
        {
            throw new InvalidOperationException("Legends AoC LRAR packs are read-only.");
        }

        int written;
        ClientFamily family = ClientFamilies.FromPath(sourcePath);
        if (Directory.Exists(sourcePath))
        {
            DatCompatibility.ThrowIfUnsafe(
                family,
                nameLength: null,
                name =>
                {
                    string path = Path.Combine(sourcePath, Path.GetFileName(name));
                    return File.Exists(path) ? File.ReadAllBytes(path) : null;
                },
                files);
            foreach ((string name, byte[] data) in files)
            {
                File.WriteAllBytes(Path.Combine(sourcePath, Path.GetFileName(name)), data);
            }

            written = files.Count;
        }
        else if (File.Exists(sourcePath))
        {
            written = DatPacker.ReplaceEntries(sourcePath, files);
        }
        else
        {
            throw new FileNotFoundException("That DAT or unpacked folder is gone.", sourcePath);
        }

        cache?.Evict(sourcePath);
        PaletteLocator.Invalidate(sourcePath);
        return written;
    }

    public static int Write(ArchiveCache? cache, CatalogEntry catalog, IReadOnlyList<(string Name, byte[] Data)> files) =>
        Write(cache, catalog.SourcePath, files);

    public static int Write(ArchiveCache? cache, CatalogEntry catalog, string name, byte[] data) =>
        Write(cache, catalog, [(name, data)]);
}
