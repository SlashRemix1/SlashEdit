using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DarkAges.Formats.Catalog;
using DarkAges.Formats.Data;
using DarkAges.Viewer.Services;
using WinForms = System.Windows.Forms;

namespace DarkAges.Viewer;

public partial class LightEditorWindow : Window
{
    private readonly ArchiveCache? _cache;
    private readonly List<CatalogEntry> _catalogs;
    private readonly ClientProfile? _client;
    private readonly ObservableCollection<LightType> _types = [];
    private readonly ObservableCollection<MapRow> _maps = [];
    private LightMeta _meta = LightMeta.Default();
    private CatalogEntry? _catalog;
    private string _fileName = "Light";
    private int _slotIndex;
    private bool _dirty;
    private bool _syncing;

    public LightEditorWindow(ArchiveCache? cache, IReadOnlyList<CatalogEntry> catalogs, ClientProfile? client = null)
    {
        InitializeComponent();
        _cache = cache;
        _catalogs = catalogs.ToList();
        _client = client;
        TypeList.ItemsSource = _types;
        MapList.ItemsSource = _maps;
        LoadFromArchives();
        BindSlots();
        SelectType(0);
        UpdateStatus();
    }

    private void LoadFromArchives()
    {
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
                if (!LightMeta.IsLightName(entry.Name) || !MetaFile.LooksLike(entry))
                {
                    continue;
                }

                try
                {
                    _meta = LightMeta.FromMetaFile(MetaFile.Load(entry, catalog.SourcePath));
                    _catalog = catalog;
                    _fileName = entry.Name;
                    _meta.FileName = entry.Name;
                    RefreshTypes();
                    return;
                }
                catch
                {
                    // try the next Light-looking entry
                }
            }
        }

        _catalog = _catalogs.FirstOrDefault(c => c.Role == ArchiveRole.Metadata)
            ?? _catalogs.FirstOrDefault();
        _meta = LightMeta.Default(_fileName);
        RefreshTypes();
    }

    private void RefreshTypes()
    {
        _syncing = true;
        _types.Clear();
        foreach (LightType type in _meta.Types)
        {
            _types.Add(type);
        }

        _syncing = false;
        if (TypeList.SelectedIndex < 0 && _types.Count > 0)
        {
            TypeList.SelectedIndex = 0;
        }

        RefreshMaps();
        BindSlots();
        LoadSlotEditor();
        PaintCycle();
    }

    private LightType? SelectedType => TypeList.SelectedItem as LightType;

    private LightSlot? SelectedSlot
    {
        get
        {
            LightType? type = SelectedType;
            if (type is null)
            {
                return null;
            }

            int i = Math.Clamp(_slotIndex, 0, type.Slots.Length - 1);
            return type.Slots[i];
        }
    }

    private void TypeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        TypeNameBox.Text = SelectedType?.Name ?? "";
        _slotIndex = 0;
        BindSlots();
        LoadSlotEditor();
        RefreshMaps();
        PaintCycle();
    }

    private void SelectType(int index)
    {
        if (_types.Count == 0)
        {
            return;
        }

        TypeList.SelectedIndex = Math.Clamp(index, 0, _types.Count - 1);
    }

    private void BindSlots()
    {
        SlotGrid.Children.Clear();
        LightType? type = SelectedType;
        if (type is null)
        {
            return;
        }

        for (int i = 0; i < type.Slots.Length; i++)
        {
            LightSlot slot = type.Slots[i];
            var cell = new Border
            {
                Margin = new Thickness(3),
                Padding = new Thickness(4),
                Background = new SolidColorBrush(SlotColor(slot)),
                BorderBrush = i == _slotIndex
                    ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(110, 168, 254))
                    : new SolidColorBrush(System.Windows.Media.Color.FromRgb(58, 63, 75)),
                BorderThickness = new Thickness(i == _slotIndex ? 2 : 1),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = i,
                ToolTip = $"{type.Name}_{slot.HexKey}  hours {slot.StartHour}–{slot.EndHour}  A{slot.A} R{slot.R} G{slot.G} B{slot.B}"
            };
            cell.Child = new TextBlock
            {
                Text = slot.HexKey,
                Foreground = System.Windows.Media.Brushes.White,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold
            };
            cell.MouseLeftButtonDown += SlotClicked;
            SlotGrid.Children.Add(cell);
        }
    }

    private void SlotClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: int index })
        {
            _slotIndex = index;
            BindSlots();
            LoadSlotEditor();
        }
    }

    private void LoadSlotEditor()
    {
        LightSlot? slot = SelectedSlot;
        _syncing = true;
        if (slot is null)
        {
            SlotKeyLabel.Text = "";
            StartHourBox.Text = "";
            EndHourBox.Text = "";
            ABox.Text = "";
            RBox.Text = "";
            GBox.Text = "";
            BBox.Text = "";
            SlotSwatch.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 20, 26));
            _syncing = false;
            return;
        }

        SlotKeyLabel.Text = $"{SelectedType?.Name}_{slot.HexKey}";
        StartHourBox.Text = slot.StartHour.ToString(CultureInfo.InvariantCulture);
        EndHourBox.Text = slot.EndHour.ToString(CultureInfo.InvariantCulture);
        ABox.Text = slot.A.ToString(CultureInfo.InvariantCulture);
        RBox.Text = slot.R.ToString(CultureInfo.InvariantCulture);
        GBox.Text = slot.G.ToString(CultureInfo.InvariantCulture);
        BBox.Text = slot.B.ToString(CultureInfo.InvariantCulture);
        SlotSwatch.Background = new SolidColorBrush(SlotColor(slot));
        _syncing = false;
    }

    private void SlotFieldCommitted(object sender, RoutedEventArgs e) => PullSlot();

    private void PullSlot()
    {
        if (_syncing || SelectedSlot is not LightSlot slot)
        {
            return;
        }

        slot.StartHour = ParseByte(StartHourBox.Text, slot.StartHour, 23);
        slot.EndHour = ParseByte(EndHourBox.Text, slot.EndHour, 23);
        slot.A = ParseByte(ABox.Text, slot.A, 32);
        slot.R = ParseByte(RBox.Text, slot.R, 255);
        slot.G = ParseByte(GBox.Text, slot.G, 255);
        slot.B = ParseByte(BBox.Text, slot.B, 255);
        _dirty = true;
        BindSlots();
        LoadSlotEditor();
        PaintCycle();
        UpdateStatus();
    }

    private void PickColorClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedSlot is not LightSlot slot)
        {
            return;
        }

        using var dialog = new WinForms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(slot.R, slot.G, slot.B),
            FullOpen = true
        };
        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        slot.R = dialog.Color.R;
        slot.G = dialog.Color.G;
        slot.B = dialog.Color.B;
        _dirty = true;
        BindSlots();
        LoadSlotEditor();
        PaintCycle();
        UpdateStatus();
    }

    private void TypeNameCommitted(object sender, RoutedEventArgs e) => RenameType();

    private void TypeNameKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RenameType();
            e.Handled = true;
        }
    }

    private void RenameType()
    {
        if (SelectedType is not LightType type)
        {
            return;
        }

        string name = TypeNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Equals(type.Name, StringComparison.Ordinal))
        {
            TypeNameBox.Text = type.Name;
            return;
        }

        string previous = type.Name;
        type.Name = name;
        foreach (int key in _meta.Maps.Where(p => p.Value.Equals(previous, StringComparison.OrdinalIgnoreCase))
                     .Select(p => p.Key)
                     .ToList())
        {
            _meta.Maps[key] = name;
        }

        _dirty = true;
        int index = TypeList.SelectedIndex;
        RefreshTypes();
        SelectType(index);
        UpdateStatus();
    }

    private void AddTypeClicked(object sender, RoutedEventArgs e)
    {
        LightType type = _meta.AddType("Light");
        _dirty = true;
        RefreshTypes();
        TypeList.SelectedItem = type;
        TypeNameBox.Focus();
        TypeNameBox.SelectAll();
        UpdateStatus();
    }

    private void RemoveTypeClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedType is not LightType type || _meta.Types.Count <= 1)
        {
            return;
        }

        _meta.Types.Remove(type);
        foreach (int key in _meta.Maps.Where(p => p.Value.Equals(type.Name, StringComparison.OrdinalIgnoreCase))
                     .Select(p => p.Key)
                     .ToList())
        {
            _meta.Maps.Remove(key);
        }

        _dirty = true;
        RefreshTypes();
        SelectType(0);
        UpdateStatus();
    }

    private void AddMapClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedType is not LightType type)
        {
            return;
        }

        if (!int.TryParse(MapIdBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int mapId)
            || mapId <= 0)
        {
            System.Windows.MessageBox.Show(this, "Enter a map id (lod number).", "Light");
            return;
        }

        _meta.Assign(mapId, type.Name);
        _dirty = true;
        MapIdBox.Clear();
        RefreshMaps();
        UpdateStatus();
    }

    private void RemoveMapClicked(object sender, RoutedEventArgs e)
    {
        if (MapList.SelectedItem is not MapRow row)
        {
            return;
        }

        _meta.Maps.Remove(row.MapId);
        _dirty = true;
        RefreshMaps();
        UpdateStatus();
    }

    private void RefreshMaps()
    {
        _maps.Clear();
        LightType? type = SelectedType;
        if (type is null)
        {
            return;
        }

        foreach ((int mapId, string name) in _meta.Maps.OrderBy(p => p.Key))
        {
            if (name.Equals(type.Name, StringComparison.OrdinalIgnoreCase))
            {
                _maps.Add(new MapRow(mapId, $"lod{mapId}  {name}"));
            }
        }
    }

    private void PaintCycle()
    {
        CycleBar.Children.Clear();
        LightType? type = SelectedType;
        if (type is null)
        {
            return;
        }

        foreach (LightSlot slot in type.Slots)
        {
            CycleBar.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Fill = new SolidColorBrush(SlotColor(slot)),
                Margin = new Thickness(1, 0, 1, 0),
                ToolTip = $"{type.Name}_{slot.HexKey}"
            });
        }
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        PullSlot();
        if (_cache is null)
        {
            System.Windows.MessageBox.Show(this, "Load a client first.", "Save Light");
            return;
        }

        CatalogEntry? catalog = _catalog ?? _catalogs.FirstOrDefault(c => c.Role == ArchiveRole.Metadata);
        if (catalog is null || !DatSave.IsWritable(catalog))
        {
            System.Windows.MessageBox.Show(this, "No writable metadata DAT or folder.", "Save Light");
            return;
        }

        try
        {
            _meta.FileName = _fileName;
            byte[] data = _meta.Save(catalog.SourcePath);
            DatSave.Write(_cache, catalog, _fileName, data);
            _catalog = catalog;
            _dirty = false;
            ClientChangeLog.Append(
                _client,
                "light",
                $"Saved {_fileName} ({_meta.Types.Count} types, {_meta.Maps.Count} maps) into {catalog.DisplayName}.");
            UpdateStatus();
            StatusText.Text = $"Saved {_fileName} into {catalog.DisplayName}.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Save Light");
        }
    }

    private void UpdateStatus()
    {
        string dirty = _dirty ? "  unsaved" : "";
        StatusText.Text =
            $"{_fileName}  {_meta.Types.Count} type(s), {_meta.Maps.Count} map assignment(s).{dirty}  7.xx uses this metafile; HEA overlay save stays blocked.";
    }

    private static System.Windows.Media.Color SlotColor(LightSlot slot)
    {
        byte a = (byte)Math.Clamp(255 - slot.A * 8, 40, 255);
        return System.Windows.Media.Color.FromArgb(a, slot.R, slot.G, slot.B);
    }

    private static byte ParseByte(string text, byte fallback, byte max)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
        {
            return fallback;
        }

        return (byte)Math.Clamp(n, 0, max);
    }

    private sealed record MapRow(int MapId, string Label);
}
