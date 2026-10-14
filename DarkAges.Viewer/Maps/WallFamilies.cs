using System.IO;

namespace DarkAges.Viewer.Maps;

public readonly record struct WallFamily(int Min, int Max, string Name)
{
    public bool Contains(int id) => id >= Min && id <= Max;
}

public static class WallFamilies
{
    public static IReadOnlyList<WallFamily> Cluster(IEnumerable<int> ids, int gap = 20)
    {
        var used = ids.Where(id => id > 0).Distinct().OrderBy(id => id).ToList();
        var families = new List<WallFamily>();
        int start = -1;
        int previous = -1;
        void Flush()
        {
            if (start > 0)
            {
                string name = start == previous ? $"Wall {start}" : $"Walls {start}–{previous}";
                families.Add(new WallFamily(start, previous, name));
            }

            start = -1;
        }

        foreach (int id in used)
        {
            if (start < 0)
            {
                start = previous = id;
                continue;
            }

            if (id - previous > gap)
            {
                Flush();
                start = id;
            }

            previous = id;
        }

        Flush();
        return families;
    }

    public static WallFamily? Find(int id, IReadOnlyList<WallFamily> families)
    {
        foreach (WallFamily family in families)
        {
            if (family.Contains(id))
            {
                return family;
            }
        }

        return null;
    }

    public static string Label(int id, IReadOnlyList<WallFamily>? families = null)
    {
        if (families is not null && Find(id, families) is WallFamily family)
        {
            return family.Min == family.Max ? family.Name : $"{family.Name}  {id}";
        }

        return $"Wall {id}";
    }

    public static int IdFromName(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        if (!stem.StartsWith("stc", StringComparison.OrdinalIgnoreCase)
            && !stem.StartsWith("sts", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var digits = new string(stem.Skip(3).Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out int n) ? n : 0;
    }
}
