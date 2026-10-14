namespace DarkAges.Viewer.Maps;

public readonly record struct FloorFamily(int Min, int Max, string Name)
{
    public bool Contains(int id) => id >= Min && id <= Max;
}

public static class FloorFamilies
{
    public static FloorFamily Grass { get; } = new(2, 102, "Grass");
    public static FloorFamily GrassPath { get; } = new(103, 246, "Grass pathing");
    public static FloorFamily WoodPlanks { get; } = new(258, 269, "Wood planks");
    public static FloorFamily Crypt { get; } = new(278, 304, "Crypt floor");
    public static FloorFamily Water { get; } = new(327, 425, "Water");
    public static FloorFamily StoneyGrass { get; } = new(426, 536, "Stoney grass");
    public static FloorFamily WoodBridge { get; } = new(537, 621, "Wood bridge");
    public static FloorFamily GrassLate { get; } = new(622, 699, "Grass");

    private static readonly FloorFamily[] All =
    [
        Grass, GrassPath, WoodPlanks, Crypt, Water, StoneyGrass, WoodBridge, GrassLate
    ];

    public static string Label(int id, IReadOnlyList<FloorFamily>? families = null)
    {
        FloorFamily? family = families is null ? Find(id) : Find(id, families);
        return family is FloorFamily fam ? $"{fam.Name} {id}" : $"Floor {id}";
    }

    public static FloorFamily? Find(int floorId)
    {
        foreach (FloorFamily family in All)
        {
            if (family.Contains(floorId))
            {
                return family;
            }
        }

        return null;
    }

    public static IReadOnlyList<FloorFamily> Seeds { get; } = All;

    public static FloorFamily? PathFor(int groundId)
    {
        FloorFamily? ground = Find(groundId);
        if (ground is null)
        {
            return null;
        }

        if (ground == Grass || ground == GrassLate || ground == StoneyGrass)
        {
            return GrassPath;
        }

        if (ground == WoodPlanks || ground == Water)
        {
            return WoodBridge;
        }

        if (ground == Crypt)
        {
            return Crypt;
        }

        return ground;
    }

    /// <summary>
    /// Known bands stay split even when their IDs touch. Every other used floor
    /// joins a family when the next used ID is within <paramref name="gap"/> of the last.
    /// </summary>
    public static List<FloorFamily> Cluster(IEnumerable<int> usedIds, int gap = 8)
    {
        var families = new List<FloorFamily>(All);
        var extra = usedIds
            .Where(id => id > 1 && Find(id) is null)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        int start = -1;
        int previous = -1;
        void Flush()
        {
            if (start > 0)
            {
                families.Add(new FloorFamily(start, previous, $"Floors {start}–{previous}"));
            }

            start = -1;
        }

        foreach (int id in extra)
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
        families.Sort((a, b) => a.Min.CompareTo(b.Min));
        return families;
    }

    public static FloorFamily? Find(int floorId, IReadOnlyList<FloorFamily> families)
    {
        foreach (FloorFamily family in families)
        {
            if (family.Contains(floorId))
            {
                return family;
            }
        }

        return null;
    }

    public static string Describe(int groundId, int pathId, IReadOnlyList<FloorFamily>? families = null)
    {
        FloorFamily? ground = families is null ? Find(groundId) : Find(groundId, families);
        FloorFamily? path = families is null ? Find(pathId) : Find(pathId, families);
        string groundText = ground is null ? $"floor {groundId}" : $"{ground.Value.Name} {ground.Value.Min}–{ground.Value.Max}";
        string pathText = path is null ? $"path {pathId}" : $"{path.Value.Name} {path.Value.Min}–{path.Value.Max}";
        return $"{groundText}. Paths use {pathText} (tile {pathId}).";
    }
}
