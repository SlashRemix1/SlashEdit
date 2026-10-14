using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace DarkAges.Viewer.Services;

public static class ButtonMenu
{
    public static void Open(object sender)
    {
        if (sender is not System.Windows.Controls.Button button || button.ContextMenu is null)
        {
            return;
        }

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }
}
