using System.IO;
using System.Text;
using DarkAges.Formats.Maps;

namespace DarkAges.Viewer.Services;

public static class AreaCaption
{
    public static string Format(
        int mapId,
        AreaName area,
        MapNameIndex names,
        string? mapFile,
        int width,
        int height,
        ServerMap? server = null)
    {
        var text = new StringBuilder();
        string label = names.Label(mapId, Path.GetFileNameWithoutExtension(mapFile ?? ""));
        if (string.IsNullOrWhiteSpace(label))
        {
            label = string.IsNullOrWhiteSpace(area.Name) ? (mapId > 0 ? $"Map {mapId}" : "Untitled map") : area.Name;
        }

        text.Append(label);
        if (mapId > 0)
        {
            text.Append("  #").Append(mapId);
        }

        text.AppendLine();
        text.Append(width).Append(" × ").Append(height);
        if (area.Cols > 0 && area.Rows > 0)
        {
            text.Append("   area ").Append(area.Cols).Append(" × ").Append(area.Rows);
        }

        text.AppendLine();
        if (area.MapId <= 0)
        {
            text.Append("No area dump for this map.");
            return text.ToString();
        }

        text.Append("Music ").Append(area.Music);
        text.Append("   Flags ").Append(area.Flags);
        if (area.LevelRequired != 0)
        {
            text.Append("   Level ").Append(area.LevelRequired);
        }

        if (area.World != 0)
        {
            text.Append("   World ").Append(area.World);
        }

        if (area.CantWarp)
        {
            text.Append("   can't warp");
        }

        if (!string.IsNullOrWhiteSpace(area.LightType))
        {
            text.Append("   Light ").Append(area.LightType);
        }

        if (area.DynamicLighting)
        {
            text.Append("   dynamic lighting");
        }

        if (!string.IsNullOrWhiteSpace(area.ScriptKey))
        {
            text.AppendLine();
            text.Append(area.ScriptKey);
        }

        if (server is not null)
        {
            text.AppendLine();
            text.Append("Server ").Append(KindLabel(server.Kind));
            if (!string.IsNullOrWhiteSpace(server.Name) && !server.Name.Equals(label, StringComparison.OrdinalIgnoreCase))
            {
                text.Append("   ").Append(server.Name);
            }

            if (server.Music != 0)
            {
                text.Append("   music ").Append(server.Music);
            }

            if (!string.IsNullOrWhiteSpace(server.LightType))
            {
                text.Append("   light ").Append(server.LightType);
            }

            if (server.DynamicLighting)
            {
                text.Append("   dynamic lighting");
            }

            if (!string.IsNullOrWhiteSpace(server.Flags))
            {
                text.Append("   ").Append(server.Flags);
            }

            text.AppendLine();
            text.Append(server.Warps.Count).Append(" server warp").Append(server.Warps.Count == 1 ? "" : "s");
            text.Append("  (read-only)");
        }

        return text.ToString();
    }

    private static string KindLabel(ServerKind kind) => kind switch
    {
        ServerKind.Hybrasyl => "Hybrasyl",
        ServerKind.Chaos => "Chaos",
        ServerKind.Mixed => "Hybrasyl/Chaos",
        _ => "world"
    };
}
