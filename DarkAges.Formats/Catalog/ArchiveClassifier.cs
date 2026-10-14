using DarkAges.Formats.Data;

namespace DarkAges.Formats.Catalog;

public static class ArchiveClassifier
{
    public static ArchiveRole Classify(IArchive archive)
    {
        var names = archive.Entries.Select(e => e.Name).ToList();
        if (names.Count == 0)
        {
            return ClassifyByFileName(archive.DisplayName);
        }

        int mpf = Count(names, n => n.EndsWith(".mpf", StringComparison.OrdinalIgnoreCase));
        int mns = Count(names, n => n.StartsWith("mns", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".mpf", StringComparison.OrdinalIgnoreCase));
        int hpf = Count(names, n => n.EndsWith(".hpf", StringComparison.OrdinalIgnoreCase));
        int spf = Count(names, n => n.EndsWith(".spf", StringComparison.OrdinalIgnoreCase));
        int stc = Count(names, n => n.StartsWith("stc", StringComparison.OrdinalIgnoreCase) || n.StartsWith("sts", StringComparison.OrdinalIgnoreCase));
        int item = Count(names, n => n.StartsWith("item", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".epf", StringComparison.OrdinalIgnoreCase));
        int efct = Count(names, n => n.StartsWith("efct", StringComparison.OrdinalIgnoreCase));
        int gui = Count(names, n => n.StartsWith("gui", StringComparison.OrdinalIgnoreCase) || n.StartsWith("skill", StringComparison.OrdinalIgnoreCase) || n.StartsWith("spell", StringComparison.OrdinalIgnoreCase));
        int btm = Count(names, n => n.StartsWith("btm", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".pcx", StringComparison.OrdinalIgnoreCase));
        int khan = Count(names, n => StartsWithAny(n, "mb", "wb", "mh", "wh", "mw", "ww"));
        int gnd = Count(names, n => n.StartsWith("gnd", StringComparison.OrdinalIgnoreCase) || n.StartsWith("mpt", StringComparison.OrdinalIgnoreCase) || n.StartsWith("mps", StringComparison.OrdinalIgnoreCase));
        int maps = Count(names, n => n.EndsWith(".map", StringComparison.OrdinalIgnoreCase));
        int bik = Count(names, n => n.EndsWith(".bik", StringComparison.OrdinalIgnoreCase));

        if (maps > 0 && maps >= names.Count / 2)
        {
            return ArchiveRole.Maps;
        }

        if (bik > 0 && bik >= names.Count / 2)
        {
            return ArchiveRole.Cinematics;
        }

        if (mns > 0 || (mpf > 5 && mpf >= hpf))
        {
            return ArchiveRole.Mobs;
        }

        if (item > 0)
        {
            return ArchiveRole.Items;
        }

        if (efct > 0)
        {
            return ArchiveRole.Effects;
        }

        if (gui > 3)
        {
            return ArchiveRole.Ui;
        }

        if (stc > 0)
        {
            return ArchiveRole.Walls;
        }

        if (btm > 0 || gnd > 0)
        {
            return ArchiveRole.Floors;
        }

        if (khan > 5 || KhanPieces.IsKhanArchive(archive))
        {
            return ArchiveRole.PlayerSprites;
        }

        if (stc == 0 && hpf <= 10 && spf > 10)
        {
            return ArchiveRole.Ui;
        }

        if (hpf > 10 || spf > 10)
        {
            string file = archive.DisplayName;
            if (file.Contains("seo", StringComparison.OrdinalIgnoreCase))
            {
                return ArchiveRole.Floors;
            }

            return ArchiveRole.Walls;
        }

        return ClassifyByFileName(archive.DisplayName);
    }

    public static bool IsCombined(IArchive archive) => PresentRoles(archive).Count > 1;

    public static IReadOnlyList<ArchiveRole> PresentRoles(IArchive archive)
    {
        var names = archive.Entries.Select(e => e.Name).ToList();
        if (names.Count == 0)
        {
            return [ClassifyByFileName(archive.DisplayName)];
        }

        var roles = new List<ArchiveRole>();
        AddIf(roles, ArchiveRole.Floors, names, n => MatchesRole(n, ArchiveRole.Floors) && !IsSidecar(n));
        AddIf(roles, ArchiveRole.Walls, names, n => MatchesRole(n, ArchiveRole.Walls) && !IsSidecar(n));
        AddIf(roles, ArchiveRole.Mobs, names, n => MatchesRole(n, ArchiveRole.Mobs) && !IsSidecar(n));
        AddIf(roles, ArchiveRole.Items, names, n => MatchesRole(n, ArchiveRole.Items) && !IsSidecar(n));
        AddIf(roles, ArchiveRole.Effects, names, n => MatchesRole(n, ArchiveRole.Effects) && !IsSidecar(n));
        AddIf(roles, ArchiveRole.PlayerSprites, names, n => MatchesRole(n, ArchiveRole.PlayerSprites) && !IsSidecar(n));
        AddIf(roles, ArchiveRole.Ui, names, n => MatchesRole(n, ArchiveRole.Ui) && !IsSidecar(n));
        AddIf(roles, ArchiveRole.Maps, names, n => MatchesRole(n, ArchiveRole.Maps));
        AddIf(roles, ArchiveRole.Metadata, names, n => MatchesRole(n, ArchiveRole.Metadata));
        AddIf(roles, ArchiveRole.Cinematics, names, n => MatchesRole(n, ArchiveRole.Cinematics));
        if (roles.Count > 1)
        {
            AddIf(roles, ArchiveRole.Misc, names, n => MatchesRole(n, ArchiveRole.Misc));
            return roles;
        }

        return [Classify(archive)];
    }

