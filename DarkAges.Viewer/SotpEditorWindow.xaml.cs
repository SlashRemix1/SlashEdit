using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DarkAges.Formats.Catalog;
using DarkAges.Formats.Imaging;
using DarkAges.Formats.IO;
using DarkAges.Viewer.Imaging;
using DarkAges.Viewer.Maps;
using DarkAges.Viewer.Services;

namespace DarkAges.Viewer;

public partial class SotpEditorWindow : Window
{
    private readonly MapAssets _assets;
    private readonly ArchiveCache? _cache;
    private readonly ClientProfile? _client;
    private byte[] _sotp;
    private readonly ObservableCollection<WallRow> _rows = [];
    private readonly List<WallFamily> _families;
    private bool _dirty;
    private int _selectedId;

    public SotpEditorWindow(MapAssets assets, ArchiveCache? cache, ClientProfile? client)
    {
        InitializeComponent();
        _assets = assets;
        _cache = cache;
        _client = client;
        _sotp = (byte[])assets.Sotp.Clone();
        _families = WallFamilies.Cluster(assets.WallIds).ToList();
        WallList.ItemsSource = _rows;
        CategoryBox.ItemsSource = new[] { "All", "Tab block (15)", "Any block", "Overlay (0x80)", "Walkable (0)" }
            .Concat(_families.Select(f => f.Name));
        CategoryBox.SelectedIndex = 0;
        ApplyFilter();
        StatusText.Text = $"{assets.WallIds.Count} walls, sotp {_sotp.Length} bytes. Tab map uses 15; any non-zero blocks walking.";
    }

    private void FilterChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void FilterChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string cat = CategoryBox.SelectedItem as string ?? "All";
        string q = FilterBox?.Text.Trim() ?? "";
        int.TryParse(q, out int wantId);
        _rows.Clear();
        foreach (int id in _assets.WallIds)
        {
            byte value = Flag(id);
            if (!Matches(cat, id, value))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(q)
                && id != wantId
                && !id.ToString().Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _rows.Add(new WallRow(id, $"{WallFamilies.Label(id, _families)}  [{value}]", value));
        }

        WallRow? match = _rows.FirstOrDefault(r => r.Id == _selectedId);
        if (match is not null)
        {
            WallList.SelectedItem = match;
        }
    }

    private bool Matches(string cat, int id, byte value) => cat switch
    {
        "Tab block (15)" => value == 15,
        "Any block" => value != 0,
        "Overlay (0x80)" => (value & 0x80) == 0x80,
        "Walkable (0)" => value == 0,
        "All" => true,
        _ => WallFamilies.Find(id, _families) is WallFamily family && family.Name == cat
    };

    private void WallSelected(object sender, SelectionChangedEventArgs e)
    {
        if (WallList.SelectedItem is WallRow row)
        {
            ShowWall(row);
        }
    }

    private void ShowWall(WallRow row)
    {
        _selectedId = row.Id;
        PixelBuffer? wall = _assets.Wall(row.Id);
        WallImage.Source = wall is null ? null : WpfImages.ToBitmap(wall);
        byte value = Flag(row.Id);
        ValueBox.Text = value.ToString();
        DetailText.Text =
            $"stc{row.Id:D5}  sotp[{row.Id - 1}] = {value}\n"
            + (value == 15 ? "Tab map: blocked.\n" : "Tab map: walkable.\n")
            + (value != 0 ? "Pathing: blocked.\n" : "Pathing: walkable.\n")
            + ((value & 0x80) == 0x80 ? "Blit: additive overlay." : "Blit: opaque.");
    }

    private void WalkClicked(object sender, RoutedEventArgs e) => SetSelected(0);

    private void BlockClicked(object sender, RoutedEventArgs e) => SetSelected(15);

    private void OverlayClicked(object sender, RoutedEventArgs e) => SetSelected(0x80);

    private void ValueKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ValueCommitted(sender, e);
            e.Handled = true;
        }
    }

    private void ValueCommitted(object sender, RoutedEventArgs e)
    {
        if (byte.TryParse(ValueBox.Text.Trim(), out byte value))
        {
            SetSelected(value);
        }
    }

    private void SetSelected(byte value)
    {
        if (_selectedId <= 0 && WallList.SelectedItem is WallRow row)
        {
            _selectedId = row.Id;
        }

        if (_selectedId <= 0)
        {
            return;
        }

        EnsureSize(_selectedId);
        _sotp[_selectedId - 1] = value;
        _dirty = true;
        StatusText.Text = $"stc{_selectedId:D5} = {value}. Save to DAT when ready.";
        ApplyFilter();
        WallRow? match = _rows.FirstOrDefault(r => r.Id == _selectedId);
        if (match is not null)
        {
            ShowWall(match);
        }
    }

    private void OpenButtonMenu(object sender, RoutedEventArgs e) => ButtonMenu.Open(sender);

    private void ExportDataClicked(object sender, RoutedEventArgs e)
    {
        IEnumerable<int> ids = _rows.Count > 0 ? _rows.Select(r => r.Id) : _assets.WallIds;
        TableDump dump = DataDumps.Sotp(_assets.Label, ids, Flag);
        TableExport.Save(this, "sotp", dump, sender, StatusText);
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (!_dirty)
        {
            StatusText.Text = "No sotp changes.";
            return;
        }

        try
        {
            DatSave.Write(_cache, _assets.IaPath, [("sotp.dat", (byte[])_sotp.Clone())]);
            _assets.ReplaceSotp((byte[])_sotp.Clone());
            _dirty = false;
            ClientChangeLog.Append(_client, "sotp", $"Saved sotp.dat ({_sotp.Length} flags) into {Path.GetFileName(_assets.IaPath)}.");
            StatusText.Text = $"Saved sotp.dat into {Path.GetFileName(_assets.IaPath)}.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Save sotp");
        }
    }

    private byte Flag(int wallId)
    {
        int i = wallId - 1;
        return i < 0 || i >= _sotp.Length ? (byte)0 : _sotp[i];
    }

    private void EnsureSize(int wallId)
    {
        if (wallId <= _sotp.Length)
        {
            return;
        }

        Array.Resize(ref _sotp, wallId);
    }

    private sealed record WallRow(int Id, string Label, byte Value);
}
