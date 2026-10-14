using DarkAges.Formats.Maps;
using WpfComboBox = System.Windows.Controls.ComboBox;

namespace DarkAges.Viewer.Services;

public sealed class MapChoice
{
    public int Id { get; init; }
    public string Label { get; init; } = "";
}

public static class MapPicker
{
    public static void Bind(WpfComboBox box, IReadOnlyList<MapChoice> maps)
    {
        box.ItemsSource = maps;
        box.DisplayMemberPath = nameof(MapChoice.Label);
        box.IsEditable = true;
        box.IsTextSearchEnabled = true;
        box.StaysOpenOnEdit = true;
        box.MaxDropDownHeight = 360;
    }

    public static void Select(WpfComboBox box, int mapId)
    {
        if (mapId > 0 && box.ItemsSource is IEnumerable<MapChoice> maps)
        {
            MapChoice? match = maps.FirstOrDefault(c => c.Id == mapId);
            if (match is not null)
            {
                box.SelectedItem = match;
                return;
            }
        }

        box.SelectedItem = null;
        box.Text = mapId > 0 ? mapId.ToString() : "";
    }

    public static int ReadId(WpfComboBox box)
    {
        if (box.SelectedItem is MapChoice choice && choice.Id > 0)
        {
            return choice.Id;
        }

        string text = box.Text?.Trim() ?? "";
        if (int.TryParse(text, out int id) && id > 0)
        {
            return id;
        }

        return WarpTable.GuessMapId(text);
    }
}
