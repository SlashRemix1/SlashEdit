using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DarkAges.Formats.Data;
using DarkAges.Formats.IO;
using DarkAges.Formats.Maps;
using DarkAges.Viewer.Services;
using WinForms = System.Windows.Forms;

namespace DarkAges.Viewer;

public partial class WarpEditorWindow : Window
{
    private readonly WarpTable _warps;
    private readonly AssetWorkspace? _workspace;
    private readonly AreaTable _areas;
    private readonly MapNameIndex _mapNames;
    private readonly Action<MapWarp>? _goSource;
    private readonly Action<MapWarp>? _goDest;
    private readonly Action? _changed;
    private readonly IReadOnlyList<IArchive> _packed;
    private readonly ObservableCollection<WarpRow> _rows = [];
    private Dictionary<int, (int Width, int Height)>? _sizes;
    private IReadOnlyList<WarpIssue> _issues = [];
    private bool _syncing;

    public WarpEditorWindow(
        WarpTable warps,
        AssetWorkspace? workspace,
        Action<MapWarp>? goSource = null,
        Action<MapWarp>? goDest = null,
        Action? changed = null,
        IEnumerable<IArchive>? packedMaps = null)
    {
        InitializeComponent();
        _warps = warps;
        _workspace = workspace;
        _areas = AreaTable.Load(_workspace?.AreaFolders, AreaStore.FileFor(_workspace?.ActiveClient));
        _mapNames = MapNameIndex.FromWorkspace(workspace);
        _packed = packedMaps?.ToList() ?? [];
        if (_packed.Count > 0)
        {
            _mapNames.AddArchives(_packed);
        }

        _mapNames.AddFriendly(_areas.All());
        MapPicker.Bind(MapIdBox, _mapNames.Choices());
        MapPicker.Bind(DestMapBox, _mapNames.Choices());

        _goSource = goSource;
        _goDest = goDest;
        _changed = changed;
        WarpGrid.ItemsSource = _rows;
        WarpGrid.CellEditEnding += GridEditEnding;
        ICollectionView view = CollectionViewSource.GetDefaultView(_rows);
        view.Filter = FilterRow;
        ReloadRows();
    }

    public void ReloadRows()
    {
        WarpRow? selected = WarpGrid.SelectedItem as WarpRow;
        _rows.Clear();
        foreach (MapWarp warp in _warps.All().OrderBy(w => w.MapId).ThenBy(w => w.Y1).ThenBy(w => w.X1))
        {
            _rows.Add(Named(warp));
        }

        RefreshValidation();
        CollectionViewSource.GetDefaultView(_rows).Refresh();
        if (selected is not null)
        {
            WarpRow? match = _rows.FirstOrDefault(r => r.Matches(selected.ToWarp()));
            if (match is not null)
            {
                WarpGrid.SelectedItem = match;
                WarpGrid.ScrollIntoView(match);
            }
        }

    }

    private void FilterChanged(object sender, TextChangedEventArgs e) =>
        CollectionViewSource.GetDefaultView(_rows).Refresh();

    private void FilterToggled(object sender, RoutedEventArgs e) =>
        CollectionViewSource.GetDefaultView(_rows).Refresh();

