using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DarkAges.Formats.Maps;

namespace DarkAges.Viewer.Services;

public static class WarpHoverTip
{
    public static string ToLine(MapWarp warp, MapNameIndex? names, WarpTable? warps = null, int sourceMapId = 0)
    {
        int destId = warp.DestMapId > 0 ? warp.DestMapId : WarpTable.GuessMapId(warp.Destination);
        string name = names?.Name(destId) ?? "";
        if (string.IsNullOrWhiteSpace(name)
            && !string.IsNullOrWhiteSpace(warp.Destination)
            && !AreaImport.LooksLikeWarpLabel(warp.Destination))
        {
            name = warp.Destination.Trim();
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = destId > 0 ? destId.ToString() : "unknown";
        }

        int x = warp.DestX;
        int y = warp.DestY;
        if ((x < 0 || y < 0) && warps is not null && destId > 0 && sourceMapId > 0)
        {
            foreach (MapWarp back in warps.For(destId))
            {
                if (back.DestMapId == sourceMapId || WarpTable.GuessMapId(back.Destination) == sourceMapId)
                {
                    x = back.X1;
                    y = back.Y1;
                    break;
                }
            }
        }

        string line = x >= 0 && y >= 0 ? $"To: {name} {x}, {y}" : $"To: {name}";
        return warp.Origin == WarpOrigin.Server ? $"Server {line}" : line;
    }

    public static void Show(Popup? popup, TextBlock? label, UIElement? target, System.Windows.Point local, string line)
    {
        if (popup is null || label is null || target is null)
        {
            return;
        }

        label.Text = line;
        popup.PlacementTarget = target;
        popup.Placement = PlacementMode.Relative;
        popup.HorizontalOffset = local.X + 14;
        popup.VerticalOffset = local.Y - 36;
        popup.IsOpen = true;
    }

    public static void Hide(Popup? popup)
    {
        if (popup is not null)
        {
            popup.IsOpen = false;
        }
    }
}
