using System.IO;
using DarkAges.Formats.IO;
using DarkAges.Formats.Maps;

namespace DarkAges.Viewer.Services;

public static class DataDumps
{
    public static TableDump MapTiles(MapFile map, int mapId = 0, string? name = null)
    {
        string stem = string.IsNullOrWhiteSpace(name)
            ? Path.GetFileNameWithoutExtension(map.Name ?? "")
            : name;
        var rows = new List<IReadOnlyList<object?>>(map.Width * map.Height);
        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                MapTile tile = map[x, y];
                rows.Add([mapId, stem ?? "", x, y, (int)tile.Floor, (int)tile.LeftWall, (int)tile.RightWall]);
            }
        }

        return TableDump.Create(
            "MapTiles",
            ["MapId", "Name", "X", "Y", "Floor", "LeftWall", "RightWall"],
            rows);
    }

    public static TableDump Warps(IEnumerable<MapWarp> warps, MapNameIndex? names = null) =>
        TableDump.Create(
            "Warps",
            ["MapId", "MapName", "X1", "Y1", "X2", "Y2", "DestMapId", "DestName", "DestX", "DestY", "Destination", "Flag", "WorldMap"],
            warps.Select(w => (IReadOnlyList<object?>)new object?[]
            {
                w.MapId,
                names?.Label(w.MapId) ?? "",
                w.X1,
                w.Y1,
                w.X2,
                w.Y2,
                w.DestMapId,
                names?.Label(w.DestMapId, w.Destination) ?? "",
                w.DestX,
                w.DestY,
                w.Destination,
                w.Flag,
                w.IsWorldMap
            }));

    public static TableDump Areas(IEnumerable<AreaName> areas) =>
        TableDump.Create(
            "Areas",
            ["MapId", "Name", "Music", "Flags", "Rows", "Cols", "LevelRequired", "CantWarp", "ScriptKey", "World"],
            areas.Select(a => (IReadOnlyList<object?>)new object?[]
            {
                a.MapId,
                a.Name,
                a.Music,
                a.Flags,
                a.Rows,
                a.Cols,
                a.LevelRequired,
                a.CantWarp,
                a.ScriptKey,
                a.World
            }));

    public static TableDump Sotp(string label, IEnumerable<int> wallIds, Func<int, byte> flags) =>
        TableDump.Create(
            "Sotp",
            ["WallId", "Value", "TabBlock", "Walkable", "Overlay", "Tileset"],
            wallIds.Select(id =>
            {
                byte value = flags(id);
                return (IReadOnlyList<object?>)new object?[]
                {
                    id,
                    (int)value,
                    value == 15,
                    value == 0,
                    (value & 0x80) == 0x80,
                    label
                };
            }));
}
