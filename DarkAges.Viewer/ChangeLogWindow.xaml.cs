using System.Diagnostics;
using System.IO;
using System.Windows;
using DarkAges.Formats.IO;
using DarkAges.Viewer.Services;

namespace DarkAges.Viewer;

public partial class ChangeLogWindow : Window
{
    private readonly ClientProfile? _client;

    public ChangeLogWindow(ClientProfile? client)
    {
        InitializeComponent();
        _client = client;
        Title = client is null ? "Change log" : $"Change log — {client.Name}";
        Reload();
    }

    private void RefreshClicked(object sender, RoutedEventArgs e) => Reload();

    private void OpenButtonMenu(object sender, RoutedEventArgs e) => ButtonMenu.Open(sender);

    private void ExportDataClicked(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<ChangeLogEntry> entries = ClientChangeLog.Load(_client);
        TableDump dump = TableDump.Create(
            "ChangeLog",
            ["When", "Kind", "Detail"],
            entries.Select(e => (IReadOnlyList<object?>)new object?[]
            {
                e.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), e.Kind, e.Detail
            }));
        TableExport.Save(this, "changelog", dump, sender, StatusText);
    }

    private void FolderClicked(object sender, RoutedEventArgs e)
    {
        string? folder = ClientChangeLog.FolderPath(_client) ?? ClientSnapshots.Folder(_client);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            System.Windows.MessageBox.Show(this, "Load a client first.", "Change log");
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true
        });
    }

    private void Reload()
    {
        IReadOnlyList<ChangeLogEntry> entries = ClientChangeLog.Load(_client);
        LogGrid.ItemsSource = entries.Select(e => new
        {
            When = e.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            e.Kind,
            e.Detail
        }).ToList();
        int snaps = ClientSnapshots.List(_client).Count;
        StatusText.Text = entries.Count == 0
            ? snaps == 0
                ? "No edits logged for this client yet."
                : $"{snaps} snapshot file(s). No changelog lines yet."
            : $"{entries.Count} change(s). {snaps} snapshot file(s).";
    }
}
