using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using DarkAges.Formats.Data;
using DarkAges.Formats.IO;
using DarkAges.Formats.Maps;
using DarkAges.Viewer.Services;

namespace DarkAges.Viewer;

public partial class WarpCheckWindow : Window
{
    private readonly WarpTable _warps;
    private readonly AssetWorkspace? _workspace;
    private readonly MapNameIndex _names;
    private readonly IReadOnlyList<IArchive> _packed;
    private readonly Action<MapWarp>? _goSource;
    private readonly Action<MapWarp>? _goDest;
    private readonly ObservableCollection<IssueRow> _rows = [];

    public WarpCheckWindow(
        WarpTable warps,
        AssetWorkspace? workspace,
        IEnumerable<IArchive>? packedMaps,
        Action<MapWarp>? goSource,
        Action<MapWarp>? goDest,
        MapNameIndex? names = null)
    {
        InitializeComponent();
        _warps = warps;
        _workspace = workspace;
        _packed = packedMaps?.ToList() ?? [];
        _names = names ?? MapNameIndex.FromWorkspace(workspace);
        _names.AddArchives(_packed);
        _goSource = goSource;
        _goDest = goDest;
        IssueGrid.ItemsSource = _rows;
        RunCheck();
    }

    private void RunClicked(object sender, RoutedEventArgs e) => RunCheck();

    private void RunCheck()
    {
        Busy.Show(this, "Checking warps…", determinate: true);
        try
        {
            Dictionary<int, (int Width, int Height)> sizes = Busy.Pump(() =>
                MapSizeIndex.Load(_workspace?.MapFolders, _packed, Busy.Tracker(this, "Reading maps…")));
            Busy.Message(this, "Checking warps…");
            var known = _names.Ids().ToHashSet();
            foreach (int id in sizes.Keys)
            {
                known.Add(id);
            }

            IReadOnlyList<WarpIssue> issues = WarpCheck.Run(
                _warps.All(),
                sizes,
                known,
                UnusedBox?.IsChecked == true);
            _rows.Clear();
            foreach (WarpIssue issue in issues)
            {
                _rows.Add(new IssueRow(issue, _names));
            }

            StatusText.Text = _rows.Count == 0
                ? "No issues. Dest maps exist, tiles are in bounds, no overlaps, and every warp has a return."
                : $"{_rows.Count} issue(s). {WarpCheck.Summary(issues)}. Double-click or Go to map.";
        }
        finally
        {
            Busy.Hide(this);
        }
    }

    private void OpenButtonMenu(object sender, RoutedEventArgs e) => ButtonMenu.Open(sender);

    private void ExportDataClicked(object sender, RoutedEventArgs e)
    {
        TableDump dump = TableDump.Create(
            "WarpIssues",
            ["Severity", "Kind", "Map", "From", "Dest", "Landing", "Detail"],
            _rows.Select(r => (IReadOnlyList<object?>)new object?[]
            {
                r.Severity, r.Kind, r.MapLabel, r.From, r.DestLabel, r.Landing, r.Detail
            }));
        TableExport.Save(this, "warp-issues", dump, sender, StatusText);
    }

    private void GoSourceClicked(object sender, RoutedEventArgs e)
    {
        if (Selected() is IssueRow row)
        {
            _goSource?.Invoke(row.Warp);
        }
    }

    private void GoDestClicked(object sender, RoutedEventArgs e)
    {
        if (Selected() is IssueRow row && row.Issue.DestMapId > 0)
        {
            _goDest?.Invoke(row.Warp);
        }
    }

    private void IssueOpened(object sender, MouseButtonEventArgs e)
    {
        if (Selected() is IssueRow row)
        {
            _goSource?.Invoke(row.Warp);
        }
    }

    private IssueRow? Selected() => IssueGrid.SelectedItem as IssueRow;

    private sealed class IssueRow
    {
        public IssueRow(WarpIssue issue, MapNameIndex names)
        {
            Issue = issue;
            Kind = issue.Kind;
            Severity = issue.Severity;
            MapLabel = names.ChoiceLabel(issue.MapId);
            DestLabel = issue.DestMapId > 0 ? names.ChoiceLabel(issue.DestMapId) : "";
            From = $"{issue.X},{issue.Y}";
            Landing = issue.DestX < 0 ? "—" : $"{issue.DestX},{issue.DestY}";
            Detail = issue.Detail;
            Warp = new MapWarp(
                issue.MapId,
                "",
                issue.X,
                issue.Y,
                issue.X,
                issue.Y,
                "",
                issue.DestMapId,
                issue.DestX,
                issue.DestY,
                WarpOrigin.Sidecar);
        }

        public WarpIssue Issue { get; }
        public MapWarp Warp { get; }
        public string Kind { get; }
        public string Severity { get; }
        public string MapLabel { get; }
        public string DestLabel { get; }
        public string From { get; }
        public string Landing { get; }
        public string Detail { get; }
    }
}
