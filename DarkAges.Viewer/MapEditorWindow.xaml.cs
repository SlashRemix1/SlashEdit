using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DarkAges.Formats.Catalog;
using DarkAges.Formats.Data;
using DarkAges.Formats.Drawing;
using DarkAges.Formats.IO;
using DarkAges.Formats.Imaging;
using DarkAges.Formats.Maps;
#if LOCAL_UNITY
using DarkAges.Viewer.Export;
#endif
using DarkAges.Viewer.Imaging;
using DarkAges.Viewer.Maps;
using DarkAges.Viewer.Services;
using WinForms = System.Windows.Forms;

namespace DarkAges.Viewer;

public partial class MapEditorWindow : Window
{
    private const int PageSize = 96;
    private readonly MapAssets _assets;
    private readonly AssetWorkspace? _workspace;
    private readonly List<string> _mapFolders = [];
    private readonly List<IArchive> _packedMaps = [];
    private readonly WarpTable _warps;
    private readonly ServerWorld _serverWorld;
    private readonly MapNameIndex _mapNames;
    private readonly ArchiveCache? _cache;
    private readonly PaintMacroSlot?[] _macros = new PaintMacroSlot?[10];
    private readonly ObservableCollection<MacroSlotView> _macroBarSlots = [];
    private MacroWindow? _macroWindow;
    private int _activeMacroKey = -1;
    private readonly MapUndoHistory _history = new();
    private readonly IsoTileSelection _selection = new();
    private readonly IsoTileSelection _warpPick = new();
    private readonly List<MapListItem> _allMaps = [];
    private (int X, int Y)? _pendingLanding;
    private readonly DispatcherTimer _paintRedraw;
    private MapFile _map;
    private bool _openedFromArchive;
    private string? _packedSourcePath;
    private string? _packedEntryName;
    private bool _painting;
    private bool _syncingSize;
    private bool _dirty;
    private bool _redrawQueued;
    private int _page;
    private int _floorBrush = 1;
    private MapGeneratorOptions _generatorOptions = new();
    private int _leftBrush;
    private int _rightBrush;
    private int _structureBrush = 1;
    private List<StructureStamp> _structures = [];
    private StampCategory _stampGroup = StampCategory.Building;
    private PaintLayer _paletteLayer = PaintLayer.Floor;
    private bool _syncingGroups;
    private readonly Dictionary<StampCategory, int> _groupPage = [];
    private readonly Dictionary<StampCategory, int> _groupBrush = [];
    private readonly Dictionary<PaintLayer, int> _layerPage = [];
    private int _lastPaintX = int.MinValue;
    private int _lastPaintY = int.MinValue;
    private int _lastStatusX = int.MinValue;
    private int _lastStatusY = int.MinValue;
    private readonly HashSet<(int X, int Y)> _dirtyTiles = [];
    private PixelBuffer? _mapPixels;
    private WriteableBitmap? _mapBitmap;
    private MapAssets.MapView _mapView;
    private int _ghostTileX = int.MinValue;
    private int _ghostTileY = int.MinValue;
    private BitmapSource? _ghostBmp;
    private int _ghostCropX;
    private int _ghostCropY;
    private List<TileChoice> _palette = [];
    private MapSnapshot? _strokeStart;
    private bool _strokeChanged;
    private bool _panning;
    private double _mapZoom = 1;
    private System.Windows.Point _panStart;
    private double _panOffsetX;
    private double _panOffsetY;
    private int _editSerial;
    private int _savedSerial;
    private readonly Stack<MapVisit> _back = new();
    private readonly DispatcherTimer _autosave;
    private bool _restoringVisit;
    private bool _syncingRecent;
    private IReadOnlyList<FloorFamily> _floorFamilies = FloorFamilies.Seeds;
    private IReadOnlyList<WallFamily> _wallFamilies = [];
    private readonly List<IArchive> _heaArchives = [];
    private readonly List<IArchive> _overlayOwned = [];
    private readonly MapOverlayLayer _lightOverlay = new();
    private readonly MapOverlayLayer _effectOverlay = new();
    private bool _heaReady;
    private bool _effectArchivesReady;
    private PixelBuffer? _pendingOverlayStamp;
    private bool _syncingServer;

    public MapEditorWindow(
        IArchive seo,
        IArchive ia,
        IEnumerable<string>? mapFolders,
        string? initialMapPath = null,
        AssetWorkspace? workspace = null,
        bool snowFloors = false,
        int floorOffset = 0,
        IEnumerable<IArchive>? packedMaps = null,
        string? initialPackedMap = null,
        WarpTable? warps = null,
        ArchiveCache? cache = null,
        ServerWorld? serverWorld = null)
    {
        InitializeComponent();
        _assets = new MapAssets(seo, ia);
        _workspace = workspace;
        _cache = cache;
        _serverWorld = serverWorld ?? new ServerWorld();
        _warps = warps is not null && !ReferenceEquals(warps, WarpTable.Empty)
            ? warps
            : WarpTable.Load(
                workspace?.MetaFolders ?? [],
                WarpStore.FileFor(workspace?.ActiveClient),
                workspace?.WarpFolders);
        _mapNames = MapNameIndex.FromWorkspace(workspace);
        AddMapFolders(mapFolders);
        AddPackedMaps(packedMaps);
        _mapNames.AddArchives(_packedMaps);
        if (!string.IsNullOrWhiteSpace(initialMapPath))
        {
            string? dir = Path.GetDirectoryName(initialMapPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                AddMapFolders([dir]);
            }
        }

        _map = new MapFile(20, 20);
        _paintRedraw = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _paintRedraw.Tick += (_, _) =>
        {
            _paintRedraw.Stop();
            _redrawQueued = false;
            FlushMap();
        };
        LoadPaintMacros();
        InitMacroBar();
        _floorFamilies = FloorFamilies.Cluster(Enumerable.Range(1, Math.Max(_assets.FloorCount, _assets.SnowCount)));
        _wallFamilies = WallFamilies.Cluster(_assets.WallIds);
        _autosave = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _autosave.Tick += (_, _) => AutosaveTick();
        _autosave.Start();
        Closed += (_, _) =>
        {
            _autosave.Stop();
            _macroWindow?.Close();
            foreach (IArchive owned in _overlayOwned)
            {
                try
                {
                    owned.Dispose();
                }
                catch
                {
                    // ignore
                }
            }
        };
        _heaArchives.Add(_assets.Seo);
        _heaArchives.Add(_assets.Ia);
        _heaArchives.AddRange(_packedMaps);
        BindHeaLights();
        LoadOverlays();
        SyncOverlayTools();
        RefreshMapList();
        RefreshSizeBox();
        LoadPalettePage();
        Redraw();
        UpdateBrushPreview();
        StatusText.Text = "Select a tile, then click-drag on the map to paint. Overlay paints lights or applies effects. Middle-click copies a tile. Right-drag pans. Ctrl+Z/Y undo/redo. Ctrl+S saves.";
        Title = TitleText();
        UpdateHistoryButtons();
        if (SnowBox is not null && snowFloors)
        {
            SnowBox.IsChecked = true;
        }

        if (FloorOffsetBox is not null && floorOffset > 0)
        {
            FloorOffsetBox.Text = floorOffset.ToString();
        }

        if (!string.IsNullOrWhiteSpace(initialMapPath) && File.Exists(initialMapPath))
        {
            OpenPath(initialMapPath);
        }
        else if (!string.IsNullOrWhiteSpace(initialPackedMap))
        {
            IArchive? packed = _packedMaps.FirstOrDefault(a => a.GetEntry(initialPackedMap) is not null);
            if (packed is not null)
            {
                OpenPacked(packed, initialPackedMap);
            }
        }

        RefreshMacros();
        BindPaletteCategories();
        RefreshRecentMaps();
        TryAutoLoadServerWorld();
        RefreshAreaInfo();
        UpdateBackButton();
    }

    public IReadOnlyList<string> MapFolders => _mapFolders;

    public IReadOnlyList<IArchive> PackedMaps => _packedMaps;

    private PaintLayer Layer =>
        StructureLayer?.IsChecked == true ? PaintLayer.Structure
        : LeftLayer?.IsChecked == true ? PaintLayer.LeftWall
        : RightLayer?.IsChecked == true ? PaintLayer.RightWall
        : PaintLayer.Floor;

    private int CurrentBrush
    {
        get => Layer switch
        {
            PaintLayer.LeftWall => _leftBrush,
            PaintLayer.RightWall => _rightBrush,
            PaintLayer.Structure => _structureBrush,
            _ => _floorBrush
        };
        set
        {
            switch (Layer)
            {
                case PaintLayer.LeftWall:
                    _leftBrush = value;
                    break;
                case PaintLayer.RightWall:
                    _rightBrush = value;
                    break;
                case PaintLayer.Structure:
                    _structureBrush = value;
                    break;
                default:
                    _floorBrush = value;
                    break;
            }
        }
    }

    private void WindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        ModifierKeys mods = Keyboard.Modifiers;
        if (e.Key == Key.S && mods == ModifierKeys.Control)
        {
            e.Handled = true;
            SaveClicked(sender, e);
            return;
        }

        if (e.Key == Key.Left && mods == ModifierKeys.Alt)
        {
            e.Handled = true;
            BackClicked(sender, e);
            return;
        }

        if (e.Key == Key.C && mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            CopyTileClicked(sender, e);
            return;
        }

        if (e.Key == Key.Z && mods == ModifierKeys.Control)
        {
            e.Handled = true;
            UndoClicked(sender, e);
            return;
        }

        if (e.Key == Key.Y && mods == ModifierKeys.Control
            || e.Key == Key.Z && mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            RedoClicked(sender, e);
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_pendingOverlayStamp is not null)
            {
                _pendingOverlayStamp = null;
                StatusText.Text = "Cancelled overlay stamp.";
                e.Handled = true;
                return;
            }

