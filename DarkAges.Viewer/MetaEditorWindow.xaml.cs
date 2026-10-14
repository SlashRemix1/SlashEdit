using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DarkAges.Formats.Catalog;
using DarkAges.Formats.Data;
using DarkAges.Formats.IO;
using DarkAges.Viewer.Services;

namespace DarkAges.Viewer;

public partial class MetaEditorWindow : Window
{
    private readonly ArchiveCache? _cache;
    private readonly List<CatalogEntry> _catalogs;
    private readonly ObservableCollection<FileRow> _files = [];
    private readonly List<FileRow> _all = [];
    private FileRow? _current;
    private MetaFile? _meta;
    private DataTable? _table;

    public MetaEditorWindow(ArchiveCache? cache, IReadOnlyList<CatalogEntry> catalogs)
    {
        InitializeComponent();
        _cache = cache;
        _catalogs = catalogs.ToList();
        FileList.ItemsSource = _files;
        LoadFiles();
    }

    private void LoadFiles()
    {
        _all.Clear();
        if (_cache is null)
        {
            StatusText.Text = "Load a client first.";
            return;
        }

        foreach (CatalogEntry catalog in _catalogs.Where(c =>
                     c.Role is ArchiveRole.Metadata or ArchiveRole.Misc or ArchiveRole.Unknown))
        {
            IArchive archive;
            try
            {
                archive = _cache.Get(catalog);
            }
            catch
            {
                continue;
            }

            foreach (ArchiveEntry entry in archive.Entries)
            {
                if (catalog.Role != ArchiveRole.Metadata && !MetaFile.IsKnownName(entry.Name))
                {
                    continue;
                }

                if (!MetaFile.LooksLike(entry))
                {
                    continue;
                }

                _all.Add(new FileRow
                {
                    Name = entry.Name,
                    Catalog = catalog,
                    Entry = entry
                });
            }
        }

        ApplyFilter();
        StatusText.Text = _all.Count == 0
            ? "No ItemInfo / SMonster / TMap metafiles. Legends AoC content.lrar has sprites, maps, and music only."
            : $"{_all.Count} metafiles (ItemInfo, SMonster, TMap, NationDesc, …).";
    }

    private void FilterChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string q = FilterBox.Text.Trim();
        _files.Clear();
        foreach (FileRow row in _all.Where(r => r.Matches(q)))
        {
            _files.Add(row);
        }
    }

    private void FileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItem is not FileRow row || _cache is null)
        {
            return;
        }

        PullGrid();
        _current = row;
        try
        {
            IArchive archive = _cache.Get(row.Catalog);
            ArchiveEntry? entry = archive.GetEntry(row.Name) ?? row.Entry;
            if (entry is null)
            {
                return;
            }

            _meta = MetaFile.Load(entry, row.Catalog.SourcePath);
            _table = new DataTable();
            foreach (string header in _meta.Headers())
            {
                _table.Columns.Add(header);
            }

            foreach (string[] values in _meta.Rows())
            {
                DataRow data = _table.NewRow();
                for (int i = 0; i < values.Length && i < _table.Columns.Count; i++)
                {
                    data[i] = values[i];
                }

                _table.Rows.Add(data);
            }

            NodeGrid.ItemsSource = _table.DefaultView;
            StatusText.Text = $"{row.Name}  {_meta.Nodes.Count} nodes. TMap properties are warp lines (dest x1 y1 x2 y2 flag).";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Metafile");
        }
    }

    private void AddNodeClicked(object sender, RoutedEventArgs e)
    {
        if (_table is null)
        {
            return;
        }

        _table.Rows.Add(_table.NewRow());
        if (_current is not null)
        {
            _current.Dirty = true;
        }
    }

    private void RemoveNodeClicked(object sender, RoutedEventArgs e)
    {
        if (NodeGrid.SelectedItem is not DataRowView view)
        {
            return;
        }

        view.Row.Delete();
        if (_current is not null)
        {
            _current.Dirty = true;
        }
    }

    private void OpenButtonMenu(object sender, RoutedEventArgs e) => ButtonMenu.Open(sender);

    private void ExportDataClicked(object sender, RoutedEventArgs e)
    {
        TableDump dump;
        string stem;
        if (_table is not null && _table.Columns.Count > 0)
        {
            var columns = _table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList();
            dump = TableDump.Create(
                "Metafile",
                columns,
                _table.Rows.Cast<DataRow>()
                    .Where(row => row.RowState != DataRowState.Deleted)
                    .Select(row => (IReadOnlyList<object?>)columns.Select(c => row[c]?.ToString() ?? "").ToList()));
            stem = Path.GetFileNameWithoutExtension(_current?.Name ?? "metafile");
        }
        else
        {
            dump = TableDump.Create(
                "Metafiles",
                ["Name", "Archive"],
                _files.Select(f => (IReadOnlyList<object?>)new object?[] { f.Name, f.Catalog.DisplayName }));
            stem = "metafiles";
        }

        TableExport.Save(this, stem, dump, sender, StatusText);
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (_current is null || _meta is null || _table is null || _cache is null)
        {
            return;
        }

        try
        {
            PullGrid();
            byte[] data = _meta.Save(_current.Catalog.SourcePath);
            DatSave.Write(_cache, _current.Catalog, _current.Name, data);
            _current.Dirty = false;
            StatusText.Text = $"Saved {_current.Name} into {_current.Catalog.DisplayName}.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Save metafile");
        }
    }

    private void PullGrid()
    {
        if (_meta is null || _table is null)
        {
            return;
        }

        var nodes = new List<MetaNode>();
        foreach (DataRow row in _table.Rows)
        {
            if (row.RowState == DataRowState.Deleted)
            {
                continue;
            }

            string name = row[0]?.ToString() ?? "";
            var props = new List<string>();
            for (int i = 1; i < _table.Columns.Count; i++)
            {
                props.Add(row[i]?.ToString() ?? "");
            }

            while (props.Count > 0 && string.IsNullOrWhiteSpace(props[^1]))
            {
                props.RemoveAt(props.Count - 1);
            }

            nodes.Add(new MetaNode(name, props));
        }

        _meta.Nodes = nodes;
        if (_current is not null)
        {
            _current.Dirty = true;
        }
    }

    private sealed class FileRow
    {
        public string Name { get; set; } = "";
        public CatalogEntry Catalog { get; set; } = new();
        public ArchiveEntry? Entry { get; set; }
        public bool Dirty { get; set; }
        public string Label => $"{Name}{(Dirty ? " *" : "")}  ({Catalog.DisplayName})";

        public bool Matches(string q) =>
            string.IsNullOrWhiteSpace(q)
            || Name.Contains(q, StringComparison.OrdinalIgnoreCase);
    }
}
