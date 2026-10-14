using DarkAges.Formats.Maps;

namespace DarkAges.Viewer.Maps;

public enum StampCategory
{
    Building,
    Pair,
    Tree,
    Path,
    Other
}

public static class StampCategories
{
    public static IReadOnlyList<StampCategory> All { get; } =
        [StampCategory.Building, StampCategory.Pair, StampCategory.Tree, StampCategory.Path, StampCategory.Other];

    public static string Title(StampCategory category) => category switch
    {
        StampCategory.Building => "Buildings",
        StampCategory.Pair => "Pairs",
        StampCategory.Tree => "Trees",
        StampCategory.Path => "Pathing",
        _ => "Other"
    };

    public static string Noun(StampCategory category) => category switch
    {
        StampCategory.Building => "Building",
        StampCategory.Pair => "Pair",
        StampCategory.Tree => "Tree",
        StampCategory.Path => "Path",
        _ => "Other"
    };

    public static StampCategory Parse(string? value)
    {
        if (Enum.TryParse(value, ignoreCase: true, out StampCategory category)
            && category is StampCategory.Building or StampCategory.Pair or StampCategory.Tree or StampCategory.Path or StampCategory.Other)
        {
            return category;
        }

        return StampCategory.Other;
    }

    public static StampCategory Classify(MapTile[] cells, int width, int height)
    {
        if (cells.Length == 0 || width <= 0 || height <= 0)
        {
            return StampCategory.Other;
        }

        int walls = 0;
        int floors = 0;
        int bothWalls = 0;
        for (int i = 0; i < cells.Length; i++)
        {
            MapTile tile = cells[i];
            bool wall = tile.LeftWall != 0 || tile.RightWall != 0;
            if (wall)
            {
                walls++;
            }

            if (tile.LeftWall != 0 && tile.RightWall != 0)
            {
                bothWalls++;
            }

            if (tile.Floor != 0)
            {
                floors++;
            }
        }

        int area = Math.Max(1, width * height);
        bool skinny = width >= 5 && height <= 2
            || height >= 5 && width <= 2
            || width >= 7 && height <= 3
            || height >= 7 && width <= 3;
        double wallDensity = walls / (double)area;
        bool enclosed = LooksEnclosed(cells, width, height, walls);

        if (walls == 0 && floors > 0)
        {
            return StampCategory.Path;
        }

        if (bothWalls > 0 && walls <= 5 && area <= 6 && !enclosed)
        {
            return StampCategory.Pair;
        }

        if (skinny && wallDensity < 0.28 && floors >= walls)
        {
            return StampCategory.Path;
        }

        if (skinny && wallDensity >= 0.28)
        {
            return StampCategory.Other;
        }

        if (enclosed && width >= 3 && height >= 2 && walls >= 6)
        {
            return StampCategory.Building;
        }

        if (width <= 4 && height <= 4 && walls <= 8 && bothWalls <= 2 && !enclosed)
        {
            return StampCategory.Tree;
        }

        if (walls >= 8 && width >= 3 && height >= 2)
        {
            return StampCategory.Building;
        }

        return StampCategory.Other;
    }

    private static bool LooksEnclosed(MapTile[] cells, int width, int height, int walls)
    {
        if (width < 3 || height < 2 || walls < 6)
        {
            return false;
        }

        int edgeWalls = 0;
        int edgeTiles = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool edge = x == 0 || y == 0 || x == width - 1 || y == height - 1;
                if (!edge)
                {
                    continue;
                }

                edgeTiles++;
                MapTile tile = cells[y * width + x];
                if (tile.LeftWall != 0 || tile.RightWall != 0)
                {
                    edgeWalls++;
                }
            }
        }

        return edgeTiles > 0 && edgeWalls >= edgeTiles * 0.55;
    }
}
