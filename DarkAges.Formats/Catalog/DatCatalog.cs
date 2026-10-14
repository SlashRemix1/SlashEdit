using DarkAges.Formats.Data;

namespace DarkAges.Formats.Catalog;

public static class DatCatalog
{
    private static readonly string[] SkipDirectoryNames =
    [
        "bin", "obj", ".git", ".vs", "node_modules", "_mapeditor_src", "_agent_out"
    ];

    private static readonly string[] MetaNameHints =
    [
        "iteminfo", "nationdesc", "npcillust", "worldmap", "skilldesc", "spelldesc",
        "metainf", "serverinfo", "eventdesc", "tmap"
    ];

    public static IReadOnlyList<CatalogEntry> Scan(string root) => Scan([root]);

    public static IReadOnlyList<CatalogEntry> Scan(IEnumerable<string> roots)
    {
        var results = new List<CatalogEntry>();
        foreach (string root in UniqueExisting(roots.SelectMany(ExpandRoot)))
        {
            foreach (string dat in EnumerateFilesSafe(root, "*.dat"))
            {
                try
                {
                    using var archive = DataArchive.OpenIndex(dat);
                    results.AddRange(CreateEntries(archive, dat, root, isFolder: false));
                }
                catch
                {
                    results.Add(new CatalogEntry
                    {
                        SourcePath = dat,
                        DisplayName = Path.GetFileName(dat),
                        Variant = ArchiveClassifier.InferVariant(dat, root),
                        Role = ArchiveClassifier.ClassifyByFileName(dat),
                        IsFolder = false,
                        EntryCount = 0
                    });
                }
            }

            foreach (string lrar in EnumerateFilesSafe(root, "*.lrar"))
            {
                try
                {
                    results.AddRange(LrarArchive.Catalog(lrar, root));
                }
                catch
                {
                    // skip unreadable Godot content packs
                }
            }

            AddFolderCatalog(results, root, DiscoverMapFolders([root]), null, ArchiveRole.Maps, "*.map", recursive: true);
            AddFolderCatalog(results, root, DiscoverBinkFolders([root]), "Cinematics", ArchiveRole.Cinematics, "*.bik", recursive: true);
            AddFolderCatalog(results, root, DiscoverMetaFolders([root]), "Metadata", ArchiveRole.Metadata, "*");

            foreach (string dir in EnumerateDirectoriesSafe(root))
            {
                string name = Path.GetFileName(dir);
                if (!name.Contains("unpacked", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    using var archive = new FolderArchive(dir);
                    if (archive.Entries.Count == 0)
                    {
                        continue;
                    }

                    results.AddRange(CreateEntries(archive, dir, root, isFolder: true));
                }
                catch
                {
                    // skip unreadable dumps
                }
            }
        }

        return results
            .DistinctBy(e => $"{e.SourcePath}\0{(int)e.Role}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(e => e.Role)
            .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IEnumerable<string> MetaFolders(string root) => DiscoverMetaFolders([root]);

    public static IEnumerable<string> MapFolders(string root) => DiscoverMapFolders([root]);

    public static IEnumerable<string> WarpFolders(string root) => DiscoverWarpFolders([root]);

    public static IEnumerable<string> AreaFolders(string root) => DiscoverAreaFolders([root]);

    public static IReadOnlyList<string> DiscoverWarpFolders(IEnumerable<string> roots)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in UniqueExisting(roots.SelectMany(ExpandRoot)))
        {
            foreach (string dir in EnumerateDirectoriesSafe(root).Prepend(root))
            {
                string name = Path.GetFileName(dir);
                if (name.Equals("warps", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("warp", StringComparison.OrdinalIgnoreCase))
                {
                    dirs.Add(Path.GetFullPath(dir));
                }
            }
        }

        return dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyList<string> DiscoverAreaFolders(IEnumerable<string> roots)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in UniqueExisting(roots.SelectMany(ExpandRoot)))
        {
            foreach (string dir in EnumerateDirectoriesSafe(root).Prepend(root))
            {
                string name = Path.GetFileName(dir);
                if (name.Equals("areas", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("area", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("mapnames", StringComparison.OrdinalIgnoreCase))
                {
                    dirs.Add(Path.GetFullPath(dir));
                }
            }
        }

        return dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyList<string> DiscoverMapFolders(IEnumerable<string> roots)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in UniqueExisting(roots.SelectMany(ExpandRoot)))
        {
            foreach (string file in EnumerateFilesSafe(root, "*.map"))
            {
                string? dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(dir))
                {
                    dirs.Add(Path.GetFullPath(dir));
                }
            }
        }

        return CollapseDescendants(dirs);
    }

    public static IReadOnlyList<string> DiscoverBinkFolders(IEnumerable<string> roots)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in UniqueExisting(roots.SelectMany(ExpandRoot)))
        {
            foreach (string file in EnumerateFilesSafe(root, "*.bik"))
            {
                string? dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(dir))
                {
                    dirs.Add(Path.GetFullPath(dir));
                }
            }
        }

        return CollapseDescendants(dirs);
    }

    public static IReadOnlyList<string> DiscoverMetaFolders(IEnumerable<string> roots)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in UniqueExisting(roots.SelectMany(ExpandRoot)))
        {
            foreach (string dir in EnumerateDirectoriesSafe(root).Prepend(root))
            {
                string name = Path.GetFileName(dir);
                if (name.Contains("meta", StringComparison.OrdinalIgnoreCase) && HasAnyFile(dir))
                {
                    dirs.Add(Path.GetFullPath(dir));
                    continue;
                }

                if (ContainsMetaFiles(dir))
                {
                    dirs.Add(Path.GetFullPath(dir));
                }
            }
        }

        return CollapseDescendants(dirs);
    }

    private static bool ContainsMetaFiles(string dir)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir))
            {
                string stem = Path.GetFileName(file);
                if (MetaNameHints.Any(h => stem.StartsWith(h, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool HasAnyFile(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir).Any();
        }
        catch
        {
            return false;
        }
    }

    private static List<string> CollapseDescendants(IEnumerable<string> dirs)
    {
        List<string> list = dirs.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s.Length).ToList();
        var keep = new List<string>();
        foreach (string dir in list)
        {
            if (keep.Any(parent => IsAncestor(parent, dir)))
            {
                continue;
            }

            keep.Add(dir);
        }

        return keep;
    }

    private static bool IsAncestor(string parent, string child)
    {
        if (parent.Equals(child, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string prefix = Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ExpandRoot(string root)
    {
        yield return root;
        foreach (string extra in LrarArchive.CompanionRoots(root))
        {
            yield return extra;
        }

        DirectoryInfo? parent;
        try
        {
            parent = Directory.GetParent(Path.GetFullPath(root));
        }
        catch
        {
            yield break;
        }

        if (parent is null || !parent.Exists)
        {
            yield break;
        }

        IEnumerable<DirectoryInfo> siblings = [];
        try
        {
            siblings = parent.GetDirectories();
        }
        catch
        {
            yield break;
        }

        foreach (DirectoryInfo sub in siblings)
        {
            if (ShouldSkip(sub.Name) || PathsEqual(sub.FullName, root))
            {
                continue;
            }

            string n = sub.Name.ToLowerInvariant();
            if (n.Contains("map") || n.Contains("meta") || n.Contains("dat") || n.Contains("lod"))
            {
                yield return sub.FullName;
            }
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> UniqueExisting(IEnumerable<string> roots)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            string full = Path.GetFullPath(root);
            if (seen.Add(full))
            {
                yield return full;
            }
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, string pattern)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            IEnumerable<string> files = [];
            try
            {
                files = Directory.EnumerateFiles(dir, pattern);
            }
            catch
            {
                // skip
            }

            foreach (string file in files)
            {
                yield return file;
            }

            IEnumerable<string> subs = [];
            try
            {
                subs = Directory.EnumerateDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (string sub in subs)
            {
                if (ShouldSkip(Path.GetFileName(sub)))
                {
                    continue;
                }

                stack.Push(sub);
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            IEnumerable<string> subs = [];
            try
            {
                subs = Directory.EnumerateDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (string sub in subs)
            {
                if (ShouldSkip(Path.GetFileName(sub)))
                {
                    continue;
                }

                yield return sub;
                stack.Push(sub);
            }
        }
    }

    private static bool ShouldSkip(string name) =>
        SkipDirectoryNames.Any(s => name.Equals(s, StringComparison.OrdinalIgnoreCase));

    private static void AddFolderCatalog(
        List<CatalogEntry> results,
        string root,
        IEnumerable<string> dirs,
        string? displayName,
        ArchiveRole role,
        string pattern,
        bool recursive = false)
    {
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (string dir in dirs)
        {
            int count;
            try
            {
                count = Directory.EnumerateFiles(dir, pattern, option).Count(f => FolderArchive.MatchesPattern(f, pattern));
            }
            catch
            {
                continue;
            }

            if (count == 0)
            {
                continue;
            }

            string name = string.IsNullOrWhiteSpace(displayName)
                ? Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : displayName;
            results.Add(new CatalogEntry
            {
                SourcePath = dir,
                DisplayName = name,
                Variant = InferFolderVariant(dir, root, name),
                Role = role,
                IsFolder = true,
                EntryCount = count
            });
        }
    }

    private static string? InferFolderVariant(string dir, string root, string displayName)
    {
        string? variant = ArchiveClassifier.InferVariant(dir, root);
        if (!string.IsNullOrWhiteSpace(variant) && !variant.Equals(displayName, StringComparison.OrdinalIgnoreCase))
        {
            return variant;
        }

        try
        {
            string parentName = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(dir)) ?? dir).Name;
            return parentName.Equals(displayName, StringComparison.OrdinalIgnoreCase) ? null : parentName;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<CatalogEntry> CreateEntries(IArchive archive, string path, string root, bool isFolder)
    {
        IReadOnlyList<ArchiveRole> roles = ArchiveClassifier.PresentRoles(archive);
        string? folderVariant = ArchiveClassifier.InferVariant(path, root);
        bool split = roles.Count > 1;
        foreach (ArchiveRole role in roles)
        {
            int count = split
                ? archive.Entries.Count(e => ArchiveClassifier.MatchesRole(e.Name, role))
                : archive.Entries.Count;
            yield return new CatalogEntry
            {
                SourcePath = path,
                DisplayName = archive.DisplayName,
                Variant = split
                    ? CombinedVariant(folderVariant, role)
                    : folderVariant,
                Role = role,
                IsFolder = isFolder,
                EntryCount = count
            };
        }
    }

    private static string CombinedVariant(string? folderVariant, ArchiveRole role)
    {
        string label = ArchiveRoleNames.Label(role);
        return string.IsNullOrWhiteSpace(folderVariant) ? label : $"{folderVariant} / {label}";
    }

}
