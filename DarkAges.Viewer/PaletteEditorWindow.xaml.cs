using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DarkAges.Formats.Catalog;
using DarkAges.Formats.Data;
using DarkAges.Formats.Drawing;
using DarkAges.Formats.IO;
using DarkAges.Viewer.Services;
using WinForms = System.Windows.Forms;

namespace DarkAges.Viewer;

public partial class PaletteEditorWindow : Window
{
    private readonly ArchiveCache? _cache;
    private readonly List<CatalogEntry> _catalogs;
    private readonly ObservableCollection<FileRow> _files = [];
    private readonly List<FileRow> _all = [];
    private readonly ObservableCollection<TableRow> _tableRows = [];
    private readonly ObservableCollection<OriginRow> _originRows = [];
    private FileRow? _current;
    private Palette? _palette;

    public PaletteEditorWindow(ArchiveCache? cache, IReadOnlyList<CatalogEntry> catalogs)
    {
        InitializeComponent();
        _cache = cache;
        _catalogs = catalogs.ToList();
        FileList.ItemsSource = _files;
        TableGrid.ItemsSource = _tableRows;
        OriginGrid.ItemsSource = _originRows;
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

        foreach (CatalogEntry catalog in _catalogs)
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
                string ext = entry.Extension;
                if (!ext.Equals(".pal", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".tbl", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _all.Add(new FileRow
                {
                    Name = entry.Name,
                    Catalog = catalog,
                    Entry = entry,
                    IsTable = ext.Equals(".tbl", StringComparison.OrdinalIgnoreCase)
                });
            }
        }

        ApplyFilter();
        StatusText.Text = _all.Count == 0
            ? "No .pal / .tbl files. Legends AoC sprites are already RGBA, so this editor stays empty."
            : $"{_all.Count} palettes and tables.";
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

        _current = row;
        try
        {
            IArchive archive = _cache.Get(row.Catalog);
            ArchiveEntry? entry = archive.GetEntry(row.Name) ?? row.Entry;
            if (entry is null)
            {
                return;
            }

            if (row.IsTable && EffectCenterTable.IsCenterTableName(row.Name))
            {
                var centers = EffectCenterTable.Load(entry, 0);
                _originRows.Clear();
                for (int i = 0; i < centers.Points.Count; i++)
                {
                    (short x, short y) = centers.Points[i];
                    _originRows.Add(new OriginRow { Frame = i, X = x, Y = y });
                }

                SwatchHost.Visibility = Visibility.Collapsed;
                TableGrid.Visibility = Visibility.Collapsed;
                OriginGrid.Visibility = Visibility.Visible;
                StatusText.Text = $"{row.Name}  {_originRows.Count} effect origins (default 28,70).";
                return;
            }

            if (row.IsTable)
            {
                var table = new PaletteTable();
                table.AddTable(entry);
                _tableRows.Clear();
                foreach (PaletteTableEntry item in table.Entries)
                {
                    _tableRows.Add(new TableRow
                    {
                        Minimum = item.Minimum,
                        Maximum = item.Maximum,
                        PaletteNumber = item.PaletteNumber,
                        Misc = item.Misc
                    });
                }

                SwatchHost.Visibility = Visibility.Collapsed;
                TableGrid.Visibility = Visibility.Visible;
                OriginGrid.Visibility = Visibility.Collapsed;
                StatusText.Text = $"{row.Name}  {_tableRows.Count} ranges (last match wins).";
                return;
            }

            _palette = new Palette(entry);
            BuildSwatches();
            SwatchHost.Visibility = Visibility.Visible;
            TableGrid.Visibility = Visibility.Collapsed;
            OriginGrid.Visibility = Visibility.Collapsed;
            StatusText.Text = $"{row.Name}  click a cell to edit RGB. Gold outline is the dye range 96–101.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Palette");
        }
    }

    private void BuildSwatches()
    {
        SwatchGrid.Children.Clear();
        if (_palette is null)
        {
            return;
        }

        for (int i = 0; i < 256; i++)
        {
            var (r, g, b) = _palette[i];
            bool dye = DyeSlots.Contains(i);
            var cell = new System.Windows.Shapes.Rectangle
            {
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b)),
                Stroke = new SolidColorBrush(dye
                    ? System.Windows.Media.Color.FromRgb(212, 168, 80)
                    : System.Windows.Media.Color.FromRgb(58, 63, 75)),
                StrokeThickness = dye ? 2 : 1,
                Tag = i,
                ToolTip = dye ? $"{i}: {r},{g},{b}  (dye slot)" : $"{i}: {r},{g},{b}"
            };
            cell.MouseLeftButtonDown += SwatchClicked;
            SwatchGrid.Children.Add(cell);
        }
    }

    private void SwatchClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Shapes.Rectangle cell || cell.Tag is not int index || _palette is null)
        {
            return;
        }

        var (r, g, b) = _palette[index];
        using var dialog = new WinForms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(r, g, b),
            FullOpen = true
        };
        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        _palette.Set(index, dialog.Color.R, dialog.Color.G, dialog.Color.B);
        cell.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B));
        cell.ToolTip = $"{index}: {dialog.Color.R},{dialog.Color.G},{dialog.Color.B}";
        if (_current is not null)
        {
            _current.Dirty = true;
        }
    }

    private void NewPaletteClicked(object sender, RoutedEventArgs e)
    {
        CatalogEntry? catalog = _current?.Catalog ?? _catalogs.FirstOrDefault();
        if (catalog is null || _cache is null)
        {
            System.Windows.MessageBox.Show(this, "Load a client first.", "New palette");
            return;
        }

        string name = NextPaletteName(catalog);
        var pal = new Palette();
        pal.Set(1, 255, 255, 255);
        try
        {
            DatSave.Write(_cache, catalog, name, pal.ToBytes());
            LoadFiles();
            FileRow? row = _files.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (row is not null)
            {
                FileList.SelectedItem = row;
            }

            StatusText.Text = $"Inserted {name} into {catalog.DisplayName}.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "New palette");
        }
    }

    private void DyeLayoutClicked(object sender, RoutedEventArgs e)
    {
        if (_palette is null || _current is null)
        {
            return;
        }

        DyeSlots.ApplyLayout(_palette);
        BuildSwatches();
        _current.Dirty = true;
        StatusText.Text = "Applied dye-slot ramp at indices 96–101.";
    }

    private void CopyDyeClicked(object sender, RoutedEventArgs e)
    {
        Palette? source = PickOtherPalette();
        if (source is null || _palette is null || _current is null)
        {
            return;
        }

        DyeSlots.Copy(source, _palette);
        BuildSwatches();
        _current.Dirty = true;
        StatusText.Text = "Copied dye slots 96–101.";
    }

    private void RemapFromClicked(object sender, RoutedEventArgs e)
    {
        Palette? source = PickOtherPalette();
        if (source is null || _palette is null || _current is null)
        {
            return;
        }

        Palette next = source.Clone();
        DyeSlots.Copy(_palette, next);
        _palette = next;
        BuildSwatches();
        _current.Dirty = true;
        StatusText.Text = "Copied the other palette and kept this file's dye slots 96–101.";
    }

    private Palette? PickOtherPalette()
    {
        FileRow? other = _all.FirstOrDefault(r => !r.IsTable && r != _current);
        if (FileList.SelectedItem is FileRow && _files.Count > 1)
        {
            other = _files.FirstOrDefault(r => !r.IsTable && r != _current);
        }

        var names = _all.Where(r => !r.IsTable).Select(r => r.Name).Distinct().ToList();
        if (names.Count == 0 || _cache is null)
        {
            return null;
        }

        var pick = new Window
        {
            Title = "Choose palette",
            Width = 380,
            Height = 460,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background
        };
        var list = new System.Windows.Controls.ListBox { ItemsSource = names, Margin = new Thickness(8) };
        if (other is not null)
        {
            list.SelectedItem = other.Name;
        }
        else if (names.Count > 0)
        {
            list.SelectedIndex = 0;
        }

        string? chosen = null;
        var ok = new System.Windows.Controls.Button { Content = "OK", Margin = new Thickness(8), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        ok.Click += (_, _) =>
        {
            chosen = list.SelectedItem as string;
            pick.DialogResult = true;
        };
        var dock = new DockPanel();
        DockPanel.SetDock(ok, Dock.Bottom);
        dock.Children.Add(ok);
        dock.Children.Add(list);
        pick.Content = dock;
        if (pick.ShowDialog() != true || chosen is null)
        {
            return null;
        }

        FileRow? row = _all.FirstOrDefault(r => r.Name.Equals(chosen, StringComparison.OrdinalIgnoreCase));
        if (row?.Entry is null)
        {
            return null;
        }

        return new Palette(row.Entry);
    }

    private void AddRowClicked(object sender, RoutedEventArgs e)
    {
        if (_current is null || !_current.IsTable)
        {
            return;
        }

        if (EffectCenterTable.IsCenterTableName(_current.Name))
        {
            _originRows.Add(new OriginRow { Frame = _originRows.Count, X = EffectCenterTable.DefaultX, Y = EffectCenterTable.DefaultY });
        }
        else
        {
            _tableRows.Add(new TableRow());
        }

        _current.Dirty = true;
    }

    private void RemoveRowClicked(object sender, RoutedEventArgs e)
    {
        if (OriginGrid.SelectedItem is OriginRow origin)
        {
            _originRows.Remove(origin);
            if (_current is not null)
            {
                _current.Dirty = true;
            }

            return;
        }

        if (TableGrid.SelectedItem is TableRow row)
        {
            _tableRows.Remove(row);
            if (_current is not null)
            {
                _current.Dirty = true;
            }
        }
    }

    private void OpenButtonMenu(object sender, RoutedEventArgs e) => ButtonMenu.Open(sender);

    private void ExportDataClicked(object sender, RoutedEventArgs e)
    {
        TableDump dump;
        string stem = _current?.Name ?? "palette";
        if (_current?.IsTable == true)
        {
            dump = TableDump.Create(
                "PaletteTable",
                ["Minimum", "Maximum", "PaletteNumber", "Misc"],
                _tableRows.Select(r => (IReadOnlyList<object?>)new object?[] { r.Minimum, r.Maximum, r.PaletteNumber, r.Misc }));
        }
        else if (_palette is not null)
        {
            var rows = new List<IReadOnlyList<object?>>(256);
            for (int i = 0; i < 256; i++)
            {
                (byte r, byte g, byte b) = _palette[i];
                rows.Add([i, (int)r, (int)g, (int)b]);
            }

            dump = TableDump.Create("Palette", ["Index", "R", "G", "B"], rows);
        }
        else
        {
            dump = TableDump.Create(
                "Palettes",
                ["Name", "Archive", "IsTable"],
                _files.Select(f => (IReadOnlyList<object?>)new object?[] { f.Name, f.Catalog.DisplayName, f.IsTable }));
            stem = "palettes";
        }

        TableExport.Save(this, System.IO.Path.GetFileNameWithoutExtension(stem), dump, sender, StatusText);
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (_current is null || _cache is null)
        {
            return;
        }

        try
        {
            byte[] data;
            if (_current.IsTable && EffectCenterTable.IsCenterTableName(_current.Name))
            {
                var table = EffectCenterTable.ForFrames(0);
                for (int i = 0; i < _originRows.Count; i++)
                {
                    table.Set(i, (short)_originRows[i].X, (short)_originRows[i].Y);
                }

                data = table.ToBytes();
            }
            else if (_current.IsTable)
            {
                var table = new PaletteTable();
                table.SetEntries(_tableRows.Select(r =>
                    new PaletteTableEntry(r.Minimum, r.Maximum, r.PaletteNumber, r.Misc)));
                data = table.ToBytes();
            }
            else
            {
                if (_palette is null)
                {
                    return;
                }

                data = _palette.ToBytes();
            }

            DatSave.Write(_cache, _current.Catalog, _current.Name, data);
            _current.Dirty = false;
            StatusText.Text = $"Saved {_current.Name} into {_current.Catalog.DisplayName}.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Save to DAT");
        }
    }

    private string NextPaletteName(CatalogEntry catalog)
    {
        IArchive archive = _cache!.Get(catalog);
        int n = 1;
        while (archive.GetEntry($"new{n:D3}.pal") is not null)
        {
            n++;
        }

        return $"new{n:D3}.pal";
    }

    private sealed class FileRow
    {
        public string Name { get; set; } = "";
        public CatalogEntry Catalog { get; set; } = new();
        public ArchiveEntry? Entry { get; set; }
        public bool IsTable { get; set; }
        public bool Dirty { get; set; }
        public string Label => $"{Name}{(Dirty ? " *" : "")}  ({Catalog.DisplayName})";

        public bool Matches(string q) =>
            string.IsNullOrWhiteSpace(q)
            || Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || Catalog.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TableRow
    {
        public int Minimum { get; set; }
        public int Maximum { get; set; }
        public int PaletteNumber { get; set; }
        public int Misc { get; set; }
    }

    private sealed class OriginRow
    {
        public int Frame { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
    }
}
