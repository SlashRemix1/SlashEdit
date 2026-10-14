using System.Windows;
using DarkAges.Formats.Maps;
using DarkAges.Viewer.Services;

namespace DarkAges.Viewer;

public partial class WarpEditWindow : Window
{
    public enum Choice
    {
        Cancel,
        Save,
        Delete,
        Go
    }

    private readonly Func<MapWarp, IReadOnlyList<WarpIssue>>? _validate;

    public WarpEditWindow(
        MapWarp warp,
        bool isNew,
        IReadOnlyList<MapChoice>? maps = null,
        Func<MapWarp, IReadOnlyList<WarpIssue>>? validate = null)
    {
        InitializeComponent();
        Warp = warp;
        Result = Choice.Cancel;
        _validate = validate;
        Title = isNew ? "New warp" : "Edit warp";
        IReadOnlyList<MapChoice> choices = maps ?? [];
        MapPicker.Bind(MapIdBox, choices);
        MapPicker.Bind(DestMapBox, choices);
        MapPicker.Select(MapIdBox, warp.MapId);
        RectBox.Text = warp.X1 == warp.X2 && warp.Y1 == warp.Y2
            ? $"{warp.X1}, {warp.Y1}"
            : $"{warp.X1},{warp.Y1} → {warp.X2},{warp.Y2}";
        MapPicker.Select(DestMapBox, warp.DestMapId);
        DestXBox.Text = warp.DestX >= 0 ? warp.DestX.ToString() : "";
        DestYBox.Text = warp.DestY >= 0 ? warp.DestY.ToString() : "";
        NameBox.Text = warp.Destination;
        MapIdBox.SelectionChanged += (_, _) => ShowIssues();
        DestMapBox.SelectionChanged += (_, _) => ShowIssues();
        DestXBox.LostFocus += (_, _) => ShowIssues();
        DestYBox.LostFocus += (_, _) => ShowIssues();
        ShowIssues();
    }

    public MapWarp Warp { get; private set; }
    public Choice Result { get; private set; }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (!TryRead(out MapWarp warp) || !ConfirmIssues(warp))
        {
            return;
        }

        Warp = warp;
        Result = Choice.Save;
        DialogResult = true;
    }

    private void GoClicked(object sender, RoutedEventArgs e)
    {
        if (!TryRead(out MapWarp warp))
        {
            return;
        }

        Warp = warp;
        Result = Choice.Go;
        DialogResult = true;
    }

    private void DeleteClicked(object sender, RoutedEventArgs e)
    {
        Result = Choice.Delete;
        DialogResult = true;
    }

    private void CancelClicked(object sender, RoutedEventArgs e)
    {
        Result = Choice.Cancel;
        DialogResult = false;
    }

    private void ShowIssues()
    {
        if (IssueText is null || _validate is null || !TryRead(out MapWarp warp))
        {
            return;
        }

        IReadOnlyList<WarpIssue> issues = _validate(warp);
        if (issues.Count == 0)
        {
            IssueText.Text = "Validation OK.";
            IssueText.Foreground = System.Windows.Media.Brushes.LightGreen;
            return;
        }

        IssueText.Text = string.Join(Environment.NewLine, issues.Select(i => $"{i.Severity}: {i.Kind} — {i.Detail}"));
        IssueText.Foreground = issues.Any(i => i.IsError)
            ? System.Windows.Media.Brushes.IndianRed
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(232, 184, 109));
    }

    private bool ConfirmIssues(MapWarp warp)
    {
        if (_validate is null)
        {
            return true;
        }

        List<WarpIssue> errors = _validate(warp).Where(i => i.IsError).ToList();
        if (errors.Count == 0)
        {
            return true;
        }

        string text = string.Join("\n", errors.Select(i => $"• {i.Kind}: {i.Detail}"));
        MessageBoxResult answer = System.Windows.MessageBox.Show(
            this,
            $"This warp has {errors.Count} validation error(s):\n\n{text}\n\nSave anyway?",
            "Warp check",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return answer == MessageBoxResult.Yes;
    }

    private bool TryRead(out MapWarp warp)
    {
        warp = Warp;
        int mapId = MapPicker.ReadId(MapIdBox);
        if (mapId <= 0)
        {
            mapId = Warp.MapId;
        }

        int destMap = MapPicker.ReadId(DestMapBox);
        if (destMap <= 0)
        {
            destMap = WarpTable.GuessMapId(NameBox.Text);
        }

        int destX = int.TryParse(DestXBox.Text.Trim(), out int dx) ? dx : -1;
        int destY = int.TryParse(DestYBox.Text.Trim(), out int dy) ? dy : -1;
        warp = Warp with
        {
            MapId = mapId,
            Destination = NameBox.Text.Trim(),
            DestMapId = destMap,
            DestX = destX,
            DestY = destY,
            Origin = WarpOrigin.Sidecar
        };
        return true;
    }
}