    public static bool MatchesRole(string name, ArchiveRole role)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string file = Path.GetFileName(name);
        string ext = Path.GetExtension(file);
        return role switch
        {
            ArchiveRole.Floors => FloorTiles.IsFloorName(file)
                || StartsWithAny(file, "gnd", "mpt", "mps", "btm")
                || (IsPalette(ext) && StartsWithAny(file, "field", "backpal", "legend")),
            ArchiveRole.Walls => ext.Equals(".hpf", StringComparison.OrdinalIgnoreCase)
                || file.StartsWith("stc", StringComparison.OrdinalIgnoreCase)
                || file.StartsWith("sts", StringComparison.OrdinalIgnoreCase)
                || file.Equals("sotp.dat", StringComparison.OrdinalIgnoreCase),
            ArchiveRole.Mobs => ext.Equals(".mpf", StringComparison.OrdinalIgnoreCase)
                || (file.StartsWith("mns", StringComparison.OrdinalIgnoreCase) && ext.Equals(".pal", StringComparison.OrdinalIgnoreCase))
                || file.StartsWith("mobtile", StringComparison.OrdinalIgnoreCase),
            ArchiveRole.Items => file.StartsWith("item", StringComparison.OrdinalIgnoreCase),
            ArchiveRole.Effects => file.StartsWith("efct", StringComparison.OrdinalIgnoreCase)
                || file.StartsWith("effect", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".efa", StringComparison.OrdinalIgnoreCase),
            ArchiveRole.PlayerSprites => KhanPieces.TryParse(file, out _)
                || StartsWithAny(file, "mb", "wb", "mh", "wh", "mw", "ww"),
            ArchiveRole.Ui => (ext.Equals(".epf", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".pcx", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".spf", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".hea", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".jpf", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".fnt", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".lft", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".upf", StringComparison.OrdinalIgnoreCase)
                    || StartsWithAny(file, "gui", "skill", "spell", "emo", "staff", "story"))
                && !MatchesRole(file, ArchiveRole.Items)
                && !MatchesRole(file, ArchiveRole.Effects)
                && !MatchesRole(file, ArchiveRole.PlayerSprites)
                && !MatchesRole(file, ArchiveRole.Floors),
            ArchiveRole.Maps => ext.Equals(".map", StringComparison.OrdinalIgnoreCase),
            ArchiveRole.Metadata => IsMetaName(file),
            ArchiveRole.Cinematics => ext.Equals(".bik", StringComparison.OrdinalIgnoreCase),
            ArchiveRole.Misc => !MatchesPrimary(file),
            _ => false
        };
    }

    private static bool MatchesPrimary(string name) =>
        MatchesRole(name, ArchiveRole.Floors)
        || MatchesRole(name, ArchiveRole.Walls)
        || MatchesRole(name, ArchiveRole.Mobs)
        || MatchesRole(name, ArchiveRole.Items)
        || MatchesRole(name, ArchiveRole.Effects)
        || MatchesRole(name, ArchiveRole.PlayerSprites)
        || MatchesRole(name, ArchiveRole.Ui)
        || MatchesRole(name, ArchiveRole.Maps)
        || MatchesRole(name, ArchiveRole.Metadata)
        || MatchesRole(name, ArchiveRole.Cinematics);

    private static bool IsMetaName(string name) => MetaFile.IsKnownName(name);

    public static ArchiveRole ClassifyByFileName(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        if (stem.StartsWith("khan", StringComparison.OrdinalIgnoreCase))
        {
            return ArchiveRole.PlayerSprites;
        }

        return stem switch
        {
            "hades" => ArchiveRole.Mobs,
            "ia" => ArchiveRole.Walls,
            "seo" => ArchiveRole.Floors,
            "setoa" or "cious" or "national" => ArchiveRole.Ui,
            "legend" => ArchiveRole.Items,
            "roh" => ArchiveRole.Effects,
            "maps" => ArchiveRole.Maps,
            "metadata" or "metafile" => ArchiveRole.Metadata,
            "misc" or "npcbase" => ArchiveRole.Misc,
            _ => ArchiveRole.Unknown
        };
    }

    public static string? InferVariant(string archivePath, string scanRoot)
    {
        try
        {
            string full = Path.GetFullPath(archivePath);
            string root = Path.GetFullPath(scanRoot);
            string? dir = Path.GetDirectoryName(full);
            if (dir is null)
            {
                return null;
            }

            string folder = new DirectoryInfo(dir).Name;
            string stem = Path.GetFileNameWithoutExtension(full);
            if (folder.Equals(stem, StringComparison.OrdinalIgnoreCase))
            {
                string? parent = Directory.GetParent(dir)?.FullName;
                if (parent is not null && !PathsEqual(parent, root))
                {
                    folder = new DirectoryInfo(parent).Name;
                    if (PathsEqual(parent, root))
                    {
                        return null;
                    }

                    return folder;
                }

                return null;
            }

            if (PathsEqual(dir, root))
            {
                return null;
            }

            return folder;
        }
        catch
        {
            return null;
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsPalette(string ext) =>
        ext.Equals(".pal", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".tbl", StringComparison.OrdinalIgnoreCase);

    private static bool IsSidecar(string name) => IsPalette(Path.GetExtension(name));

    private static void AddIf(List<ArchiveRole> roles, ArchiveRole role, List<string> names, Func<string, bool> pred)
    {
        if (names.Any(pred))
        {
            roles.Add(role);
        }
    }

    private static int Count(List<string> names, Func<string, bool> pred) => names.Count(pred);

    private static bool StartsWithAny(string name, params string[] prefixes)
    {
        foreach (string p in prefixes)
        {
            if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