    private bool FilterRow(object obj)
    {
        if (obj is not WarpRow row)
        {
            return false;
        }

        if (IssuesOnlyBox?.IsChecked == true && string.IsNullOrWhiteSpace(row.Problems))
        {
            return false;
        }

        string filter = FilterBox?.Text?.Trim() ?? "";
        if (filter.Length == 0)
        {
            return true;
        }

        return row.MapId.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.DestMapId.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.MapName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.DestMapName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.Destination.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.Origin.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.Problems.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void GridEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not WarpRow row)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            Commit(row, row.ToWarp() with { Origin = WarpOrigin.Sidecar });
            FillDetail(row);
        });
    }

    private void WarpSelected(object sender, SelectionChangedEventArgs e) => FillDetail(SelectedRow());

    private void WarpDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        if (SelectedRow() is WarpRow row)
        {
            _goSource?.Invoke(row.ToWarp());
        }
    }

    private void GridKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            DeleteClicked(sender, e);
            e.Handled = true;
        }
    }

    private WarpRow? SelectedRow() => WarpGrid.SelectedItem as WarpRow;

    private void FillDetail(WarpRow? row)
    {
        _syncing = true;
        MapPicker.Select(MapIdBox, row?.MapId ?? 0);
        MapNameBox.Text = row?.MapName ?? "";
        X1Box.Text = row?.X1.ToString() ?? "";
        Y1Box.Text = row?.Y1.ToString() ?? "";
        X2Box.Text = row?.X2.ToString() ?? "";
        Y2Box.Text = row?.Y2.ToString() ?? "";
        MapPicker.Select(DestMapBox, row is null ? 0 : row.DestMapId);
        DestMapNameBox.Text = row?.DestMapName ?? "";
        DestXBox.Text = row is null || row.DestX < 0 ? "" : row.DestX.ToString();
        DestYBox.Text = row is null || row.DestY < 0 ? "" : row.DestY.ToString();
        NameBox.Text = row?.Destination ?? "";
        OriginBox.Text = row?.Origin ?? "";
        if (IssueText is not null)
        {
            IssueText.Text = string.IsNullOrWhiteSpace(row?.IssueDetail) ? "OK" : row.IssueDetail;
            IssueText.Foreground = string.IsNullOrWhiteSpace(row?.Problems)
                ? System.Windows.Media.Brushes.LightGreen
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(232, 184, 109));
        }

        _syncing = false;
    }

    private void MapPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || SelectedRow() is not WarpRow row || !TryReadDetail(out MapWarp warp))
        {
            return;
        }

        Commit(row, warp);
        FillDetail(row);
    }

    private void DetailLostFocus(object sender, RoutedEventArgs e)
    {
        if (_syncing || SelectedRow() is not WarpRow row || !TryReadDetail(out MapWarp warp))
        {
            return;
        }

        Commit(row, warp);
        FillDetail(row);
    }

    private bool TryReadDetail(out MapWarp warp)
    {
        warp = default;
        int mapId = MapPicker.ReadId(MapIdBox);
        if (mapId <= 0)
        {
            return false;
        }

        int x1 = ParseOr(X1Box.Text, 0);
        int y1 = ParseOr(Y1Box.Text, 0);
        int x2 = ParseOr(X2Box.Text, x1);
        int y2 = ParseOr(Y2Box.Text, y1);
        int destMap = MapPicker.ReadId(DestMapBox);
        if (destMap <= 0)
        {
            destMap = WarpTable.GuessMapId(NameBox.Text);
        }
        int destX = int.TryParse(DestXBox.Text.Trim(), out int dx) ? dx : -1;
        int destY = int.TryParse(DestYBox.Text.Trim(), out int dy) ? dy : -1;
        warp = new MapWarp(mapId, NameBox.Text.Trim(), x1, y1, x2, y2, "", destMap, destX, destY, WarpOrigin.Sidecar);
        return true;
    }

    private static int ParseOr(string text, int fallback) =>
        int.TryParse(text.Trim(), out int n) ? n : fallback;

    private void NewClicked(object sender, RoutedEventArgs e)
    {
        var warp = new MapWarp(1, "", 0, 0, 0, 0, "", 0, -1, -1, WarpOrigin.Sidecar);
        _warps.Add(warp);
        var row = Named(warp);
        _rows.Add(row);
        WarpGrid.SelectedItem = row;
        WarpGrid.ScrollIntoView(row);
        MapIdBox.Focus();
        RefreshValidation();
        Notify();
        StatusText.Text = "New warp. Set source map and destination, then Save.";
    }

    private void DeleteClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedRow() is not WarpRow row)
        {
            return;
        }

        _warps.Remove(row.Original);
        _rows.Remove(row);
        Persist();
        RefreshValidation();
        Notify();
        StatusText.Text = "Removed warp.";
    }

    private void ImportFileClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Warp dumps|*.json;*.csv;*.txt;*.tsv;*.xml;*.sql|JSON|*.json|CSV / text|*.csv;*.txt;*.tsv|XML|*.xml|SQL|*.sql|All files|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            ImportPath(dialog.FileName);
        }
    }

    private void ImportFolderClicked(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Folder of warp dumps for this client"
        };
        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            ImportPath(dialog.SelectedPath);
        }
    }

    private void ImportAreasClicked(object sender, RoutedEventArgs e)
    {
        var file = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import area names",
            Filter = "Area dumps|*.json;*.csv;*.txt;*.tsv;*.xml;*.sql|JSON|*.json|CSV / text|*.csv;*.txt;*.tsv|XML|*.xml|SQL|*.sql|All files|*.*"
        };
        if (file.ShowDialog(this) == true)
        {
            ImportAreasPath(file.FileName);
            return;
        }

        using var folder = new WinForms.FolderBrowserDialog
        {
            Description = "Folder of area dumps (one JSON per map, or a combined CSV/XML/JSON)"
        };
        if (folder.ShowDialog() == WinForms.DialogResult.OK)
        {
            ImportAreasPath(folder.SelectedPath);
        }
    }

    private void ImportAreasPath(string path)
    {
        try
        {
            AreaImportResult result = AreaImport.FromPath(path);
            if (result.Areas.Count == 0)
            {
                StatusText.Text = "No map names in that dump. Need an id plus a Name (Zolian Areas JSON, CSV, Hybrasyl XML, SQL).";
                return;
            }

            _areas.AddRange(result.Areas);
            _mapNames.AddFriendly(_areas.All());
            MapPicker.Bind(MapIdBox, _mapNames.Choices());
            MapPicker.Bind(DestMapBox, _mapNames.Choices());
            if (_workspace?.ActiveClient is not null)
            {
                AreaStore.Save(_workspace.ActiveClient, _areas);
            }

            ReloadRows();
            ClientChangeLog.Append(_workspace?.ActiveClient, "area", $"Imported {result.Areas.Count} map names from {Path.GetFileName(path)}.");
            StatusText.Text = $"Imported {result.Areas.Count} map names ({result.Format}) from {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Import areas");
        }
    }

    private void ImportPath(string path)
    {
        try
        {
            WarpImportResult result = WarpImport.FromPath(path);
            if (result.Warps.Count == 0)
            {
                StatusText.Text = "No warps in that dump. Tried JSON, CSV headers, XML, SQL, and TMap.";
                return;
            }

            _warps.AddRange(result.Warps, replace: true);
            Persist();
            ReloadRows();
            Notify();
            StatusText.Text = $"Imported {result.Warps.Count} warps ({result.Format}) from {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Import warps");
        }
    }

    private void OpenButtonMenu(object sender, RoutedEventArgs e) => ButtonMenu.Open(sender);

    private void ExportClicked(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<MapWarp> all = _warps.All().ToList();
        TableDump dump = DataDumps.Warps(all, _mapNames);
        TableExport.Save(this, $"{_workspace?.ActiveClient?.Name ?? "warps"}-warps", dump, sender, StatusText);
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (SelectedRow() is WarpRow row && TryReadDetail(out MapWarp warp))
            {
                Commit(row, warp, persist: false);
            }

            Persist();
            StatusText.Text = $"Saved warps for {_workspace?.ActiveClient?.Name ?? "this client"}.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Save warps");
        }
    }

    private void GoSourceClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedRow() is WarpRow row)
        {
            _goSource?.Invoke(row.ToWarp());
        }
    }

    private void GoDestClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedRow() is WarpRow row)
        {
            _goDest?.Invoke(row.ToWarp());
        }
    }

    private void Commit(WarpRow row, MapWarp next, bool persist = true)
    {
        MapWarp prev = row.Original;
        if (prev.MapId != next.MapId || !WarpTable.SameRect(prev, next))
        {
            _warps.Remove(prev);
        }

        _warps.Add(next);
        row.Apply(next, _mapNames);
        if (persist)
        {
            Persist();
        }

        RefreshValidation();
        Notify();
    }

    private void Persist()
    {
        if (_workspace?.ActiveClient is null)
        {
            return;
        }

        WarpStore.Save(_workspace.ActiveClient, _warps);
        ClientSnapshots.SaveWarps(_workspace.ActiveClient, _warps);
        ClientChangeLog.Append(_workspace.ActiveClient, "warp", $"Saved {_warps.WarpCount} warps.");
    }

    private void CheckClicked(object sender, RoutedEventArgs e)
    {
        var window = new WarpCheckWindow(_warps, _workspace, _packed, _goSource, _goDest, _mapNames) { Owner = this };
        window.Show();
    }

    private void RefreshValidation()
    {
        _issues = WarpCheck.Run(_warps.All(), MapSizes(), KnownMaps());
        foreach (WarpRow row in _rows)
        {
            List<WarpIssue> mine = _issues.Where(i => i.Matches(row.ToWarp())).ToList();
            row.Problems = WarpCheck.Line(mine);
            row.IssueDetail = string.Join(Environment.NewLine, mine.Select(i => $"{i.Severity}: {i.Kind} — {i.Detail}"));
        }

        string check = WarpCheck.Summary(_issues);
        StatusText.Text = string.IsNullOrWhiteSpace(check)
            ? $"{_rows.Count} warps. Validation OK."
            : $"{_rows.Count} warps. {check}.";
    }

    private Dictionary<int, (int Width, int Height)> MapSizes()
    {
        if (_sizes is not null)
        {
            return _sizes;
        }

        _sizes = Busy.Compute(this, "Reading map sizes…", progress =>
            MapSizeIndex.Load(_workspace?.MapFolders, _packed, progress));
        return _sizes;
    }

    private IReadOnlyCollection<int> KnownMaps()
    {
        var ids = _mapNames.Ids().ToHashSet();
        foreach (int id in MapSizes().Keys)
        {
            ids.Add(id);
        }

        return ids;
    }

    private void Notify() => _changed?.Invoke();

    private WarpRow Named(MapWarp warp) => WarpRow.From(warp, _mapNames);

    private sealed class WarpRow : INotifyPropertyChanged
    {
        public MapWarp Original { get; private set; } = default!;

        private int _mapId;
        private int _x1;
        private int _y1;
        private int _x2;
        private int _y2;
        private int _destMapId;
        private int _destX;
        private int _destY;
        private string _mapName = "";
        private string _destMapName = "";
        private string _destination = "";
        private string _origin = "";
        private string _problems = "";
        private string _issueDetail = "";

        public int MapId { get => _mapId; set => Set(ref _mapId, value); }
        public string MapName { get => _mapName; set => Set(ref _mapName, value ?? ""); }
        public int X1 { get => _x1; set => Set(ref _x1, value); }
        public int Y1 { get => _y1; set => Set(ref _y1, value); }
        public int X2 { get => _x2; set => Set(ref _x2, value); }
        public int Y2 { get => _y2; set => Set(ref _y2, value); }
        public int DestMapId { get => _destMapId; set => Set(ref _destMapId, value); }
        public string DestMapName { get => _destMapName; set => Set(ref _destMapName, value ?? ""); }
        public int DestX { get => _destX; set => Set(ref _destX, value); }
        public int DestY { get => _destY; set => Set(ref _destY, value); }
        public string Destination { get => _destination; set => Set(ref _destination, value ?? ""); }
        public string Origin { get => _origin; set => Set(ref _origin, value ?? ""); }
        public string Problems { get => _problems; set => Set(ref _problems, value ?? ""); }
        public string IssueDetail { get => _issueDetail; set => Set(ref _issueDetail, value ?? ""); }

        public event PropertyChangedEventHandler? PropertyChanged;

        public static WarpRow From(MapWarp warp, MapNameIndex names)
        {
            var row = new WarpRow { Original = warp };
            row.Apply(warp, names);
            return row;
        }

        public void Apply(MapWarp warp, MapNameIndex names)
        {
            Original = warp;
            MapId = warp.MapId;
            MapName = names.Label(warp.MapId);
            X1 = warp.X1;
            Y1 = warp.Y1;
            X2 = warp.X2;
            Y2 = warp.Y2;
            DestMapId = warp.DestMapId;
            DestMapName = names.Label(warp.DestMapId, warp.Destination);
            DestX = warp.DestX;
            DestY = warp.DestY;
            Destination = warp.Destination ?? "";
            Origin = warp.Origin.ToString();
        }

        public MapWarp ToWarp() =>
            new(MapId, Destination ?? "", X1, Y1, X2, Y2, Original.Flag, DestMapId, DestX, DestY,
                Enum.TryParse(Origin, out WarpOrigin origin) ? origin : WarpOrigin.Sidecar);

        public bool Matches(MapWarp warp) =>
            MapId == warp.MapId && X1 == warp.X1 && Y1 == warp.Y1 && X2 == warp.X2 && Y2 == warp.Y2;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