            ClearSelection();
            e.Handled = true;
            return;
        }

        if (_selection.HasSelection && (e.Key == Key.Enter || (e.Key == Key.C && mods == ModifierKeys.Control)))
        {
            StampSelectionClicked(sender, e);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete && Layer == PaintLayer.Structure)
        {
            RemovePresetClicked(sender, e);
            e.Handled = true;
            return;
        }

        if (mods == ModifierKeys.Control && (e.Key == Key.OemPlus || e.Key == Key.Add))
        {
            NudgeMapZoom(1);
            e.Handled = true;
            return;
        }

        if (mods == ModifierKeys.Control && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
        {
            NudgeMapZoom(-1);
            e.Handled = true;
            return;
        }

        if (mods == ModifierKeys.None && TryMacroKey(e.Key, out int macroKey) && !IsTypingTarget())
        {
            e.Handled = true;
            ApplyPaintMacro(macroKey);
            return;
        }

        if (mods == ModifierKeys.Control && (e.Key == Key.D0 || e.Key == Key.NumPad0))
        {
            SetMapZoom(1);
            e.Handled = true;
        }
    }

    public static bool TryMacroKey(Key key, out int slot)
    {
        slot = key switch
        {
            Key.D0 or Key.NumPad0 => 0,
            Key.D1 or Key.NumPad1 => 1,
            Key.D2 or Key.NumPad2 => 2,
            Key.D3 or Key.NumPad3 => 3,
            Key.D4 or Key.NumPad4 => 4,
            Key.D5 or Key.NumPad5 => 5,
            Key.D6 or Key.NumPad6 => 6,
            Key.D7 or Key.NumPad7 => 7,
            Key.D8 or Key.NumPad8 => 8,
            Key.D9 or Key.NumPad9 => 9,
            _ => -1
        };
        return slot >= 0;
    }

    private static bool IsTypingTarget() =>
        Keyboard.FocusedElement is System.Windows.Controls.TextBox;

    private void MacrosClicked(object sender, RoutedEventArgs e)
    {
        if (_macroWindow is not null)
        {
            _macroWindow.Activate();
            return;
        }

        _macroWindow = new MacroWindow { Owner = this };
        _macroWindow.SlotSet += CapturePaintMacro;
        _macroWindow.SlotClear += ClearPaintMacro;
        _macroWindow.SlotApply += ApplyPaintMacro;
        _macroWindow.Closed += (_, _) => _macroWindow = null;
        RefreshMacros();
        _macroWindow.Show();
    }

    private void CapturePaintMacro(int key)
    {
        if ((uint)key >= 10)
        {
            return;
        }

        if (Layer == PaintLayer.Structure)
        {
            if (!TryGetStructure(CurrentBrush, out StructureStamp stamp))
            {
                StatusText.Text = "Select a structure preset first, then click Set.";
                return;
            }

            _macros[key] = new PaintMacroSlot
            {
                Key = key,
                Kind = "Structure",
                Id = CurrentBrush,
                StampKey = stamp.Key,
                StampCategory = stamp.Category.ToString(),
                Label = stamp.Label
            };
        }
        else
        {
            string kind = Layer switch
            {
                PaintLayer.LeftWall => "LeftWall",
                PaintLayer.RightWall => "RightWall",
                _ => "Floor"
            };
            string noun = Layer switch
            {
                PaintLayer.LeftWall => "L wall",
                PaintLayer.RightWall => "R wall",
                _ => "floor"
            };
            int id = CurrentBrush;
            _macros[key] = new PaintMacroSlot
            {
                Key = key,
                Kind = kind,
                Id = id,
                Label = id == 0 ? $"{noun} eraser" : $"{noun} {id:D5}"
            };
        }

        SavePaintMacros();
        RefreshMacros();
        StatusText.Text = $"Macro {key}: {_macros[key]!.Label}";
    }

    private void ClearPaintMacro(int key)
    {
        if ((uint)key >= 10 || _macros[key] is null)
        {
            return;
        }

        string label = _macros[key]!.Label;
        _macros[key] = null;
        SavePaintMacros();
        RefreshMacros();
        StatusText.Text = $"Cleared macro {key} ({label}).";
    }

    private void ApplyPaintMacro(int key)
    {
        if ((uint)key >= 10)
        {
            return;
        }

        PaintMacroSlot? slot = _macros[key];
        if (slot is null)
        {
            StatusText.Text = $"Macro {key} is empty. Open Macros and click Set.";
            return;
        }

        if (string.Equals(slot.Kind, "Structure", StringComparison.OrdinalIgnoreCase))
        {
            if (!TrySelectStructureMacro(slot))
            {
                StatusText.Text = $"Macro {key} preset is missing: {slot.Label}";
                return;
            }
        }
        else
        {
            PaintLayer layer = slot.Kind switch
            {
                "LeftWall" => PaintLayer.LeftWall,
                "RightWall" => PaintLayer.RightWall,
                _ => PaintLayer.Floor
            };
            SetPaintLayer(layer);
            SelectBrush(slot.Id, jumpPage: true);
        }

        _activeMacroKey = key;
        StatusText.Text = $"Brush from macro {key}: {slot.Label}";
        SyncMacroBarSelection();
        Activate();
    }

    private bool TrySelectStructureMacro(PaintMacroSlot slot)
    {
        EnsureStructures();
        StructureStamp stamp = default;
        bool found = false;
        if (!string.IsNullOrWhiteSpace(slot.StampKey))
        {
            foreach (StructureStamp candidate in _structures)
            {
                if (string.Equals(candidate.Key, slot.StampKey, StringComparison.Ordinal))
                {
                    stamp = candidate;
                    found = true;
                    break;
                }
            }
        }

        if (!found)
        {
            return false;
        }

        SetPaintLayer(PaintLayer.Structure);
        SelectStampGroup(stamp.Category);
        List<StructureStamp> visible = VisibleStructures();
        int index = visible.FindIndex(s => string.Equals(s.Key, stamp.Key, StringComparison.Ordinal));
        if (index < 0)
        {
            return false;
        }

        SelectBrush(index + 1, jumpPage: true);
        return true;
    }

    private void SetPaintLayer(PaintLayer layer)
    {
        System.Windows.Controls.RadioButton? box = layer switch
        {
            PaintLayer.LeftWall => LeftLayer,
            PaintLayer.RightWall => RightLayer,
            PaintLayer.Structure => StructureLayer,
            _ => FloorLayer
        };
        if (box is null)
        {
            return;
        }

        if (box.IsChecked == true)
        {
            LayerChanged(box, new RoutedEventArgs());
            return;
        }

        box.IsChecked = true;
    }

    private void LoadPaintMacros()
    {
        Array.Clear(_macros);
        List<PaintMacroSlot>? stored = _workspace?.ActiveClient?.PaintMacros;
        if (stored is null)
        {
            return;
        }

        foreach (PaintMacroSlot slot in stored)
        {
            if ((uint)slot.Key < 10)
            {
                _macros[slot.Key] = slot;
            }
        }
    }

    private void SavePaintMacros()
    {
        ClientProfile? client = _workspace?.ActiveClient;
        if (client is null)
        {
            return;
        }

        client.PaintMacros = _macros.OfType<PaintMacroSlot>().OrderBy(s => s.Key).ToList();
        _workspace!.Settings.Save();
    }

    private void InitMacroBar()
    {
        if (MacroBarHost is null)
        {
            return;
        }

        _macroBarSlots.Clear();
        for (int i = 0; i < 10; i++)
        {
            _macroBarSlots.Add(new MacroSlotView(i));
        }

        MacroBarHost.ItemsSource = _macroBarSlots;
    }

    private void MacroBarSlotClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: int key })
        {
            ApplyPaintMacro(key);
        }
    }

    private void RefreshMacros()
    {
        if ((MacroBar is null || MacroBar.Visibility != Visibility.Visible) && _macroWindow is null)
        {
            bool anyBound = _macros.Any(s => s is not null);
            if (MacroBar is not null)
            {
                MacroBar.Visibility = anyBound ? Visibility.Visible : Visibility.Collapsed;
            }

            if (!anyBound)
            {
                return;
            }
        }

        int highlight = HighlightMacroKey();
        bool any = false;
        var popup = _macroWindow is null ? null : new List<MacroSlotView>(10);
        for (int i = 0; i < 10; i++)
        {
            PaintMacroSlot? slot = _macros[i];
            BitmapSource? image = slot is null ? null : MacroPreview(slot);
            string label = slot?.Label ?? "empty";
            bool hasStamp = slot is not null;
            any |= hasStamp;
            if (_macroBarSlots.Count == 10)
            {
                MacroSlotView bar = _macroBarSlots[i];
                bar.Image = image;
                bar.Label = label;
                bar.HasStamp = hasStamp;
                bar.Selected = i == highlight;
                bar.Caption = slot is null ? i.ToString() : $"{i}  {MacroKindCaption(slot.Kind)}";
            }

            if (popup is not null)
            {
                var view = new MacroSlotView(i)
                {
                    Image = image,
                    Label = label,
                    HasStamp = hasStamp,
                    Selected = i == highlight,
                    Caption = slot is null ? i.ToString() : $"{i}  {MacroKindCaption(slot.Kind)}"
                };
                popup.Add(view);
            }
        }

        if (MacroBar is not null)
        {
            MacroBar.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }

        _macroWindow?.Bind(popup!);
    }

    private void SyncMacroBarSelection()
    {
        if (_macroBarSlots.Count != 10)
        {
            return;
        }

        int highlight = HighlightMacroKey();
        for (int i = 0; i < 10; i++)
        {
            _macroBarSlots[i].Selected = i == highlight;
        }

        if (_macroWindow is not null)
        {
            for (int i = 0; i < _macroWindow.Slots.Count; i++)
            {
                _macroWindow.Slots[i].Selected = i == highlight;
            }
        }
    }

    private int HighlightMacroKey()
    {
        if ((uint)_activeMacroKey < 10 && SlotMatchesBrush(_macros[_activeMacroKey]))
        {
            return _activeMacroKey;
        }

        _activeMacroKey = -1;

        for (int i = 0; i < 10; i++)
        {
            if (SlotMatchesBrush(_macros[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private bool SlotMatchesBrush(PaintMacroSlot? slot)
    {
        if (slot is null)
        {
            return false;
        }

        if (string.Equals(slot.Kind, "Structure", StringComparison.OrdinalIgnoreCase))
        {
            return Layer == PaintLayer.Structure
                && TryGetStructure(CurrentBrush, out StructureStamp stamp)
                && string.Equals(stamp.Key, slot.StampKey, StringComparison.Ordinal);
        }

        PaintLayer layer = slot.Kind switch
        {
            "LeftWall" => PaintLayer.LeftWall,
            "RightWall" => PaintLayer.RightWall,
            _ => PaintLayer.Floor
        };
        return Layer == layer && CurrentBrush == slot.Id;
    }

    private static string MacroKindCaption(string kind) => kind switch
    {
        "LeftWall" => "L wall",
        "RightWall" => "R wall",
        "Structure" => "preset",
        _ => "floor"
    };

    private BitmapSource? MacroPreview(PaintMacroSlot slot)
    {
        try
        {
            if (string.Equals(slot.Kind, "Structure", StringComparison.OrdinalIgnoreCase))
            {
                EnsureStructures();
                StructureStamp stamp = _structures.FirstOrDefault(s =>
                    string.Equals(s.Key, slot.StampKey, StringComparison.Ordinal));
                if (string.IsNullOrEmpty(stamp.Key))
                {
                    return null;
                }

                return WpfImages.ToBitmap(RenderStamp(stamp, maxEdge: 96, out _, out _));
            }

            PixelBuffer? img = string.Equals(slot.Kind, "LeftWall", StringComparison.OrdinalIgnoreCase)
                || string.Equals(slot.Kind, "RightWall", StringComparison.OrdinalIgnoreCase)
                ? _assets.Wall(slot.Id)
                : _assets.Floor(slot.Id, SnowBox?.IsChecked == true, FloorAtlasOffset());
            return img is null || slot.Id == 0 ? null : WpfImages.ToBitmap(img);
        }
        catch
        {
            return null;
        }
    }

    private void NewClicked(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard())
        {
            return;
        }

        var dialog = new NewMapWindow { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string name = dialog.FileName;
        if (!name.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
        {
            name += ".map";
        }

        string folder = ExistingMapsFolder() ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string path = Path.Combine(folder, name);
        _map = new MapFile(dialog.MapWidth, dialog.MapHeight, path);
        if (dialog.FillFloor != 0)
        {
            for (int i = 0; i < _map.Tiles.Length; i++)
            {
                _map.Tiles[i] = new MapTile(dialog.FillFloor, 0, 0);
            }
        }

        ResetHistory(dirty: true);
        ResetOverlays();
        RefreshSizeBox();
        Redraw();
        Title = TitleText();
        StatusText.Text = $"New {dialog.MapWidth}×{dialog.MapHeight} map. Paint, then Save.";
    }

    private void RandomizeClicked(object sender, RoutedEventArgs e)
    {
        int.TryParse(WidthBox?.Text.Trim(), out int width);
        int.TryParse(HeightBox?.Text.Trim(), out int height);
        if (width < 4)
        {
            width = _map.Width;
        }

        if (height < 4)
        {
            height = _map.Height;
        }

        int floor = _floorBrush > 0 ? _floorBrush : 278;
        int wall = _leftBrush > 2 ? _leftBrush : (_rightBrush > 2 ? _rightBrush : 4209);
        var dialog = new RandomizeMapWindow(_assets, MapSearchFolders(), width, height, floor, wall) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            Busy.Run(this, "Randomizing map…", () =>
            {
                MapSnapshot before = CaptureSnapshot();
                _generatorOptions = dialog.Options;
                MapTile[] tiles = MapGenerator.Generate(dialog.Options, _assets, dialog.Stats);
                _map.LoadState(dialog.Options.Width, dialog.Options.Height, tiles, _map.SizeHeader);
                CommitSnapshot(before);
                MarkEdited();
                RefreshSizeBox();
                Redraw();
                Title = TitleText();
                StatusText.Text =
                    $"Randomized {dialog.Options.Width}×{dialog.Options.Height}  floor {dialog.Options.Floor}  wall {dialog.Options.WallSeed}  buildings {dialog.Options.BuildingCount}.";
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Randomize failed");
        }
    }

    private void RerollWallsClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Busy.Run(this, "Rerolling walls…", () =>
            {
                MapCatalogStats stats = MapCatalogStats.Load(MapSearchFolders());
                int wall = _generatorOptions.WallSeed > 2
                    ? _generatorOptions.WallSeed
                    : _leftBrush > 2 ? _leftBrush : _rightBrush > 2 ? _rightBrush : 0;
                MapSnapshot before = CaptureSnapshot();
                MapTile[] tiles = MapGenerator.RerollWalls(_map, _assets, stats, wall, _generatorOptions.BuildingCount);
                _map.LoadState(_map.Width, _map.Height, tiles, _map.SizeHeader);
                CommitSnapshot(before);
                MarkEdited();
                Redraw();
                Title = TitleText();
                StatusText.Text = "Rerolled walls using harvested houses and buildings. Ctrl+Z undoes.";
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Reroll walls failed");
        }
    }

    private void RerollFloorsClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Busy.Run(this, "Rerolling floors…", () =>
            {
                MapCatalogStats stats = MapCatalogStats.Load(MapSearchFolders());
                MapSnapshot before = CaptureSnapshot();
                MapTile[] tiles = MapGenerator.RerollFloors(_map, _assets, stats, _generatorOptions);
                _map.LoadState(_map.Width, _map.Height, tiles, _map.SizeHeader);
                CommitSnapshot(before);
                MarkEdited();
                Redraw();
                Title = TitleText();
                StatusText.Text =
                    $"Rerolled floors to {_generatorOptions.Floor} with {_generatorOptions.PathCount} paths (walls kept). Set the style in Randomize…. Ctrl+Z undoes.";
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Reroll floors failed");
        }
    }

    private void RerollTreesClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Busy.Show(this, "Rerolling trees…");
            MapCatalogStats stats = MapCatalogStats.Load(MapSearchFolders());
            (MapTile[] tiles, int placed) = MapGenerator.RerollFoliage(_map, _assets, stats, _generatorOptions.PathFloor);
            if (placed == 0)
            {
                StatusText.Text = "No tree or foliage stamps were available, so the map was left as it is.";
                return;
            }

            MapSnapshot before = CaptureSnapshot();
            _map.LoadState(_map.Width, _map.Height, tiles, _map.SizeHeader);
            CommitSnapshot(before);
            MarkEdited();
            Redraw();
            Title = TitleText();
            StatusText.Text = $"Placed {placed} trees and foliage on open ground. Buildings and paths were left alone. Ctrl+Z undoes.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Reroll trees failed");
        }
        finally
        {
            Busy.Hide(this);
        }
    }

    private void OpenClicked(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard())
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Dark Ages maps|*.map|All files|*.*",
            InitialDirectory = ExistingMapsFolder()
        };
        if (dialog.ShowDialog(this) == true)
        {
            OpenPath(dialog.FileName);
        }
    }

    private void MapListOpened(object sender, MouseButtonEventArgs e)
    {
        if (MapList.SelectedItem is MapListItem item && ConfirmDiscard())
        {
            OpenItem(item);
        }
    }

    private void OpenItem(MapListItem item)
    {
        if (item.Archive is not null)
        {
            OpenPacked(item.Archive, item.EntryName);
            return;
        }

        OpenPath(item.Path);
    }

    private void OpenPath(string path)
    {
        try
        {
            RememberVisit();
            Busy.Show(this, "Opening map…");
            _map = MapFile.Load(path);
            _openedFromArchive = false;
            _packedSourcePath = null;
            _packedEntryName = null;
            ResetHistory(dirty: false);
            LoadOverlays();
            RefreshSizeBox();
            Redraw();
            Title = TitleText();
            StatusText.Text = $"Opened {path}  ({_map.Width}×{_map.Height}).{WarpStatus()} Paint and Save to overwrite.";
            HighlightMapList(path);
            NoteOpenedMap();
            ApplyPendingLanding();
        }
        catch (Exception ex)
        {
            _pendingLanding = null;
            System.Windows.MessageBox.Show(this, ex.Message, "Open map failed");
        }
        finally
        {
            Busy.Hide(this);
        }
    }

    private void OpenPacked(IArchive archive, string name)
    {
        try
        {
            RememberVisit();
            Busy.Show(this, "Opening map…");
            ArchiveEntry? entry = archive.GetEntry(name);
            if (entry is null)
            {
                throw new FileNotFoundException($"Map {name} was not in {archive.DisplayName}.");
            }

            _map = MapFile.FromBytes(entry.ReadAllBytes());
            _map.Name = name;
            _openedFromArchive = true;
            _packedSourcePath = archive.SourcePath;
            _packedEntryName = name;
            ResetHistory(dirty: false);
            LoadOverlays();
            RefreshSizeBox();
            Redraw();
            Title = TitleText();
            StatusText.Text =
                $"Opened {name} from {archive.DisplayName}  ({_map.Width}×{_map.Height}).{WarpStatus()} Save writes this map back into the DAT.";
            HighlightMapList(null, archive, name);
            NoteOpenedMap();
            ApplyPendingLanding();
        }
        catch (Exception ex)
        {
            _pendingLanding = null;
            System.Windows.MessageBox.Show(this, ex.Message, "Open map failed");
        }
        finally
        {
            Busy.Hide(this);
        }
    }

    private void HighlightMapList(string? path, IArchive? packed = null, string? packedName = null)
    {
        if (MapList?.ItemsSource is not IEnumerable<MapListItem> items)
        {
            return;
        }

        MapListItem? match = null;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            string full = Path.GetFullPath(path);
            match = items.FirstOrDefault(i =>
                i.Archive is null
                && !string.IsNullOrWhiteSpace(i.Path)
                && string.Equals(Path.GetFullPath(i.Path), full, StringComparison.OrdinalIgnoreCase));
        }
        else if (packed is not null && !string.IsNullOrWhiteSpace(packedName))
        {
            match = items.FirstOrDefault(i =>
                i.Archive is not null
                && string.Equals(i.Archive.SourcePath, packed.SourcePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(i.EntryName, packedName, StringComparison.OrdinalIgnoreCase));
        }

        if (match is null)
        {
            return;
        }

        MapList.SelectedItem = match;
        MapList.ScrollIntoView(match);
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (_openedFromArchive
            && !string.IsNullOrWhiteSpace(_packedSourcePath)
            && !string.IsNullOrWhiteSpace(_packedEntryName)
            && DatSave.IsWritable(_packedSourcePath))
        {
            try
            {
                byte[] data = _map.ToBytes();
                DatSave.Write(_cache, _packedSourcePath, [(_packedEntryName, data)]);
                _savedSerial = _editSerial;
                _dirty = false;
                Title = TitleText();
                LogMapSave(_packedEntryName, data);
                StatusText.Text = SaveOverlaysIfDirty(
                    $"Saved {_packedEntryName} into {Path.GetFileName(_packedSourcePath)} (no unpack).");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(this, ex.Message, "Save to DAT");
            }

            return;
        }

        if (!CanOverwriteMap())
        {
            SaveAsClicked(sender, e);
            return;
        }

        SaveTo(_map.Name);
    }

    private void SaveAsClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Dark Ages maps|*.map|All files|*.*",
            InitialDirectory = ExistingMapsFolder(),
            FileName = string.IsNullOrWhiteSpace(_map.Name) ? "new.map" : Path.GetFileName(_map.Name)
        };
        if (dialog.ShowDialog(this) == true)
        {
            SaveTo(dialog.FileName);
        }
    }

    private void OpenButtonMenu(object sender, RoutedEventArgs e) => ButtonMenu.Open(sender);

    private void ExportMapImageClicked(object sender, RoutedEventArgs e) =>
        ExportPng("PNG image|*.png|All files|*.*", "-map.png", tabIso: false, square: false);

    private void ExportTabIsoClicked(object sender, RoutedEventArgs e) =>
        ExportPng("PNG image|*.png|All files|*.*", "-tab.png", tabIso: true, square: false);

    private void ExportTabSquareClicked(object sender, RoutedEventArgs e) =>
        ExportPng("PNG image|*.png|All files|*.*", "-tab-square.png", tabIso: false, square: true);

    private void ExportDataClicked(object sender, RoutedEventArgs e)
    {
        string stem = string.IsNullOrWhiteSpace(_map.Name)
            ? "map"
            : Path.GetFileNameWithoutExtension(_map.Name);
        TableDump dump = DataDumps.MapTiles(_map, CurrentMapId(), stem);
        string? jsonOverride = null;
#if LOCAL_UNITY
        jsonOverride = UnityJson.Map(_map, CurrentMapId(), _map.Name);
#endif
        TableExport.Save(this, stem + "-tiles", dump, sender, StatusText, jsonOverride);
    }

    private void ExportPng(string filter, string suffix, bool tabIso, bool square)
    {
        string stem = string.IsNullOrWhiteSpace(_map.Name) ? "map" : Path.GetFileNameWithoutExtension(_map.Name);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = filter,
            InitialDirectory = ExistingMapsFolder(),
            FileName = stem + suffix
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            Busy.Show(this, "Exporting map…");
            bool snow = SnowBox?.IsChecked == true;
            int offset = FloorAtlasOffset();
            PixelBuffer buffer = square
                ? _assets.RenderTabSquare(_map)
                : _assets.Render(_map, snow, floorOffset: offset, tabMap: tabIso, warps: VisibleWarps(), arrivals: VisibleArrivals());
            PngWriter.Write(buffer, dialog.FileName);
            StatusText.Text = $"Exported {dialog.FileName} ({buffer.Width}×{buffer.Height})";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Export failed");
        }
        finally
        {
            Busy.Hide(this);
        }
    }

    private bool CanOverwriteMap() =>
        !_openedFromArchive
        && !string.IsNullOrWhiteSpace(_map.Name)
        && Path.IsPathRooted(_map.Name);

    private void SaveTo(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            byte[] data = _map.ToBytes();
            byte[]? prior = File.Exists(path) ? File.ReadAllBytes(path) : null;
            ClientFamily family = ClientFamilies.FromPath(path);
            if (family == ClientFamily.Unknown)
            {
                family = ClientFamilies.FromPath(_workspace?.ActiveClient?.Path ?? _assets.Seo.SourcePath);
            }

            DatCompatibility.ThrowIfUnsafe(
                family,
                nameLength: null,
                _ => prior,
                [(Path.GetFileName(path), data)]);
            File.WriteAllBytes(path, data);
            _map.Name = path;
            _openedFromArchive = false;
            _savedSerial = _editSerial;
            _dirty = false;
            Title = TitleText();
            RefreshMapList();
                LogMapSave(path, _map.ToBytes());
            StatusText.Text = SaveOverlaysIfDirty($"Saved {path}");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Save failed");
        }
    }

    private void ResizeClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new ResizeMapWindow(_map.Width, _map.Height, Math.Max(0, _floorBrush)) { Owner = this };
        if (dialog.ShowDialog() != true || !dialog.HasChange)
        {
            return;
        }

        CommitSnapshot(CaptureSnapshot());
        _map.Pad(
            dialog.LeftPad,
            dialog.UpPad,
            dialog.RightPad,
            dialog.DownPad,
            new MapTile(dialog.FillFloor, 0, 0));
        _warps.FitAfterPad(CurrentMapId(), dialog.LeftPad, dialog.UpPad, _map.Width, _map.Height);
        _lightOverlay.Pad(dialog.LeftPad, dialog.UpPad, dialog.RightPad, dialog.DownPad);
        _effectOverlay.Pad(dialog.LeftPad, dialog.UpPad, dialog.RightPad, dialog.DownPad);
        _selection.Clear();
        MarkEdited();
        RefreshSizeBox();
        RefreshAreaInfo();
        Redraw();
        Title = TitleText();
        StatusText.Text =
            $"Resized to {_map.Width}×{_map.Height}  (left {dialog.LeftPad}, right {dialog.RightPad}, up {dialog.UpPad}, down {dialog.DownPad}).";
    }

    private void MapSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSize || SizeBox.SelectedItem is not SizeChoice choice)
        {
            return;
        }

        try
        {
            if (choice.Width == _map.Width && choice.Height == _map.Height)
            {
                return;
            }

            CommitSnapshot(CaptureSnapshot());
            _map.SetSize(choice.Width, choice.Height);
            _lightOverlay.Ensure(_map.Width, _map.Height);
            _effectOverlay.Ensure(_map.Width, _map.Height);
            MarkEdited();
            WidthBox.Text = choice.Width.ToString();
            HeightBox.Text = choice.Height.ToString();
            Redraw();
            Title = TitleText();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Size");
        }
    }

    private void LayerChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        RememberPalettePlace();
        _paletteLayer = Layer;
        RestorePalettePlace();
        if (StampGroupBar is not null)
        {
            StampGroupBar.Visibility = Layer == PaintLayer.Structure ? Visibility.Visible : Visibility.Collapsed;
        }

        if (TileIdBox is not null)
        {
            TileIdBox.Text = CurrentBrush.ToString();
        }

        BindPaletteCategories();
        LoadPalettePage();
        UpdateBrushPreview();
        SyncMacroBarSelection();
    }

    private void StampGroupChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingGroups || !IsLoaded)
        {
            return;
        }

        StampCategory next = sender == GroupPairs ? StampCategory.Pair
            : sender == GroupTrees ? StampCategory.Tree
            : sender == GroupPaths ? StampCategory.Path
            : sender == GroupOther ? StampCategory.Other
            : StampCategory.Building;
        if (next == _stampGroup)
        {
            return;
        }

        RememberGroupPlace();
        _stampGroup = next;
        RestoreGroupPlace();
        if (TileIdBox is not null)
        {
            TileIdBox.Text = CurrentBrush.ToString();
        }

        LoadPalettePage();
        UpdateBrushPreview();
        SyncMacroBarSelection();
    }

    private void RememberPalettePlace()
    {
        if (_paletteLayer == PaintLayer.Structure)
        {
            RememberGroupPlace();
        }
        else
        {
            _layerPage[_paletteLayer] = _page;
        }
    }

    private void RestorePalettePlace()
    {
        if (Layer == PaintLayer.Structure)
        {
            RestoreGroupPlace();
            return;
        }

        _page = _layerPage.GetValueOrDefault(Layer);
    }

    private void RememberGroupPlace()
    {
        _groupPage[_stampGroup] = _page;
        _groupBrush[_stampGroup] = _structureBrush;
    }

    private void RestoreGroupPlace()
    {
        _page = _groupPage.GetValueOrDefault(_stampGroup);
        int count = VisibleStructures().Count;
        int saved = _groupBrush.GetValueOrDefault(_stampGroup, count > 0 ? 1 : 0);
        _structureBrush = count == 0 ? 0 : Math.Clamp(saved, 1, count);
    }

    private void SelectStampGroup(StampCategory category)
    {
        if (_stampGroup != category)
        {
            RememberGroupPlace();
            _stampGroup = category;
        }

        _syncingGroups = true;
        if (GroupBuildings is not null)
        {
            GroupBuildings.IsChecked = category == StampCategory.Building;
        }

        if (GroupPairs is not null)
        {
            GroupPairs.IsChecked = category == StampCategory.Pair;
        }

        if (GroupTrees is not null)
        {
            GroupTrees.IsChecked = category == StampCategory.Tree;
        }

        if (GroupPaths is not null)
        {
            GroupPaths.IsChecked = category == StampCategory.Path;
        }

        if (GroupOther is not null)
        {
            GroupOther.IsChecked = category == StampCategory.Other;
        }

        _syncingGroups = false;
        RestoreGroupPlace();
        if (StampGroupBar is not null)
        {
            StampGroupBar.Visibility = Visibility.Visible;
        }
    }

    private void MapFilterChanged(object sender, TextChangedEventArgs e) => RefreshMapList();

    private void GoTileClicked(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TileIdBox.Text.Trim(), out int id))
        {
            return;
        }

        SelectBrush(id, jumpPage: true);
    }

    private void PrevPageClicked(object sender, RoutedEventArgs e)
    {
        _page = Math.Max(0, _page - 1);
        LoadPalettePage();
    }

    private void NextPageClicked(object sender, RoutedEventArgs e)
    {
        _page++;
        LoadPalettePage();
    }

    private void EraserClicked(object sender, RoutedEventArgs e) => SelectBrush(0, jumpPage: false);

    private void UndoClicked(object sender, RoutedEventArgs e)
    {
        if (_painting)
        {
            StopPaint();
        }

        ApplyHistory(undo: true);
    }

    private void RedoClicked(object sender, RoutedEventArgs e)
    {
        if (_painting)
        {
            StopPaint();
        }

        ApplyHistory(undo: false);
    }

    private void ApplyHistory(bool undo)
    {
        MapSnapshot current = CaptureSnapshot();
        bool ok = undo
            ? _history.TryUndo(_map, current, out MapSnapshot applied)
            : _history.TryRedo(_map, current, out applied);
        if (!ok)
        {
            return;
        }

        RestoreOverlays(applied);
        _editSerial += undo ? -1 : 1;
        _dirty = _editSerial != _savedSerial;
        RefreshSizeBox();
        Redraw();
        Title = TitleText();
        UpdateHistoryButtons();
        StatusText.Text = $"{(undo ? "Undo" : "Redo")}  ({_history.UndoCount} undo, {_history.RedoCount} redo)";
    }

    private void CommitSnapshot(MapSnapshot before)
    {
        _history.Commit(before);
        UpdateHistoryButtons();
    }

    private MapSnapshot CaptureSnapshot()
    {
        _lightOverlay.Ensure(_map.Width, _map.Height);
        _effectOverlay.Ensure(_map.Width, _map.Height);
        return new MapSnapshot(
            _map.Width,
            _map.Height,
            _map.SizeHeader,
            (MapTile[])_map.Tiles.Clone(),
            _map.Name,
            _lightOverlay.CloneBytes(),
            _effectOverlay.CloneBytes());
    }

    private void RestoreOverlays(MapSnapshot snap)
    {
        _lightOverlay.Restore(snap.Light, snap.Width, snap.Height);
        _effectOverlay.Restore(snap.Effect, snap.Width, snap.Height);
    }

    private void MarkEdited()
    {
        _editSerial++;
        _dirty = true;
        Title = TitleText();
        UpdateHistoryButtons();
    }

    private void ResetHistory(bool dirty)
    {
        _history.Clear();
        _strokeStart = null;
        _strokeChanged = false;
        _editSerial = dirty ? 1 : 0;
        _savedSerial = 0;
        _dirty = dirty;
        UpdateHistoryButtons();
    }

    private void UpdateHistoryButtons()
    {
        if (UndoButton is not null)
        {
            UndoButton.IsEnabled = _history.CanUndo;
        }

        if (RedoButton is not null)
        {
            RedoButton.IsEnabled = _history.CanRedo;
        }
    }

    private void PaletteTileClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TileChoice choice })
        {
            SelectBrush(choice.Id, jumpPage: false);
            e.Handled = true;
        }
    }

    private bool WantSelect =>
        SelectModeBox?.IsChecked == true || Keyboard.Modifiers == ModifierKeys.Shift;

    private void MapMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_panning)
        {
            return;
        }

        System.Windows.Point pos = e.GetPosition(MapImage);
        if (MapAssets.TryHit((int)pos.X, (int)pos.Y, _map, _mapView, out int hx, out int hy))
        {
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
            MapWarp? hit = _warps.At(_map.Name, hx, hy)
                ?? (WantServerOverlay ? _serverWorld.At(_map.Name, hx, hy) : null);
            if (WantWarpEdit)
            {
                StartWarpPick(pos);
                return;
            }

            bool showHit = hit is not null && (hit.Value.Origin == WarpOrigin.Server
                ? WantServerOverlay
                : ctrl || WarpsBox?.IsChecked == true);
            if (showHit && !shift && TryTravel(hx, hy, beside: alt))
            {
                e.Handled = true;
                return;
            }
        }

        if (WantSelect)
        {
            StartSelect(pos);
            return;
        }

        if (WantOverlay && _pendingOverlayStamp is not null
            && MapAssets.TryHit((int)pos.X, (int)pos.Y, _map, _mapView, out int stampX, out int stampY))
        {
            StampOverlayAt(stampX, stampY);
            e.Handled = true;
            return;
        }

        _painting = true;
        _strokeChanged = false;
        _strokeStart = CaptureSnapshot();
        _lastPaintX = int.MinValue;
        MapImage.CaptureMouse();
        PaintAt(pos);
    }

    private void MapMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_warpPick.Dragging)
        {
            StopWarpPick();
            return;
        }

        if (_selection.Dragging)
        {
            StopSelect();
            return;
        }

        StopPaint();
    }

    private void MapLostCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_warpPick.Dragging)
        {
            StopWarpPick();
            return;
        }

        if (_selection.Dragging)
        {
            StopSelect();
            return;
        }

        StopPaint();
    }

    private void MapMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_panning)
        {
            WarpHoverTip.Hide(WarpHoverPopup);
            return;
        }

        System.Windows.Point p = e.GetPosition(MapImage);
        if (_warpPick.Dragging)
        {
            if (MapAssets.TryHit((int)p.X, (int)p.Y, _map, _mapView, out int wx, out int wy))
            {
                _warpPick.Drag(wx, wy);
                UpdateSelectionVisual();
            }

            return;
        }

        if (_selection.Dragging)
        {
            if (MapAssets.TryHit((int)p.X, (int)p.Y, _map, _mapView, out int sx, out int sy))
            {
                _selection.Drag(sx, sy);
                UpdateSelectionVisual();
            }

            return;
        }

        if (MapAssets.TryHit((int)p.X, (int)p.Y, _map, _mapView, out int x, out int y))
        {
            if (x != _lastStatusX || y != _lastStatusY)
            {
                _lastStatusX = x;
                _lastStatusY = y;
                MapTile tile = _map[x, y];
                string extra = _selection.HasSelection ? $"  selection {_selection.Width}×{_selection.Height}" : "";
                if (_painting)
                {
                    StatusText.Text = $"({x},{y})  brush {Layer} #{CurrentBrush}{extra}";
                }
                else
                {
                    string warpNote = WarpHover(x, y);
                    StatusText.Text =
                        $"({x},{y}) {_assets.Label}  floor={tile.Floor} TILEA[{Math.Max(0, tile.Floor - 1 + FloorAtlasOffset())}] pal={PaletteLocator.FloorPaletteNumber(_assets.Seo, tile.Floor + FloorAtlasOffset())}"
                        + $"  L={tile.LeftWall} stc{tile.LeftWall:D5} pal={PaletteLocator.WallPaletteNumber(_assets.Ia, tile.LeftWall)}"
                        + $"  R={tile.RightWall} stc{tile.RightWall:D5} pal={PaletteLocator.WallPaletteNumber(_assets.Ia, tile.RightWall)}"
                        + $"   brush {Layer} #{CurrentBrush}{extra}{warpNote}";
                    ShowWarpHover(x, y, e);
                }
            }

            if (!WantSelect && !WantOverlay)
            {
                UpdateGhost(x, y);
            }
            else
            {
                HideGhost();
            }
        }
        else
        {
            HideGhost();
            WarpHoverTip.Hide(WarpHoverPopup);
        }

        if (_painting && e.LeftButton == MouseButtonState.Pressed && (WantOverlay || Layer != PaintLayer.Structure))
        {
            PaintAt(p);
        }
    }

    private void MapMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        WarpHoverTip.Hide(WarpHoverPopup);
        if (!_painting)
        {
            HideGhost();
        }
    }

    private void MapMiddleDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        MapEyedrop(e.GetPosition(MapImage));
        e.Handled = true;
    }

    private void MapPanDown(object sender, MouseButtonEventArgs e)
    {
        if (MapScroller is null || e.ChangedButton != MouseButton.Right)
        {
            return;
        }

        if (e.OriginalSource is System.Windows.Controls.Primitives.Thumb
            or System.Windows.Controls.Primitives.RepeatButton
            or System.Windows.Controls.Primitives.Track)
        {
            return;
        }

        if (_painting)
        {
            StopPaint();
        }

        if (_selection.Dragging)
        {
            StopSelect();
        }

        _panning = true;
        _panStart = e.GetPosition(MapScroller);
        _panOffsetX = MapScroller.HorizontalOffset;
        _panOffsetY = MapScroller.VerticalOffset;
        MapScroller.CaptureMouse();
        MapScroller.Cursor = System.Windows.Input.Cursors.SizeAll;
        MapImage.Cursor = System.Windows.Input.Cursors.SizeAll;
        HideGhost();
        e.Handled = true;
    }

    private void MapPanMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_panning || MapScroller is null || e.RightButton != MouseButtonState.Pressed)
        {
            return;
        }

        System.Windows.Point now = e.GetPosition(MapScroller);
        MapScroller.ScrollToHorizontalOffset(_panOffsetX - (now.X - _panStart.X));
        MapScroller.ScrollToVerticalOffset(_panOffsetY - (now.Y - _panStart.Y));
        e.Handled = true;
    }

    private void MapPanUp(object sender, MouseButtonEventArgs e)
    {
        if (_panning)
        {
            e.Handled = true;
        }

        StopPan();
    }

    private void MapPanLost(object sender, System.Windows.Input.MouseEventArgs e) => StopPan();

    private void MapWheelZoom(object sender, MouseWheelEventArgs e)
    {
        if (_panning)
        {
            return;
        }

        System.Windows.Point mouse = e.GetPosition(MapScroller);
        System.Windows.Point content = e.GetPosition(MapHost);
        SetMapZoom(ViewZoom.Step(_mapZoom, e.Delta), mouse, content);
        e.Handled = true;
    }

    private void ZoomInClicked(object sender, RoutedEventArgs e) => NudgeMapZoom(1);

    private void ZoomOutClicked(object sender, RoutedEventArgs e) => NudgeMapZoom(-1);

    private void ZoomResetClicked(object sender, RoutedEventArgs e) => SetMapZoom(1);

    private void NudgeMapZoom(int direction) =>
        SetMapZoom(ViewZoom.Step(_mapZoom, direction > 0 ? 120 : -120));

    private void SetMapZoom(double zoom, System.Windows.Point? mouse = null, System.Windows.Point? content = null)
    {
        zoom = ViewZoom.Clamp(zoom);
        if (Math.Abs(zoom - _mapZoom) < 0.001)
        {
            return;
        }

        _mapZoom = zoom;
        ViewZoom.Apply(MapScale, _mapZoom);
        if (mouse is { } view && content is { } local)
        {
            ViewZoom.ScrollToward(MapScroller, view, local, _mapZoom);
        }

        if (ZoomLabel is not null)
        {
            ZoomLabel.Text = ViewZoom.Label(_mapZoom);
        }
    }

    private void StopPan()
    {
        if (!_panning)
        {
            return;
        }

        _panning = false;
        if (MapScroller is not null)
        {
            if (MapScroller.IsMouseCaptured)
            {
                MapScroller.ReleaseMouseCapture();
            }

            MapScroller.Cursor = System.Windows.Input.Cursors.Arrow;
        }

        if (MapImage is not null)
        {
            MapImage.Cursor = System.Windows.Input.Cursors.Pen;
        }
    }

    private void MapEyedrop(System.Windows.Point p)
    {
        if (!MapAssets.TryHit((int)p.X, (int)p.Y, _map, _mapView, out int x, out int y))
        {
            return;
        }

        MapTile tile = _map[x, y];
        if (WantOverlay)
        {
            var (b, g, r, a) = ActiveOverlay.Pixels.GetPixel(x, y);
            int lum = a == 0 ? 0 : Math.Clamp((r + g + b) / 3, 0, 255);
            if (OverlayStrengthBox is not null)
            {
                OverlayStrengthBox.Value = Math.Max(1, lum);
            }

            OverlayEraseBox?.SetCurrentValue(System.Windows.Controls.CheckBox.IsCheckedProperty, lum == 0);
            StatusText.Text = $"Eyedrop overlay ({x},{y}) strength {lum}";
            return;
        }

        if (Layer == PaintLayer.Structure)
        {
            EyedropStructure(x, y);
            return;
        }

        int id = Layer switch
        {
            PaintLayer.LeftWall => tile.LeftWall,
            PaintLayer.RightWall => tile.RightWall,
            _ => tile.Floor
        };
        SelectBrush(id, jumpPage: true);
    }

    private void PaintAt(System.Windows.Point p)
    {
        if (!MapAssets.TryHit((int)p.X, (int)p.Y, _map, _mapView, out int x, out int y))
        {
            return;
        }

        if (x == _lastPaintX && y == _lastPaintY)
        {
            return;
        }

        _lastPaintX = x;
        _lastPaintY = y;
        if (WantOverlay)
        {
            PaintOverlayAt(x, y);
            return;
        }

        if (Layer == PaintLayer.Structure)
        {
            if (CurrentBrush <= 0)
            {
                if (EraseStructureAt(x, y))
                {
                    _strokeChanged = true;
                    _dirty = true;
                    QueueRedraw();
                }

                return;
            }

            if (TryGetStructure(CurrentBrush, out StructureStamp stamp) && StampStructure(x, y, stamp))
            {
                _strokeChanged = true;
                _dirty = true;
                QueueRedraw();
            }

            return;
        }

        ushort id = (ushort)Math.Max(0, CurrentBrush);
        MapTile tile = _map[x, y];
        MapTile next = Layer switch
        {
            PaintLayer.LeftWall => new MapTile(tile.Floor, id, tile.RightWall),
            PaintLayer.RightWall => new MapTile(tile.Floor, tile.LeftWall, id),
            _ => new MapTile(id, tile.LeftWall, tile.RightWall)
        };
        if (next.Floor == tile.Floor && next.LeftWall == tile.LeftWall && next.RightWall == tile.RightWall)
        {
            return;
        }

        _map[x, y] = next;
        _dirtyTiles.Add((x, y));
        _strokeChanged = true;
        _dirty = true;
        QueueRedraw();
    }

    private void StopPaint()
    {
        if (!_painting)
        {
            return;
        }

        _painting = false;
        _lastPaintX = int.MinValue;
        if (MapImage.IsMouseCaptured)
        {
            MapImage.ReleaseMouseCapture();
        }

        if (_strokeChanged && _strokeStart is { } start)
        {
            CommitSnapshot(start);
            _editSerial++;
            _dirty = true;
            UpdateHistoryButtons();
            Title = TitleText();
        }

        _strokeStart = null;
        _strokeChanged = false;
        Title = TitleText();
        Redraw();
    }

    private void QueueRedraw()
    {
        if (_redrawQueued)
        {
            return;
        }

        _redrawQueued = true;
        _paintRedraw.Start();
    }

    private void RedrawClicked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            LoadPalettePage();
            Redraw();
            RefreshMacros();
        }
    }

    private IReadOnlyList<MapWarp> VisibleWarps() =>
        WarpsBox?.IsChecked == true || WantWarpEdit ? _warps.For(_map.Name) : [];

    private IReadOnlyList<MapWarp> VisibleArrivals() =>
        WarpsBox?.IsChecked == true || WantWarpEdit ? _warps.Arrivals(_map.Name) : [];

    private bool WantServerOverlay =>
        ServerBox?.IsChecked == true || ServerOverlayBox?.IsChecked == true;

    private IReadOnlyList<MapWarp> VisibleServerWarps() =>
        WantServerOverlay ? _serverWorld.Warps(_map.Name) : [];

    private IReadOnlyList<MapWarp> VisibleServerArrivals() =>
        WantServerOverlay ? _serverWorld.Arrivals(_map.Name) : [];

    private bool WantWarpEdit => WarpEditBox?.IsChecked == true;

    private bool WantOverlay => OverlayModeBox?.IsChecked == true;

    private bool OverlayIsEffect => OverlayEffectBox?.IsChecked == true;

    private MapOverlayLayer ActiveOverlay => OverlayIsEffect ? _effectOverlay : _lightOverlay;

    private bool OverlayDirty => _lightOverlay.Dirty || _effectOverlay.Dirty;

    private bool OverlayPersists => OverlaySaveBlockReason() is null;

    private bool UnsavedWork => _dirty || (OverlayDirty && OverlayPersists);

    private void ShowWarpHover(int x, int y, System.Windows.Input.MouseEventArgs e)
    {
        if (_painting || _panning)
        {
            WarpHoverTip.Hide(WarpHoverPopup);
            return;
        }

        MapWarp? warp = _warps.At(_map.Name, x, y)
            ?? (WantServerOverlay ? _serverWorld.At(_map.Name, x, y) : null);
        if (warp is null)
        {
            WarpHoverTip.Hide(WarpHoverPopup);
            return;
        }

        string tip = WarpHoverTip.ToLine(warp.Value, _mapNames, _warps, CurrentMapId());
        UIElement host = MapScroller;
        WarpHoverTip.Show(WarpHoverPopup, WarpHoverText, host, e.GetPosition(host), tip);
    }

    private string WarpHover(int x, int y)
    {
        MapWarp? warp = _warps.At(_map.Name, x, y)
            ?? (WantServerOverlay ? _serverWorld.At(_map.Name, x, y) : null);
        if (warp is not null)
        {
            string dest = string.IsNullOrWhiteSpace(warp.Value.Destination)
                ? (warp.Value.DestMapId > 0 ? $"map {warp.Value.DestMapId}" : "exit")
                : warp.Value.Destination;
            string land = warp.Value.HasLanding ? $" @ {warp.Value.DestX},{warp.Value.DestY}" : "";
            string kind = warp.Value.Origin == WarpOrigin.Server ? "server " : "";
            return $"  {kind}exit → {dest}{land}  (click to go, Alt+click for a second window, Shift+click to paint)";
        }

        MapWarp? arrival = _warps.ArrivalAt(_map.Name, x, y)
            ?? (WantServerOverlay ? _serverWorld.ArrivalAt(_map.Name, x, y) : null);
        if (arrival is null)
        {
            return "";
        }

        string from = arrival.Value.MapId > 0 ? $"map {arrival.Value.MapId}" : "another map";
        return $"  landing from {from} ({arrival.Value.X1},{arrival.Value.Y1})";
    }

    private void EditorModeChanged(object sender, RoutedEventArgs e)
    {
        if (MapImage is null)
        {
            return;
        }

        SyncOverlayTools();
        if (WantWarpEdit)
        {
            if (WarpsBox is not null)
            {
                WarpsBox.IsChecked = true;
            }

            MapImage.Cursor = System.Windows.Input.Cursors.Cross;
            Redraw();
            return;
        }

        if (SelectModeBox?.IsChecked == true)
        {
            HideGhost();
            MapImage.Cursor = System.Windows.Input.Cursors.Cross;
            return;
        }

        if (WantOverlay)
        {
            HideGhost();
            MapImage.Cursor = System.Windows.Input.Cursors.Pen;
            StatusText.Text = _pendingOverlayStamp is null
                ? "Paint light or color. Apply image or an effect to cover the whole map, or stamp an effect on click."
                : "Click the map to stamp the chosen effect.";
            return;
        }

        MapImage.Cursor = System.Windows.Input.Cursors.Pen;
    }

    private void StartWarpPick(System.Windows.Point p)
    {
        if (!MapAssets.TryHit((int)p.X, (int)p.Y, _map, _mapView, out int x, out int y))
        {
            return;
        }

        _warpPick.Begin(x, y);
        MapImage.CaptureMouse();
        UpdateSelectionVisual();
    }

    private void StopWarpPick()
    {
        if (!_warpPick.Dragging)
        {
            return;
        }

        _warpPick.End(_map.Width, _map.Height, maxSize: 256);
        if (MapImage.IsMouseCaptured)
        {
            MapImage.ReleaseMouseCapture();
        }

        int x0 = Math.Min(_warpPick.X0, _warpPick.X1);
        int y0 = Math.Min(_warpPick.Y0, _warpPick.Y1);
        int x1 = Math.Max(_warpPick.X0, _warpPick.X1);
        int y1 = Math.Max(_warpPick.Y0, _warpPick.Y1);
        MapWarp? existing = _warpPick.Width == 1 && _warpPick.Height == 1
            ? _warps.At(_map.Name, x0, y0)
            : null;
        MapWarp warp = existing ?? new MapWarp(
            CurrentMapId(),
            "",
            x0,
            y0,
            x1,
            y1,
            "",
            0,
            -1,
            -1,
            WarpOrigin.Sidecar);
        _warpPick.Clear();
        UpdateSelectionVisual();
        EditWarp(warp, isNew: existing is null);
    }

    private void EditWarp(MapWarp warp, bool isNew)
    {
        var dialog = new WarpEditWindow(
            warp,
            isNew,
            _mapNames.Choices(),
            w => WarpCheck.For(w, _warps.All(), MapSizes(), KnownMapIds())) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (dialog.Result == WarpEditWindow.Choice.Delete)
        {
            _warps.Remove(warp);
            try
            {
                PersistWarps();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(this, ex.Message, "Save warps");
            }

            Redraw();
            StatusText.Text = "Removed warp.";
            return;
        }

        _warps.Add(dialog.Warp);
        try
        {
            PersistWarps();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Save warps");
        }

        Redraw();
        if (dialog.Result == WarpEditWindow.Choice.Go)
        {
            Travel(dialog.Warp, allowBeside: (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt);
            return;
        }

        StatusText.Text = $"Saved warp → {(string.IsNullOrWhiteSpace(dialog.Warp.Destination) ? dialog.Warp.DestMapId.ToString() : dialog.Warp.Destination)}.";
    }

    private bool TryTravel(int x, int y, bool beside = false)
    {
        MapWarp? warp = _warps.At(_map.Name, x, y)
            ?? (WantServerOverlay ? _serverWorld.At(_map.Name, x, y) : null);
        if (warp is null)
        {
            StatusText.Text = $"({x},{y}) is not a warp tile. Turn on Warps, or Edit warps to add one.";
            return false;
        }

        return Travel(warp.Value, allowBeside: beside);
    }

    public void RefreshWarps()
    {
        if (WarpsBox is not null)
        {
            WarpsBox.IsChecked = true;
        }

        Redraw();
        RefreshAreaInfo();
    }

    public void RefreshServerOverlay()
    {
        if (!_serverWorld.IsEmpty)
        {
            if (ServerBox is not null)
            {
                ServerBox.IsChecked = true;
            }

            if (ServerOverlayBox is not null)
            {
                ServerOverlayBox.IsChecked = true;
            }
        }

        Redraw();
        RefreshAreaInfo();
    }

    public bool TryShowWarp(MapWarp warp)
    {
        if (!TryFindMap(warp.MapId, "", out MapListItem? item) || item is null)
        {
            StatusText.Text = $"Map {warp.MapId} is not in this client.";
            return false;
        }

        _pendingLanding = (Math.Min(warp.X1, warp.X2), Math.Min(warp.Y1, warp.Y2));
        if (WarpsBox is not null)
        {
            WarpsBox.IsChecked = true;
        }

        OpenItem(item);
        return true;
    }

    public bool TryTravelWarp(MapWarp warp, bool beside = false) => Travel(warp, allowBeside: beside);

    private void OpenBesideClicked(object sender, RoutedEventArgs e)
    {
        if (Owner is MainWindow main)
        {
            main.OpenSecondMapEditor();
        }
    }

    private void AllWarpsClicked(object sender, RoutedEventArgs e)
    {
        if (Owner is MainWindow main)
        {
            main.OpenWarpEditor();
            return;
        }

        Busy.Run(this, "Opening Warp Editor…", () =>
        {
            var window = new WarpEditorWindow(_warps, _workspace, w => TryShowWarp(w), w => Travel(w), RefreshWarps, _packedMaps)
            {
                Owner = this
            };
            window.Show();
        });
    }

    private bool Travel(MapWarp warp, bool allowBeside = false)
    {
        if (allowBeside && Owner is MainWindow main && main.TryOpenWarpDestBeside(this, warp))
        {
            return true;
        }

        if (!ConfirmDiscard())
        {
            return true;
        }

        int destId = warp.DestMapId > 0 ? warp.DestMapId : WarpTable.GuessMapId(warp.Destination);
        if (!TryFindMap(destId, warp.Destination, out MapListItem? item) || item is null)
        {
            StatusText.Text = destId > 0
                ? $"Warp destination map {destId} is not in this client."
                : $"Warp \"{warp.Destination}\" has no dest map id. Edit the warp and set Dest map id.";
            return true;
        }

        int landX = warp.DestX;
        int landY = warp.DestY;
        if (!warp.HasLanding)
        {
            int sourceId = CurrentMapId();
            foreach (MapWarp back in _warps.For(destId))
            {
                if (back.DestMapId == sourceId || (sourceId > 0 && WarpTable.GuessMapId(back.Destination) == sourceId))
                {
                    landX = back.X1;
                    landY = back.Y1;
                    break;
                }
            }
        }

        _pendingLanding = landX >= 0 && landY >= 0 ? (landX, landY) : null;
        OpenItem(item);
        return true;
    }

    private void ApplyPendingLanding()
    {
        if (_pendingLanding is not (int x, int y))
        {
            return;
        }

        _pendingLanding = null;
        Dispatcher.BeginInvoke(() => CenterOnTile(x, y), DispatcherPriority.Loaded);
    }

    private void CenterOnTile(int x, int y)
    {
        if (MapScroller is null)
        {
            return;
        }

        x = Math.Clamp(x, 0, Math.Max(0, _map.Width - 1));
        y = Math.Clamp(y, 0, Math.Max(0, _map.Height - 1));
        (int sx, int sy) = MapAssets.TileToScreen(x, y, _mapView.OriginX, _mapView.OriginY, _mapView.StepX, _mapView.StepY);
        double cx = (sx + _mapView.TileW / 2.0) * _mapZoom;
        double cy = (sy + _mapView.TileH / 2.0) * _mapZoom;
        MapScroller.UpdateLayout();
        MapScroller.ScrollToHorizontalOffset(Math.Max(0, cx - MapScroller.ViewportWidth / 2));
        MapScroller.ScrollToVerticalOffset(Math.Max(0, cy - MapScroller.ViewportHeight / 2));
        StatusText.Text = $"Arrived ({x},{y}) on {Path.GetFileName(_map.Name)}.{WarpStatus()}";
    }

    private bool TryFindMap(int mapId, string destName, out MapListItem? item)
    {
        item = null;
        if (mapId > 0)
        {
            item = _allMaps.FirstOrDefault(m => MapIdOf(m) == mapId);
            if (item is not null)
            {
                return true;
            }
        }

        if (string.IsNullOrWhiteSpace(destName))
        {
            return false;
        }

        item = _allMaps.FirstOrDefault(m =>
            Path.GetFileNameWithoutExtension(m.Name).Equals(destName, StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains(destName, StringComparison.OrdinalIgnoreCase)
            || destName.Contains(Path.GetFileNameWithoutExtension(m.Name), StringComparison.OrdinalIgnoreCase));
        return item is not null;
    }

    private static int MapIdOf(MapListItem item)
    {
        string name = item.Archive is null ? item.Path : item.EntryName;
        return WarpTable.TryParseMapId(name, out int id) ? id : WarpTable.GuessMapId(name);
    }

    private string MapDisplayName(string fileLabel, string mapIdSource)
    {
        int id = WarpTable.TryParseMapId(mapIdSource, out int parsed) ? parsed : WarpTable.GuessMapId(Path.GetFileName(mapIdSource));
        string friendly = _mapNames.FriendlyName(id);
        if (string.IsNullOrWhiteSpace(friendly))
        {
            return fileLabel;
        }

        return $"{friendly}  ({fileLabel})";
    }

    private int CurrentMapId() =>
        WarpTable.TryParseMapId(_map.Name, out int id) ? id : WarpTable.GuessMapId(_map.Name);

    private void ImportWarpsClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Warp dumps|*.json;*.csv;*.txt;*.tsv;*.xml;*.sql|JSON|*.json|CSV / text|*.csv;*.txt;*.tsv|XML|*.xml|SQL|*.sql|All files|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            WarpImportResult imported = WarpImport.FromPath(dialog.FileName);
            if (imported.Warps.Count == 0)
            {
                StatusText.Text = "No warps were found. Tried JSON, CSV headers, XML, SQL, and TMap.";
                return;
            }

            _warps.AddRange(imported.Warps, replace: true);
            PersistWarps();
            if (WarpsBox is not null)
            {
                WarpsBox.IsChecked = true;
            }

            Redraw();
            StatusText.Text = $"Imported {imported.Warps.Count} warps ({imported.Format}) from {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Import warps");
        }
    }

    private void SaveWarpsClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            PersistWarps();
            StatusText.Text = $"Saved warps for {_workspace?.ActiveClient?.Name ?? "this client"}.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Save warps");
        }
    }

    private void PersistWarps()
    {
        if (_workspace?.ActiveClient is null)
        {
            return;
        }

        WarpStore.Save(_workspace.ActiveClient, _warps);
        ClientSnapshots.SaveWarps(_workspace.ActiveClient, _warps);
        ClientChangeLog.Append(_workspace.ActiveClient, "warp", $"Saved {_warps.WarpCount} warps.");
    }

    private string WarpStatus()
    {
        IReadOnlyList<MapWarp> warps = _warps.For(_map.Name);
        if (warps.Count == 0)
        {
            return "";
        }

        string dests = string.Join(", ", warps
            .Select(w => string.IsNullOrEmpty(w.Destination) ? "exit" : w.Destination)
            .Distinct()
            .Take(4));
        int landings = _warps.Arrivals(_map.Name).Count;
        int mapId = CurrentMapId();
        string check = WarpCheck.Summary(
            WarpCheck.Run(_warps.All(), MapSizes(), KnownMapIds()).Where(i => i.MapId == mapId));
        string issues = string.IsNullOrWhiteSpace(check) ? "" : $" {check}.";
        return $"  {warps.Count} exit{(warps.Count == 1 ? "" : "s")} (blue): {dests}. {landings} landing{(landings == 1 ? "" : "s")} (yellow). Click blue to jump.{issues}";
    }

    private Dictionary<int, (int Width, int Height)>? _mapSizes;

    private Dictionary<int, (int Width, int Height)> MapSizes()
    {
        if (_mapSizes is not null)
        {
            return _mapSizes;
        }

        _mapSizes = Busy.Compute(this, "Reading map sizes…", progress =>
            MapSizeIndex.Load(_mapFolders, _packedMaps, progress));
        return _mapSizes;
    }

    private IReadOnlyCollection<int> KnownMapIds()
    {
        var ids = _mapNames.Ids().ToHashSet();
        foreach (int id in MapSizes().Keys)
        {
            ids.Add(id);
        }

        return ids;
    }

    private int FloorAtlasOffset()
    {
        if (int.TryParse(FloorOffsetBox?.Text?.Trim(), out int n) && n > 0)
        {
            return n;
        }

        return 0;
    }

    private void FloorOffsetKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RedrawClicked(sender, e);
            e.Handled = true;
        }
    }

    private void FlushMap()
    {
        if (_dirtyTiles.Count is > 0 and < 80
            && _mapPixels is not null
            && _mapBitmap is not null
            && _mapPixels.Width == _mapView.Width
            && _mapPixels.Height == _mapView.Height)
        {
            (int x, int y, int w, int h) = _assets.RepaintTiles(
                _map,
                _mapPixels,
                _mapView,
                _dirtyTiles,
                SnowBox?.IsChecked == true,
                FloorAtlasOffset(),
                TabMapBox?.IsChecked == true,
                VisibleWarps(),
                VisibleArrivals(),
                VisibleServerWarps(),
                VisibleServerArrivals());
            OverlayMap(_dirtyTiles);
            _dirtyTiles.Clear();
            WpfImages.Update(_mapBitmap, _mapPixels, x, y, w, h);
            return;
        }

        Redraw();
    }

    private void BindHeaLights()
    {
        _heaReady = HeaLights.ClientHasMapLights(_heaArchives);
        if (HeaLightsBox is not null)
        {
            HeaLightsBox.Visibility = Visibility.Visible;
            if (HeaLightsBox.IsChecked != true && _heaReady)
            {
                HeaLightsBox.IsChecked = true;
            }
        }
    }

    private void OverlayHeaLights() => OverlayMap();

    private void OverlayMap(IEnumerable<(int X, int Y)>? only = null)
    {
        if (HeaLightsBox?.IsChecked != true || _mapPixels is null)
        {
            return;
        }

        var view = new HeaLights.MapViewLike(
            _mapView.OriginX,
            _mapView.OriginY,
            _mapView.StepX,
            _mapView.StepY,
            _mapView.TileW,
            _mapView.TileH);
        if (_lightOverlay.HasPixels)
        {
            HeaLights.Overlay(_mapPixels, _map, view, _lightOverlay.Pixels, OverlayBlendMode.Shade, only);
        }

        if (_effectOverlay.HasPixels)
        {
            HeaLights.Overlay(_mapPixels, _map, view, _effectOverlay.Pixels, OverlayBlendMode.Additive, only);
        }
    }

    private void Redraw()
    {
        _dirtyTiles.Clear();
        bool snow = SnowBox?.IsChecked == true;
        _mapView = MapAssets.Layout(_map);
        _mapPixels = _assets.Render(
            _map,
            snow,
            floorOffset: FloorAtlasOffset(),
            tabMap: TabMapBox?.IsChecked == true,
            warps: VisibleWarps(),
            arrivals: VisibleArrivals(),
            serverWarps: VisibleServerWarps(),
            serverArrivals: VisibleServerArrivals(),
            reuse: _mapPixels);
        OverlayHeaLights();
        if (_mapBitmap is null
            || _mapBitmap.PixelWidth != _mapPixels.Width
            || _mapBitmap.PixelHeight != _mapPixels.Height)
        {
            _mapBitmap = WpfImages.ToWriteable(_mapPixels);
            MapImage.Source = _mapBitmap;
        }
        else
        {
            WpfImages.UpdateFull(_mapBitmap, _mapPixels);
        }

        MapImage.Width = _mapPixels.Width;
        MapImage.Height = _mapPixels.Height;
        if (GhostLayer is not null)
        {
            GhostLayer.Width = _mapPixels.Width;
            GhostLayer.Height = _mapPixels.Height;
        }

        if (WarpLabelLayer is not null)
        {
            WarpLabelLayer.Width = _mapPixels.Width;
            WarpLabelLayer.Height = _mapPixels.Height;
        }

        LayoutWarpLabels();

        if (!_syncingSize)
        {
            WidthBox.Text = _map.Width.ToString();
            HeightBox.Text = _map.Height.ToString();
        }

        if (!_painting)
        {
            UpdateBrushPreview();
            UpdateSelectionVisual();
        }
    }

    private void SelectBrush(int id, bool jumpPage)
    {
        CurrentBrush = Math.Max(0, id);
        if (TileIdBox is not null && TileIdBox.Text != CurrentBrush.ToString())
        {
            TileIdBox.Text = CurrentBrush.ToString();
        }

        if (jumpPage)
        {
            IReadOnlyList<int> ids = PaletteIds();
            int index = ids.ToList().FindIndex(v => v == CurrentBrush);
            if (index >= 0)
            {
                _page = index / PageSize;
            }

            LoadPalettePage();
        }
        else
        {
            foreach (TileChoice tile in _palette)
            {
                tile.Selected = tile.Id == CurrentBrush;
            }
        }

        UpdateBrushPreview();
        HideGhost();
        SyncMacroBarSelection();
    }

    private void UpdateGhost(int tileX, int tileY)
    {
        if (GhostImage is null)
        {
            return;
        }

        if (_ghostBmp is null || (Layer != PaintLayer.Structure && CurrentBrush <= 0))
        {
            HideGhost();
            return;
        }

        if (_ghostTileX == tileX && _ghostTileY == tileY && GhostImage.Visibility == Visibility.Visible)
        {
            return;
        }

        _ghostTileX = tileX;
        _ghostTileY = tileY;
        int stepX = _mapView.StepX > 0 ? _mapView.StepX : 28;
        int stepY = _mapView.StepY > 0 ? _mapView.StepY : 14;
        int originX = _mapView.OriginX > 0 ? _mapView.OriginX : _map.Height * stepX;
        int originY = _mapView.OriginY > 0 ? _mapView.OriginY : 160;
        (int sx, int sy) = MapAssets.TileToScreen(tileX, tileY, originX, originY, stepX, stepY);
        int gx = sx;
        int gy = sy;
        int h = _ghostBmp.PixelHeight;
        if (Layer == PaintLayer.Structure && TryGetStructure(CurrentBrush, out StructureStamp stamp))
        {
            int stampOriginX = stamp.Height * stepX;
            int stampOriginY = 160;
            (int ssx, int ssy) = MapAssets.TileToScreen(0, 0, stampOriginX, stampOriginY, stepX, stepY);
            gx = sx - ssx + _ghostCropX;
            gy = sy - ssy + _ghostCropY;
        }
        else if (Layer == PaintLayer.LeftWall)
        {
            gy = sy + MapAssets.WallFootNative - h;
        }
        else if (Layer == PaintLayer.RightWall)
        {
            gx = sx + stepX;
            gy = sy + MapAssets.WallFootNative + MapAssets.RightWallFootAdjust - h;
        }

        GhostImage.Source = _ghostBmp;
        Canvas.SetLeft(GhostImage, gx);
        Canvas.SetTop(GhostImage, gy);
        GhostImage.Visibility = Visibility.Visible;
    }

    private void HideGhost()
    {
        _ghostTileX = int.MinValue;
        if (GhostImage is not null)
        {
            GhostImage.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateBrushPreview()
    {
        if (BrushImage is null)
        {
            return;
        }

        int id = CurrentBrush;
        if (Layer == PaintLayer.Structure)
        {
            EnsureStructures();
            if (id <= 0 || !TryGetStructure(id, out StructureStamp stamp))
            {
                BrushLabel.Text = "Structure  eraser (click a building to clear it)";
                BrushImage.Source = null;
                _ghostBmp = null;
                _ghostCropX = 0;
                _ghostCropY = 0;
                _ghostTileX = int.MinValue;
                return;
            }

            BrushLabel.Text = stamp.Label;
            PixelBuffer preview = RenderStamp(stamp, maxEdge: 120, out _, out _);
            BrushImage.Source = WpfImages.ToBitmap(preview);
            _ghostBmp = WpfImages.ToBitmap(RenderStamp(stamp, maxEdge: 0, out _ghostCropX, out _ghostCropY));
            _ghostTileX = int.MinValue;
            return;
        }

        BrushLabel.Text = id == 0 ? $"{Layer}  empty (erase)" : $"{Layer}  #{id}";
        PixelBuffer? img = BrushImageFor(id);
        BrushImage.Source = img is null || id == 0 ? null : WpfImages.Checkerboard(img.Width, img.Height, img);
        _ghostBmp = img is null || id == 0 ? null : WpfImages.ToBitmap(img);
        _ghostCropX = 0;
        _ghostCropY = 0;
        _ghostTileX = int.MinValue;
    }

    private PixelBuffer? BrushImageFor(int id) =>
        Layer is PaintLayer.LeftWall or PaintLayer.RightWall
            ? _assets.Wall(id)
            : _assets.Floor(id, SnowBox?.IsChecked == true, FloorAtlasOffset());

    private IReadOnlyList<int> PaletteIds()
    {
        if (Layer == PaintLayer.Structure)
        {
            int n = VisibleStructures().Count;
            var ids = new List<int>(n);
            for (int i = 1; i <= n; i++)
            {
                ids.Add(i);
            }

            return ids;
        }

        if (Layer is PaintLayer.LeftWall or PaintLayer.RightWall)
        {
            return FilterWallIds(_assets.WallIds);
        }

        int count = SnowBox?.IsChecked == true ? _assets.SnowCount : _assets.FloorCount;
        count = Math.Max(0, count - FloorAtlasOffset());
        var floorIds = new List<int>(count);
        for (int i = 1; i <= count; i++)
        {
            floorIds.Add(i);
        }

        return FilterFloorIds(floorIds);
    }

    private void LoadPalettePage()
    {
        if (TilePalette is null)
        {
            return;
        }

        IReadOnlyList<int> ids = PaletteIds();
        int pageSize = Layer == PaintLayer.Structure ? 32 : PageSize;
        int pages = Math.Max(1, (int)Math.Ceiling(ids.Count / (double)pageSize));
        _page = Math.Clamp(_page, 0, pages - 1);
        int start = _page * pageSize;
        _palette = [];
        bool snow = SnowBox?.IsChecked == true;
        bool walls = Layer is PaintLayer.LeftWall or PaintLayer.RightWall;
        for (int i = start; i < Math.Min(ids.Count, start + pageSize); i++)
        {
            int id = ids[i];
            if (Layer == PaintLayer.Structure && TryGetStructure(id, out StructureStamp stamp))
            {
                PixelBuffer preview = RenderStamp(stamp, maxEdge: 96, out _, out _);
                _palette.Add(new TileChoice(id, stamp.Label, WpfImages.ToBitmap(preview), id == CurrentBrush));
                continue;
            }

            PixelBuffer? img = walls ? _assets.Wall(id) : _assets.Floor(id, snow, FloorAtlasOffset());
            if (img is null)
            {
                continue;
            }

            int shown = walls ? id : id + FloorAtlasOffset();
            string label = walls
                ? WallFamilies.Label(shown, _wallFamilies)
                : FloorFamilies.Label(shown, _floorFamilies);
            _palette.Add(new TileChoice(id, label, WpfImages.ToBitmap(img), id == CurrentBrush));
        }

        TilePalette.ItemsSource = _palette;
        PageLabel.Text = Layer == PaintLayer.Structure
            ? $"{StampCategories.Title(_stampGroup)}  {VisibleStructures().Count}   page {_page + 1} / {pages}"
            : $"page {_page + 1} / {pages}";
    }

    private void RefreshMapList()
    {
        if (MapList is null)
        {
            return;
        }

        string filter = MapFilterBox?.Text ?? "";
        var folders = MapSearchFolders().ToList();
        bool prefixFolder = folders.Count > 1 || _packedMaps.Count > 0;
        bool prefixPacked = folders.Count > 0 || _packedMaps.Count > 1;
        var items = new List<MapListItem>();
        _allMaps.Clear();
        foreach (string folder in folders)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*.map", SearchOption.AllDirectories)
                    .Where(p => Path.GetExtension(p).Equals(".map", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                continue;
            }

            foreach (string path in files.OrderBy(p => p, NaturalStringComparer.OrdinalIgnoreCase))
            {
                string relative = Path.GetRelativePath(folder, path);
                string file = prefixFolder ? Path.Combine(Path.GetFileName(folder), relative) : relative;
                string name = MapDisplayName(file, path);
                var row = new MapListItem(name, path, null, path);
                _allMaps.Add(row);
                if (!string.IsNullOrWhiteSpace(filter) && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                items.Add(row);
            }
        }

        foreach (IArchive archive in _packedMaps)
        {
            string prefix = prefixPacked ? archive.DisplayName + Path.DirectorySeparatorChar : "";
            foreach (ArchiveEntry entry in archive.Entries
                         .Where(e => e.Extension.Equals(".map", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(e => e.Name, NaturalStringComparer.OrdinalIgnoreCase))
            {
                string name = MapDisplayName(prefix + entry.DisplayName, entry.Name);
                var row = new MapListItem(name, "", archive, entry.Name);
                _allMaps.Add(row);
                if (!string.IsNullOrWhiteSpace(filter) && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                items.Add(row);
            }
        }

        MapList.ItemsSource = items.OrderBy(i => i.Name, NaturalStringComparer.OrdinalIgnoreCase).ToList();
    }

    private void RefreshSizeBox()
    {
        _syncingSize = true;
        var choices = MapFile.SizeOptions(_map.Tiles.Length)
            .Select(o => new SizeChoice(o.Width, o.Height))
            .ToList();
        SizeBox.ItemsSource = choices;
        SizeBox.SelectedItem = choices.FirstOrDefault(c => c.Width == _map.Width && c.Height == _map.Height)
            ?? choices.FirstOrDefault();
        WidthBox.Text = _map.Width.ToString();
        HeightBox.Text = _map.Height.ToString();
        _syncingSize = false;
    }

    private bool ConfirmDiscard()
    {
        if (!UnsavedWork)
        {
            return true;
        }

        MessageBoxResult result = System.Windows.MessageBox.Show(
            this,
            OverlayDirty && OverlayPersists && !_dirty
                ? "Save overlay changes?"
                : "Save changes to the current map and overlays?",
            "Unsaved map",
            MessageBoxButton.YesNoCancel);
        if (result == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (result == MessageBoxResult.Yes)
        {
            SaveClicked(this, new RoutedEventArgs());
            if (OverlayDirty && OverlayPersists)
            {
                OverlaySaveClicked(this, new RoutedEventArgs());
            }

            return !UnsavedWork;
        }

        return true;
    }

    private string TitleText()
    {
        string name = string.IsNullOrWhiteSpace(_map.Name) ? "untitled" : Path.GetFileName(_map.Name);
        return UnsavedWork ? $"Map Editor — {name} *" : $"Map Editor — {name}";
    }

    private string? ExistingMapsFolder() => _mapFolders.FirstOrDefault(Directory.Exists);

    private IEnumerable<string> MapSearchFolders() => _mapFolders;

    private void AddMapFolders(IEnumerable<string>? folders)
    {
        if (folders is null)
        {
            return;
        }

        foreach (string folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                continue;
            }

            string full = Path.GetFullPath(folder);
            if (!_mapFolders.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                _mapFolders.Add(full);
            }
        }
    }

    private void AddPackedMaps(IEnumerable<IArchive>? archives)
    {
        if (archives is null)
        {
            return;
        }

        foreach (IArchive archive in archives)
        {
            if (_packedMaps.Any(a =>
                    string.Equals(a.SourcePath, archive.SourcePath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(a.DisplayName, archive.DisplayName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _packedMaps.Add(archive);
        }
    }

    private void AddMapsFolderClicked(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Select a folder that contains .map files (searched recursively)",
            UseDescriptionForTitle = true
        };
        string? current = ExistingMapsFolder();
        if (current is not null)
        {
            dialog.SelectedPath = current;
        }

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        AddMapFolders([dialog.SelectedPath]);
        RefreshMapList();
        StatusText.Text = $"Maps folders: {_mapFolders.Count}. Added {dialog.SelectedPath}";
    }

    private void LoadServerWorldClicked(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Hybrasyl xml/maps or Chaos Data/Configuration (read-only overlay)",
            UseDescriptionForTitle = true
        };
        string current = _workspace?.ActiveClient?.ServerWorldFolder ?? "";
        if (Directory.Exists(current))
        {
            dialog.SelectedPath = current;
        }

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        ApplyServerWorld(dialog.SelectedPath, persist: true);
    }

    private void ClearServerWorldClicked(object sender, RoutedEventArgs e)
    {
        _serverWorld.Clear();
        PersistServerWorldFolder("");
        RefreshServerOverlay();
        StatusText.Text = "Cleared the server overlay. DAT warps are unchanged.";
    }

    private void ServerOverlayToggled(object sender, RoutedEventArgs e)
    {
        if (_syncingServer)
        {
            return;
        }

        _syncingServer = true;
        try
        {
            bool on = sender == ServerOverlayBox
                ? ServerOverlayBox?.IsChecked == true
                : ServerBox?.IsChecked == true;
            if (ServerBox is not null)
            {
                ServerBox.IsChecked = on;
            }

            if (ServerOverlayBox is not null)
            {
                ServerOverlayBox.IsChecked = on;
            }

            if (IsLoaded)
            {
                Redraw();
                RefreshAreaInfo();
            }
        }
        finally
        {
            _syncingServer = false;
        }
    }

    private void TryAutoLoadServerWorld()
    {
        if (!_serverWorld.IsEmpty)
        {
            RefreshServerOverlay();
            return;
        }

        string folder = _workspace?.ActiveClient?.ServerWorldFolder ?? "";
        if (Directory.Exists(folder))
        {
            ApplyServerWorld(folder, persist: false);
        }
    }

    private void ApplyServerWorld(string path, bool persist)
    {
        try
        {
            _serverWorld.LoadFrom(path);
            if (persist)
            {
                PersistServerWorldFolder(path);
            }

            RefreshServerOverlay();
            if (Owner is MainWindow main)
            {
                main.NotifyServerWorldLoaded();
            }

            StatusText.Text = _serverWorld.IsEmpty
                ? "No Hybrasyl maps or Chaos map templates in that folder."
                : $"Server overlay: {_serverWorld.Kind}  {_serverWorld.MapCount} maps, {_serverWorld.WarpCount} warps (read-only).";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Server world");
        }
    }

    private void PersistServerWorldFolder(string path)
    {
        ClientProfile? client = _workspace?.ActiveClient;
        if (client is null)
        {
            return;
        }

        client.ServerWorldFolder = path ?? "";
        _workspace?.Settings.Save();
    }

    private void StartSelect(System.Windows.Point p)
    {
        if (!MapAssets.TryHit((int)p.X, (int)p.Y, _map, _mapView, out int x, out int y))
        {
            return;
        }

        if (_painting)
        {
            StopPaint();
        }

        _selection.Begin(x, y);
        HideGhost();
        MapImage.CaptureMouse();
        UpdateSelectionVisual();
    }

    private void StopSelect()
    {
        if (!_selection.Dragging)
        {
            return;
        }

        _selection.End(_map.Width, _map.Height);
        if (MapImage.IsMouseCaptured)
        {
            MapImage.ReleaseMouseCapture();
        }

        UpdateSelectionVisual();
        if (StampSelectionButton is not null)
        {
            StampSelectionButton.IsEnabled = _selection.HasSelection;
        }

        StatusText.Text =
            $"Selected {_selection.Width}×{_selection.Height} tiles. Click Use as stamp (or press Enter / Ctrl+C). Esc clears.";
    }

    private void ClearSelection()
    {
        _selection.Clear();
        if (SelectionShape is not null)
        {
            SelectionShape.Visibility = Visibility.Collapsed;
        }

        if (StampSelectionButton is not null)
        {
            StampSelectionButton.IsEnabled = false;
        }
    }

    private void UpdateSelectionVisual()
    {
        if (SelectionShape is null)
        {
            return;
        }

        IsoTileSelection box = _warpPick.Dragging || _warpPick.HasSelection ? _warpPick : _selection;
        if (!box.HasSelection && !box.Dragging)
        {
            SelectionShape.Visibility = Visibility.Collapsed;
            return;
        }

        box.Clamp(_map.Width, _map.Height, box == _warpPick ? 256 : 48);
        SelectionShape.Points = new PointCollection(
            box.ScreenCorners(_map.Height).Select(p => new System.Windows.Point(p.X, p.Y)));
        SelectionShape.Stroke = box == _warpPick ? System.Windows.Media.Brushes.Cyan : System.Windows.Media.Brushes.Orange;
        SelectionShape.Visibility = Visibility.Visible;
    }

    private void StampSelectionClicked(object sender, RoutedEventArgs e)
    {
        if (!_selection.HasSelection)
        {
            return;
        }

        _selection.Clamp(_map.Width, _map.Height);
        int w = _selection.Width;
        int h = _selection.Height;
        var cells = new MapTile[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                cells[y * w + x] = _map[_selection.X0 + x, _selection.Y0 + y];
            }
        }

        var stamp = new StructureStamp(
            $"sel-{_selection.X0},{_selection.Y0}-{w}x{h}-{Environment.TickCount64}",
            $"Selection  {w}×{h}",
            w,
            h,
            cells,
            1,
            CopyFloors: true,
            Category: _stampGroup);
        SelectStamp(stamp, $"Using {w}×{h} selection as a stamp for this session. Save preset to keep it. Click the map to paint it.");
        if (PaintModeBox is not null)
        {
            PaintModeBox.IsChecked = true;
        }
        else if (SelectModeBox is not null)
        {
            SelectModeBox.IsChecked = false;
        }

        MapImage.Cursor = System.Windows.Input.Cursors.Pen;
    }

    private void SavePresetClicked(object sender, RoutedEventArgs e)
    {
        if (_selection.HasSelection)
        {
            _selection.Clamp(_map.Width, _map.Height);
            int w = _selection.Width;
            int h = _selection.Height;
            var cells = new MapTile[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    cells[y * w + x] = _map[_selection.X0 + x, _selection.Y0 + y];
                }
            }

            PersistAndSelectStamp(
                new StructureStamp(
                    $"user-{Environment.TickCount64}-{w}x{h}",
                    $"Preset  {w}×{h}",
                    w,
                    h,
                    cells,
                    1,
                    CopyFloors: true,
                    Category: _stampGroup),
                $"Saved preset: {w}×{h} tiles.");
            return;
        }

        if (TryGetStructure(CurrentBrush, out StructureStamp stamp))
        {
            var saved = new StructureStamp(
                stamp.Key.StartsWith("user-", StringComparison.Ordinal) ? stamp.Key : $"user-{Environment.TickCount64}-{stamp.Width}x{stamp.Height}",
                stamp.Label.StartsWith("Preset ", StringComparison.Ordinal) ? stamp.Label : $"Preset  {stamp.Label}",
                stamp.Width,
                stamp.Height,
                stamp.Cells,
                Math.Max(1, stamp.Uses),
                stamp.CopyFloors,
                stamp.Category);
            PersistAndSelectStamp(saved, $"Saved preset: {saved.Label}");
            return;
        }

        StatusText.Text = "Select tiles on the map, or highlight a structure, then Save preset.";
    }

    private void RemovePresetClicked(object sender, RoutedEventArgs e)
    {
        if (!TryGetStructure(CurrentBrush, out StructureStamp stamp))
        {
            StatusText.Text = "Highlight a structure in the palette, then Remove preset.";
            return;
        }

        StructurePresetStore.Load().Remove(stamp.Key);
        _structures.RemoveAll(s => string.Equals(s.Key, stamp.Key, StringComparison.Ordinal));
        if (StructureLayer is not null)
        {
            StructureLayer.IsChecked = true;
        }

        RestoreGroupPlace();
        SelectBrush(_structureBrush, jumpPage: true);
        StatusText.Text = $"Removed preset: {stamp.Label}";
    }

    private void PersistAndSelectStamp(StructureStamp stamp, string status)
    {
        StructurePresetStore.Load().AddOrUpdate(stamp);
        SelectStamp(stamp, status);
    }

    private void SelectStamp(StructureStamp stamp, string status)
    {
        EnsureStructures();
        _structures.RemoveAll(s => string.Equals(s.Key, stamp.Key, StringComparison.Ordinal));
        _structures.Insert(0, stamp);
        if (StructureLayer is not null)
        {
            StructureLayer.IsChecked = true;
        }

        SelectStampGroup(stamp.Category);
        _page = 0;
        _structureBrush = 1;
        _groupPage[stamp.Category] = 0;
        _groupBrush[stamp.Category] = 1;
        SelectBrush(1, jumpPage: true);
        StatusText.Text = status;
    }

    private void EnsureStructures()
    {
        if (_structures.Count > 0)
        {
            return;
        }

        MapCatalogStats stats = MapCatalogStats.Load(MapSearchFolders());
        _structures = MapStampLibrary.Load(stats, _assets.WallIds);
        RestoreGroupPlace();
    }

    private List<StructureStamp> VisibleStructures()
    {
        EnsureStructures();
        List<StructureStamp> visible = _structures.FindAll(s => s.Category == _stampGroup);
        if (_stampGroup == StampCategory.Building)
        {
            visible.RemoveAll(MapStampLibrary.IsProcedural);
            const int keep = 9 * 32;
            if (visible.Count > keep)
            {
                visible.RemoveRange(keep, visible.Count - keep);
            }
        }

        return visible;
    }

    private bool TryGetStructure(int brushId, out StructureStamp stamp)
    {
        List<StructureStamp> visible = VisibleStructures();
        int index = brushId - 1;
        if ((uint)index >= (uint)visible.Count)
        {
            stamp = default;
            return false;
        }

        stamp = visible[index];
        return true;
    }

    private PixelBuffer RenderStamp(StructureStamp stamp, int maxEdge, out int cropX, out int cropY)
    {
        var mini = new MapFile(stamp.Width, stamp.Height);
        for (int y = 0; y < stamp.Height; y++)
        {
            for (int x = 0; x < stamp.Width; x++)
            {
                MapTile cell = stamp.Cells[y * stamp.Width + x];
                ushort floor = stamp.CopyFloors
                    ? cell.Floor
                    : cell.LeftWall != 0 || cell.RightWall != 0 ? (ushort)1 : (ushort)0;
                mini[x, y] = new MapTile(floor, cell.LeftWall, cell.RightWall);
            }
        }

        PixelBuffer raw = _assets.Render(
            mini,
            SnowBox?.IsChecked == true,
            maxEdge,
            FloorAtlasOffset(),
            transparentBackdrop: true);
        return raw.CropVisible(pad: 4, out cropX, out cropY);
    }

    private bool StampStructure(int originX, int originY, StructureStamp stamp)
    {
        bool changed = false;
        for (int y = 0; y < stamp.Height; y++)
        {
            for (int x = 0; x < stamp.Width; x++)
            {
                MapTile cell = stamp.Cells[y * stamp.Width + x];
                if (cell.Floor == 0 && cell.LeftWall == 0 && cell.RightWall == 0)
                {
                    continue;
                }

                if (!stamp.CopyFloors && cell.LeftWall == 0 && cell.RightWall == 0)
                {
                    continue;
                }

                int mx = originX + x;
                int my = originY + y;
                if ((uint)mx >= (uint)_map.Width || (uint)my >= (uint)_map.Height)
                {
                    continue;
                }

                MapTile tile = _map[mx, my];
                MapTile next = stamp.CopyFloors
                    ? cell
                    : new MapTile(tile.Floor, cell.LeftWall, cell.RightWall);
                if (next.Floor == tile.Floor && next.LeftWall == tile.LeftWall && next.RightWall == tile.RightWall)
                {
                    continue;
                }

                _map[mx, my] = next;
                _dirtyTiles.Add((mx, my));
                changed = true;
            }
        }

        return changed;
    }

    private bool EraseStructureAt(int x, int y)
    {
        if ((uint)x >= (uint)_map.Width || (uint)y >= (uint)_map.Height)
        {
            return false;
        }

        if (_map[x, y].LeftWall == 0 && _map[x, y].RightWall == 0)
        {
            return false;
        }

        var seen = new bool[_map.Tiles.Length];
        var queue = new Queue<int>();
        int start = y * _map.Width + x;
        queue.Enqueue(start);
        seen[start] = true;
        int[] dx = [1, -1, 0, 0];
        int[] dy = [0, 0, 1, -1];
        bool changed = false;
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int cx = i % _map.Width;
            int cy = i / _map.Width;
            MapTile tile = _map.Tiles[i];
            if (tile.LeftWall != 0 || tile.RightWall != 0)
            {
                _map[cx, cy] = new MapTile(tile.Floor, 0, 0);
                _dirtyTiles.Add((cx, cy));
                changed = true;
            }

            for (int d = 0; d < 4; d++)
            {
                int nx = cx + dx[d];
                int ny = cy + dy[d];
                if ((uint)nx >= (uint)_map.Width || (uint)ny >= (uint)_map.Height)
                {
                    continue;
                }

                int ni = ny * _map.Width + nx;
                if (seen[ni])
                {
                    continue;
                }

                MapTile next = _map.Tiles[ni];
                if (next.LeftWall == 0 && next.RightWall == 0)
                {
                    continue;
                }

                seen[ni] = true;
                queue.Enqueue(ni);
            }
        }

        return changed;
    }

    private void EyedropStructure(int x, int y)
    {
        if (!TryCaptureCluster(_map, x, y, out StructureStamp captured))
        {
            return;
        }

        EnsureStructures();
        captured = captured with
        {
            Label = "From map  " + captured.Label,
            Category = StampCategories.Classify(captured.Cells, captured.Width, captured.Height)
        };
        if (StructureLayer is not null)
        {
            StructureLayer.IsChecked = true;
        }

        SelectStampGroup(captured.Category);
        int existing = _structures.FindIndex(s => s.Key == captured.Key);
        if (existing < 0)
        {
            _structures.Insert(0, captured);
        }

        int visibleIndex = VisibleStructures().FindIndex(s => s.Key == captured.Key);
        SelectBrush(Math.Max(1, visibleIndex + 1), jumpPage: true);
    }

    private static bool TryCaptureCluster(MapFile map, int x, int y, out StructureStamp stamp)
    {
        stamp = default;
        if ((uint)x >= (uint)map.Width || (uint)y >= (uint)map.Height)
        {
            return false;
        }

        if (map[x, y].LeftWall == 0 && map[x, y].RightWall == 0)
        {
            return false;
        }

        var seen = new bool[map.Tiles.Length];
        var cells = new List<(int X, int Y)>();
        var queue = new Queue<int>();
        int start = y * map.Width + x;
        queue.Enqueue(start);
        seen[start] = true;
        int minX = x, maxX = x, minY = y, maxY = y;
        int[] dx = [1, -1, 0, 0];
        int[] dy = [0, 0, 1, -1];
        ushort sampleL = 0;
        ushort sampleR = 0;
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int cx = i % map.Width;
            int cy = i / map.Width;
            cells.Add((cx, cy));
            minX = Math.Min(minX, cx);
            maxX = Math.Max(maxX, cx);
            minY = Math.Min(minY, cy);
            maxY = Math.Max(maxY, cy);
            MapTile tile = map.Tiles[i];
            if (sampleL == 0 && tile.LeftWall != 0)
            {
                sampleL = tile.LeftWall;
            }

            if (sampleR == 0 && tile.RightWall != 0)
            {
                sampleR = tile.RightWall;
            }

            for (int d = 0; d < 4; d++)
            {
                int nx = cx + dx[d];
                int ny = cy + dy[d];
                if ((uint)nx >= (uint)map.Width || (uint)ny >= (uint)map.Height)
                {
                    continue;
                }

                int ni = ny * map.Width + nx;
                if (seen[ni])
                {
                    continue;
                }

                MapTile next = map.Tiles[ni];
                if (next.LeftWall == 0 && next.RightWall == 0)
                {
                    continue;
                }

                seen[ni] = true;
                queue.Enqueue(ni);
            }
        }

        int w = maxX - minX + 1;
        int h = maxY - minY + 1;
        if (cells.Count < 2 || w > 12 || h > 10)
        {
            return false;
        }

        var tiles = new MapTile[w * h];
        var key = new System.Text.StringBuilder();
        key.Append(w).Append('x').Append(h).Append(':');
        foreach ((int cx, int cy) in cells)
        {
            MapTile tile = map[cx, cy];
            tiles[(cy - minY) * w + (cx - minX)] = new MapTile(0, tile.LeftWall, tile.RightWall);
        }

        foreach (MapTile tile in tiles)
        {
            key.Append(tile.LeftWall).Append(',').Append(tile.RightWall).Append(';');
        }

        stamp = new StructureStamp(
            key.ToString(),
            $"L{sampleL}/R{sampleR}  {w}×{h}",
            w,
            h,
            tiles,
            1,
            CopyFloors: false,
            Category: StampCategories.Classify(tiles, w, h));
        return true;
    }

    private void SyncOverlayTools()
    {
        if (OverlayTools is not null)
        {
            OverlayTools.Visibility = WantOverlay ? Visibility.Visible : Visibility.Collapsed;
        }

        string? blocked = OverlaySaveBlockReason();
        bool canSave = blocked is null;
        if (OverlaySaveButton is not null)
        {
            OverlaySaveButton.IsEnabled = canSave;
            OverlaySaveButton.ToolTip = canSave
                ? "Write the light and effect HEA sheets into seo.dat"
                : blocked;
        }

        if (OverlaySaveMenu is not null)
        {
            OverlaySaveMenu.IsEnabled = canSave;
            OverlaySaveMenu.ToolTip = canSave
                ? "Write the light and effect HEA sheets into seo.dat (or the graphics folder)"
                : blocked;
        }
    }

    private string? OverlaySaveBlockReason()
    {
        string hint = OverlaySavePath()
            ?? _workspace?.ActiveClient?.Path
            ?? _assets.Seo.SourcePath;
        return DatCompatibility.OverlaySaveBlockReason(ClientFamilies.FromPath(hint));
    }

    private void OverlaySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OverlayRadiusLabel is not null && OverlayRadiusBox is not null)
        {
            OverlayRadiusLabel.Text = ((int)OverlayRadiusBox.Value).ToString();
        }

        if (OverlayStrengthLabel is not null && OverlayStrengthBox is not null)
        {
            OverlayStrengthLabel.Text = ((int)OverlayStrengthBox.Value).ToString();
        }
    }

    private void ResetOverlays()
    {
        _pendingOverlayStamp = null;
        _lightOverlay.Reset(_map.Width, _map.Height, HeaLights.PreferredFileName(_map.Name, OverlayKind.Light));
        _effectOverlay.Reset(_map.Width, _map.Height, HeaLights.PreferredFileName(_map.Name, OverlayKind.Effect));
    }

    private void LoadOverlays()
    {
        ResetOverlays();
        TryLoadOverlay(_lightOverlay, OverlayKind.Light);
        TryLoadOverlay(_effectOverlay, OverlayKind.Effect);
        if (HeaLightsBox is not null && (_lightOverlay.HasPixels || _effectOverlay.HasPixels || _heaReady))
        {
            HeaLightsBox.IsChecked = true;
        }
    }

    private void TryLoadOverlay(MapOverlayLayer layer, OverlayKind kind)
    {
        ArchiveEntry? entry = HeaLights.FindForMap(_heaArchives, _map.Name, kind);
        if (entry is null)
        {
            return;
        }

        try
        {
            PixelBuffer? decoded = new HeaFile(entry).TryRender();
            if (decoded is null)
            {
                return;
            }

            layer.Replace(HeaLights.ToTileOverlay(decoded, _map.Width, _map.Height), entry.Name, dirty: false);
        }
        catch
        {
            // leave the overlay empty if the sheet will not decode
        }
    }

    private void PaintOverlayAt(int x, int y)
    {
        ActiveOverlay.Ensure(_map.Width, _map.Height);
        int radius = OverlayRadiusBox is null ? 3 : (int)OverlayRadiusBox.Value;
        int strength = OverlayStrengthBox is null ? 180 : (int)OverlayStrengthBox.Value;
        bool erase = OverlayEraseBox?.IsChecked == true;
        byte gray = (byte)Math.Clamp(strength, 1, 255);
        byte b = OverlayIsEffect ? (byte)80 : gray;
        byte g = OverlayIsEffect ? (byte)170 : gray;
        byte r = OverlayIsEffect ? (byte)255 : gray;
        IReadOnlyList<(int X, int Y)> dirty = ActiveOverlay.Paint(x, y, radius, strength, erase, b, g, r);
        if (dirty.Count == 0)
        {
            return;
        }

        foreach ((int dx, int dy) in dirty)
        {
            _dirtyTiles.Add((dx, dy));
        }

        _strokeChanged = true;
        QueueRedraw();
        Title = TitleText();
    }

    private void StampOverlayAt(int x, int y)
    {
        if (_pendingOverlayStamp is null)
        {
            return;
        }

        CommitSnapshot(CaptureSnapshot());
        ActiveOverlay.Ensure(_map.Width, _map.Height);
        int radius = OverlayRadiusBox is null ? 6 : Math.Max(2, (int)OverlayRadiusBox.Value * 2);
        IReadOnlyList<(int X, int Y)> dirty = ActiveOverlay.Stamp(_pendingOverlayStamp, x, y, radius);
        foreach ((int dx, int dy) in dirty)
        {
            _dirtyTiles.Add((dx, dy));
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            _pendingOverlayStamp = null;
        }

        MarkEdited();
        QueueRedraw();
        Title = TitleText();
        StatusText.Text = _pendingOverlayStamp is null
            ? $"Stamped effect at ({x},{y}). Hold Ctrl while clicking to stamp again."
            : $"Stamped at ({x},{y}). Click another tile, or Esc to cancel.";
    }

    private void OverlayImageClicked(object sender, RoutedEventArgs e)
    {
        OverlayModeBox?.SetCurrentValue(System.Windows.Controls.RadioButton.IsCheckedProperty, true);
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        PixelBuffer? pixels = WpfImages.TryDecodeImage(File.ReadAllBytes(dialog.FileName));
        if (pixels is null)
        {
            System.Windows.MessageBox.Show(this, "Could not read that image.", "Apply overlay");
            return;
        }

        ApplyEntireOverlay(pixels, Path.GetFileName(dialog.FileName));
    }

    private void OverlayEffectClicked(object sender, RoutedEventArgs e)
    {
        OverlayModeBox?.SetCurrentValue(System.Windows.Controls.RadioButton.IsCheckedProperty, true);
        EnsureEffectArchives();
        var items = new List<OverlaySourceItem>();
        foreach (IArchive archive in _heaArchives)
        {
            foreach (ArchiveEntry entry in archive.Entries)
            {
                if (OverlayGraphic.IsOverlaySource(entry.Name))
                {
                    items.Add(new OverlaySourceItem(entry.Name, archive, entry));
                }
            }
        }

        if (items.Count == 0)
        {
            System.Windows.MessageBox.Show(
                this,
                "No EFA, EPF, or HEA graphics were found next to this client's seo.dat. Add roh.dat or open a PNG with Apply image.",
                "Apply overlay");
            return;
        }

        var picker = new OverlaySourceWindow(items) { Owner = this };
        if (picker.ShowDialog() != true || picker.Pixels is null)
        {
            return;
        }

        if (picker.Mode == OverlayApplyMode.Stamp)
        {
            _pendingOverlayStamp = picker.Pixels;
            StatusText.Text = $"Click the map to stamp {picker.ChosenName}. Hold Ctrl to stamp more than once.";
            return;
        }

        ApplyEntireOverlay(picker.Pixels, picker.ChosenName);
    }

    private void ApplyEntireOverlay(PixelBuffer pixels, string? name)
    {
        CommitSnapshot(CaptureSnapshot());
        ActiveOverlay.Ensure(_map.Width, _map.Height);
        ActiveOverlay.ApplyImage(pixels, _map.Width, _map.Height, grayscale: !OverlayIsEffect);
        if (!string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(ActiveOverlay.FileName))
        {
            ActiveOverlay.FileName = HeaLights.PreferredFileName(_map.Name, OverlayIsEffect ? OverlayKind.Effect : OverlayKind.Light);
        }

        if (HeaLightsBox is not null)
        {
            HeaLightsBox.IsChecked = true;
        }

        MarkEdited();
        Redraw();
        Title = TitleText();
        StatusText.Text = OverlayIsEffect
            ? "Applied the graphic across the effect overlay. Save overlay writes fx#####.hea."
            : "Applied the graphic across the light overlay. Save overlay writes a numbered .hea mask.";
    }

    private void OverlayClearClicked(object sender, RoutedEventArgs e)
    {
        OverlayModeBox?.SetCurrentValue(System.Windows.Controls.RadioButton.IsCheckedProperty, true);
        CommitSnapshot(CaptureSnapshot());
        ActiveOverlay.Ensure(_map.Width, _map.Height);
        ActiveOverlay.Clear();
        _pendingOverlayStamp = null;
        MarkEdited();
        Redraw();
        Title = TitleText();
        StatusText.Text = OverlayIsEffect ? "Cleared the effect overlay." : "Cleared the light overlay.";
    }

    private void OverlaySaveClicked(object sender, RoutedEventArgs e)
    {
        string? message = SaveOverlays();
        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusText.Text = message;
            Title = TitleText();
        }
    }

    private string SaveOverlaysIfDirty(string prefix)
    {
        if (!OverlayDirty)
        {
            return prefix;
        }

        if (OverlaySaveBlockReason() is not null)
        {
            return prefix + "  Overlay was not written into the DAT (not a drop-in for this client).";
        }

        string? overlay = SaveOverlays();
        return string.IsNullOrWhiteSpace(overlay) ? prefix : prefix + "  " + overlay;
    }

    private string? SaveOverlays()
    {
        string? blocked = OverlaySaveBlockReason();
        if (!string.IsNullOrWhiteSpace(blocked))
        {
            System.Windows.MessageBox.Show(this, blocked, "Save overlay");
            return null;
        }

        string? target = OverlaySavePath();
        if (string.IsNullOrWhiteSpace(target) || !DatSave.IsWritable(target))
        {
            System.Windows.MessageBox.Show(
                this,
                "Overlays save into seo.dat (or the unpacked graphics folder) for this client. That path is missing or read-only.",
                "Save overlay");
            return null;
        }

        var files = new List<(string Name, byte[] Data)>();
        if (_lightOverlay.HasPixels || _lightOverlay.Dirty)
        {
            _lightOverlay.Ensure(_map.Width, _map.Height);
            files.Add((
                _lightOverlay.FileName ?? HeaLights.PreferredFileName(_map.Name, OverlayKind.Light),
                _lightOverlay.ToHeaBytes()));
        }

        if (_effectOverlay.HasPixels || _effectOverlay.Dirty)
        {
            _effectOverlay.Ensure(_map.Width, _map.Height);
            files.Add((
                _effectOverlay.FileName ?? HeaLights.PreferredFileName(_map.Name, OverlayKind.Effect),
                _effectOverlay.ToHeaBytes()));
        }

        if (files.Count == 0)
        {
            StatusText.Text = "Both overlays are empty, so nothing was written.";
            return "No overlay to save.";
        }

        try
        {
            DatSave.Write(_cache, target, files);
            _lightOverlay.Dirty = false;
            _effectOverlay.Dirty = false;
            return $"Saved overlay {string.Join(", ", files.Select(f => f.Name))} into {Path.GetFileName(target)}.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Save overlay");
            return null;
        }
    }

    private string? OverlaySavePath()
    {
        if (DatSave.IsWritable(_assets.Seo.SourcePath))
        {
            return _assets.Seo.SourcePath;
        }

        string? dir = Path.GetDirectoryName(_assets.Seo.SourcePath);
        if (string.IsNullOrWhiteSpace(dir))
        {
            return null;
        }

        string seo = Path.Combine(dir, "seo.dat");
        return DatSave.IsWritable(seo) ? seo : dir;
    }

    private void EnsureEffectArchives()
    {
        if (_effectArchivesReady)
        {
            return;
        }

        _effectArchivesReady = true;
        string? dir = Path.GetDirectoryName(_assets.Seo.SourcePath);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            return;
        }

        foreach (string name in new[] { "roh.dat", "Roh.dat" })
        {
            string path = Path.Combine(dir, name);
            if (!File.Exists(path)
                || _heaArchives.Any(a => string.Equals(a.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            try
            {
                if (_cache is not null)
                {
                    var catalog = new CatalogEntry
                    {
                        SourcePath = path,
                        DisplayName = Path.GetFileName(path),
                        Role = ArchiveRole.Effects
                    };
                    _heaArchives.Add(_cache.Get(catalog));
                }
                else
                {
                    var archive = new DataArchive(path);
                    _overlayOwned.Add(archive);
                    _heaArchives.Add(archive);
                }
            }
            catch
            {
                // skip unreadable packs
            }
        }
    }

    private enum PaintLayer
    {
        Floor,
        LeftWall,
        RightWall,
        Structure
    }

    private sealed record SizeChoice(int Width, int Height)
    {
        public override string ToString() => $"{Width} × {Height}";
    }

    private void BackClicked(object sender, RoutedEventArgs e)
    {
        if (_back.Count == 0 || !ConfirmDiscard())
        {
            return;
        }

        MapVisit visit = _back.Pop();
        UpdateBackButton();
        _restoringVisit = true;
        try
        {
            _pendingLanding = visit.X >= 0 && visit.Y >= 0 ? (visit.X, visit.Y) : null;
            if (visit.Packed)
            {
                IArchive? packed = _packedMaps.FirstOrDefault(a =>
                    string.Equals(a.SourcePath, visit.PackedSource, StringComparison.OrdinalIgnoreCase)
                    && a.GetEntry(visit.Key) is not null);
                if (packed is not null)
                {
                    OpenPacked(packed, visit.Key);
                    return;
                }
            }

            if (!string.IsNullOrWhiteSpace(visit.Key) && File.Exists(visit.Key))
            {
                OpenPath(visit.Key);
                return;
            }

            if (TryFindMap(visit.MapId, visit.Key, out MapListItem? item) && item is not null)
            {
                OpenItem(item);
            }
        }
        finally
        {
            _restoringVisit = false;
        }
    }

    private void CheckWarpsClicked(object sender, RoutedEventArgs e)
    {
        Busy.Run(this, "Checking warps…", () =>
        {
            var window = new WarpCheckWindow(
                _warps,
                _workspace,
                _packedMaps,
                w => TryShowWarp(w),
                w => Travel(w),
                _mapNames)
            {
                Owner = this
            };
            window.Show();
        });
    }

    private void SotpClicked(object sender, RoutedEventArgs e)
    {
        Busy.Run(this, "Opening walk flags…", () =>
        {
            var window = new SotpEditorWindow(_assets, _cache, _workspace?.ActiveClient) { Owner = this };
            window.Show();
        });
    }

    private void LightEditorClicked(object sender, RoutedEventArgs e)
    {
        if (Owner is MainWindow main)
        {
            main.OpenLightEditor();
            return;
        }

        if (_cache is null)
        {
            System.Windows.MessageBox.Show(this, "Load a client first.", "Light");
            return;
        }
    }

    private void CopyTileClicked(object sender, RoutedEventArgs e)
    {
        int x = _lastStatusX == int.MinValue ? 0 : _lastStatusX;
        int y = _lastStatusY == int.MinValue ? 0 : _lastStatusY;
        string text = $"{x}, {y}";
        try
        {
            System.Windows.Clipboard.SetText(text);
            StatusText.Text = $"Copied {text}";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void JumpIdClicked(object sender, RoutedEventArgs e) => JumpToIdText();

    private void JumpIdKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            JumpToIdText();
            e.Handled = true;
        }
    }

    private void JumpToIdText()
    {
        if (!int.TryParse(JumpIdBox?.Text.Trim(), out int id) || id <= 0)
        {
            StatusText.Text = "Enter a map id.";
            return;
        }

        if (!ConfirmDiscard())
        {
            return;
        }

        if (!TryFindMap(id, "", out MapListItem? item) || item is null)
        {
            StatusText.Text = $"Map {id} is not in this client.";
            return;
        }

        OpenItem(item);
    }

    private void RecentMapChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingRecent || RecentMapBox.SelectedItem is not MapListItem item || !ConfirmDiscard())
        {
            return;
        }

        OpenItem(item);
    }

    private void PaletteCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _page = 0;
        LoadPalettePage();
    }

    private void RememberVisit()
    {
        if (_restoringVisit)
        {
            return;
        }

        string key = CurrentVisitKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (_back.Count > 0 && string.Equals(_back.Peek().Key, key, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _back.Push(new MapVisit(
            key,
            _packedSourcePath,
            CurrentMapId(),
            _lastStatusX == int.MinValue ? 0 : _lastStatusX,
            _lastStatusY == int.MinValue ? 0 : _lastStatusY,
            _openedFromArchive));
        if (_back.Count > 40)
        {
            var keep = _back.Reverse().Take(40).Reverse().ToList();
            _back.Clear();
            foreach (MapVisit visit in keep)
            {
                _back.Push(visit);
            }
        }

        UpdateBackButton();
    }

    private string CurrentVisitKey() =>
        _openedFromArchive && !string.IsNullOrWhiteSpace(_packedEntryName)
            ? _packedEntryName
            : _map.Name;

    private void UpdateBackButton()
    {
        if (BackButton is not null)
        {
            BackButton.IsEnabled = _back.Count > 0;
        }
    }

    private void NoteOpenedMap()
    {
        RefreshAreaInfo();
        int id = CurrentMapId();
        if (id > 0)
        {
            RecentMaps.Touch(_workspace?.ActiveClient, id);
            RefreshRecentMaps();
        }
    }

    private void RefreshRecentMaps()
    {
        if (RecentMapBox is null)
        {
            return;
        }

        _syncingRecent = true;
        var items = new List<MapListItem>();
        foreach (int id in RecentMaps.Load(_workspace?.ActiveClient))
        {
            if (TryFindMap(id, "", out MapListItem? item) && item is not null)
            {
                items.Add(item);
            }
        }

        RecentMapBox.ItemsSource = items;
        RecentMapBox.SelectedIndex = -1;
        _syncingRecent = false;
    }

    private void RefreshAreaInfo()
    {
        string text = AreaCaption.Format(
            CurrentMapId(),
            _mapNames.Area(CurrentMapId()),
            _mapNames,
            _map.Name,
            _map.Width,
            _map.Height,
            WantServerOverlay ? _serverWorld.Get(CurrentMapId()) : null);
        if (AreaInfoText is not null)
        {
            AreaInfoText.Text = text;
        }

        if (AreaOverlayText is not null)
        {
            AreaOverlayText.Text = text;
        }

        if (AreaOverlay is not null)
        {
            AreaOverlay.Visibility = Visibility.Visible;
        }
    }

    private void LayoutWarpLabels()
    {
        if (WarpLabelLayer is null)
        {
            return;
        }

        WarpLabelLayer.Children.Clear();
        if (WarpLabelsBox?.IsChecked != true)
        {
            return;
        }

        int sourceId = CurrentMapId();
        if (WarpsBox?.IsChecked == true)
        {
            AddWarpLabels(_warps.For(_map.Name), sourceId, System.Windows.Media.Color.FromArgb(200, 37, 40, 48));
        }

        if (WantServerOverlay)
        {
            AddWarpLabels(
                _serverWorld.Warps(_map.Name),
                sourceId,
                System.Windows.Media.Color.FromArgb(200, 88, 32, 72));
        }
    }

    private void AddWarpLabels(IReadOnlyList<MapWarp> warps, int sourceId, System.Windows.Media.Color background)
    {
        foreach (MapWarp warp in warps)
        {
            (int x, int y) = (Math.Min(warp.X1, warp.X2), Math.Min(warp.Y1, warp.Y2));
            if ((uint)x >= (uint)_map.Width || (uint)y >= (uint)_map.Height)
            {
                continue;
            }

            (int sx, int sy) = MapAssets.TileToScreen(x, y, _mapView.OriginX, _mapView.OriginY, _mapView.StepX, _mapView.StepY);
            var label = new TextBlock
            {
                Text = WarpHoverTip.ToLine(warp, _mapNames, _warps, sourceId),
                Foreground = System.Windows.Media.Brushes.White,
                Background = new SolidColorBrush(background),
                FontSize = 11,
                Padding = new Thickness(3, 1, 3, 1)
            };
            Canvas.SetLeft(label, sx);
            Canvas.SetTop(label, Math.Max(0, sy - 2));
            WarpLabelLayer.Children.Add(label);
        }
    }

    private void BindPaletteCategories()
    {
        if (PaletteCategoryBox is null)
        {
            return;
        }

        var items = new List<string> { "All" };
        if (Layer is PaintLayer.LeftWall or PaintLayer.RightWall)
        {
            items.Add("Tab block (15)");
            items.Add("Any block");
            items.Add("Overlay (0x80)");
            items.Add("Walkable (0)");
            items.AddRange(_wallFamilies.Select(f => f.Name));
        }
        else if (Layer == PaintLayer.Floor)
        {
            items.AddRange(_floorFamilies.Select(f => f.Name));
        }

        PaletteCategoryBox.ItemsSource = items;
        PaletteCategoryBox.SelectedIndex = 0;
        PaletteCategoryBox.Visibility = Layer == PaintLayer.Structure ? Visibility.Collapsed : Visibility.Visible;
    }

    private IReadOnlyList<int> FilterFloorIds(IReadOnlyList<int> ids)
    {
        string cat = PaletteCategoryBox?.SelectedItem as string ?? "All";
        if (cat == "All")
        {
            return ids;
        }

        return ids.Where(id => FloorFamilies.Find(id + FloorAtlasOffset(), _floorFamilies) is FloorFamily f && f.Name == cat).ToList();
    }

    private IReadOnlyList<int> FilterWallIds(IReadOnlyList<int> ids)
    {
        string cat = PaletteCategoryBox?.SelectedItem as string ?? "All";
        if (cat == "All")
        {
            return ids;
        }

        return cat switch
        {
            "Tab block (15)" => ids.Where(id => _assets.SotpFlags(id) == 15).ToList(),
            "Any block" => ids.Where(id => _assets.SotpFlags(id) != 0).ToList(),
            "Overlay (0x80)" => ids.Where(id => (_assets.SotpFlags(id) & 0x80) == 0x80).ToList(),
            "Walkable (0)" => ids.Where(id => _assets.SotpFlags(id) == 0).ToList(),
            _ => ids.Where(id => WallFamilies.Find(id, _wallFamilies) is WallFamily f && f.Name == cat).ToList()
        };
    }

    private void LogMapSave(string name, byte[] data)
    {
        ClientProfile? client = _workspace?.ActiveClient;
        ClientSnapshots.SaveMap(client, name, data);
        ClientChangeLog.Append(client, "map", $"Saved {Path.GetFileName(name)} ({_map.Width}×{_map.Height}).");
    }

    private void AutosaveTick()
    {
        if (!_dirty)
        {
            return;
        }

        try
        {
            byte[] data = _map.ToBytes();
            string name = string.IsNullOrWhiteSpace(_map.Name) ? "untitled.map" : _map.Name;
            ClientSnapshots.SaveMap(_workspace?.ActiveClient, name, data);
            ClientChangeLog.Append(_workspace?.ActiveClient, "autosave", $"Snapshot {Path.GetFileName(name)} ({_map.Width}×{_map.Height}).");
            StatusText.Text = $"Autosaved snapshot of {Path.GetFileName(name)}.";
        }
        catch
        {
            // keep editing
        }
    }

    private readonly record struct MapVisit(string Key, string? PackedSource, int MapId, int X, int Y, bool Packed);

    private sealed record MapListItem(string Name, string Path, IArchive? Archive, string EntryName);

    private sealed class TileChoice : INotifyPropertyChanged
    {
        private bool _selected;

        public TileChoice(int id, string label, BitmapSource image, bool selected)
        {
            Id = id;
            Label = label;
            Image = image;
            _selected = selected;
        }

        public int Id { get; }
        public string Label { get; }
        public BitmapSource Image { get; }
        public System.Windows.Media.Brush Border => new SolidColorBrush(_selected
            ? System.Windows.Media.Color.FromRgb(110, 168, 254)
            : System.Windows.Media.Color.FromRgb(58, 63, 75));

        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value)
                {
                    return;
                }

                _selected = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Border));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
