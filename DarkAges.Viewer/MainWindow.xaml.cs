using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DarkAges.Formats.Catalog;
using DarkAges.Formats.Data;
using DarkAges.Formats.Drawing;
using DarkAges.Formats.Imaging;
using DarkAges.Formats.IO;
using DarkAges.Viewer.Imaging;
using DarkAges.Formats.Maps;
#if LOCAL_UNITY
using DarkAges.Viewer.Export;
#endif
using DarkAges.Viewer.Maps;
using DarkAges.Viewer.Preview;
using DarkAges.Viewer.Services;
using Microsoft.Win32;
using NAudio.Wave;
using WinForms = System.Windows.Forms;

namespace DarkAges.Viewer;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly AssetWorkspace _workspace;
    private readonly ObservableCollection<CatalogEntry> _archives = new();
    private readonly DispatcherTimer _timer;
    private ArchiveCache? _cache;
    private PreviewResult? _preview;
    private int _frame;
    private bool _playing = true;
    private bool _syncingFrame;
    private IWavePlayer? _player;
    private WaveStream? _wave;
    private WriteableBitmap? _cinematicBmp;
    private PixelBuffer? _cinematicPixels;
    private EntryGallery? _gallery;
    private CancellationTokenSource? _galleryCts;
    private bool _ignoreGalleryToggle;
    private bool _ignorePageSize;
    private bool _galleryPageSizePicked;
    private List<ArchiveEntry> _entrySource = [];
    private int _entryPage;
    private int _entryPageSize = 200;
    private bool _ignoreEntryPageSize;
    private static readonly int[] EntryPageSizeChoices = [50, 100, 200, 500];
    private List<MapAssets> _mapTilesets = [];
    private WarpTable _warps = WarpTable.Empty;
    private readonly ServerWorld _serverWorld = new();
    private MapNameIndex _mapNames = new();
    private readonly List<MapEditorWindow> _mapEditors = [];
    private WarpEditorWindow? _warpEditor;
    private ItemEditorWindow? _itemEditor;
    private MonsterEditorWindow? _monsterEditor;
    private PaletteEditorWindow? _paletteEditor;
    private MetaEditorWindow? _metaEditor;
    private LightEditorWindow? _lightEditor;
    private GraphicEditorWindow? _graphicEditor;
    private ConvertWindow? _convertWindow;
    private TileSplicerWindow? _splicerWindow;
    private CharacterAssemblerWindow? _characterAssembler;
    private MusicBrowserWindow? _musicBrowser;
    private bool _panning;
    private bool _previewPressed;
    private (int X, int Y)? _pendingViewerLanding;
    private readonly Stack<(CatalogEntryView Catalog, string Entry, int? Frame, int X, int Y)> _viewerBack = new();
    private bool _restoringViewer;
    private double _previewZoom = 1;
    private bool _syncingZoom;
    private bool _syncingClient;
    private bool _suppressAutoGallery;
    private string? _colorVariantKey;
    private System.Windows.Point _panStart;
    private double _panOffsetX;
    private double _panOffsetY;

    private bool GalleryVisible => GalleryPanel?.Visibility == Visibility.Visible && _gallery is not null;

    public MainWindow()
    {
        InitializeComponent();
        Title = AppInfo.Name;
        UpdateCaptionButtons();
        _workspace = new AssetWorkspace(_settings);
        WorkspaceBox.ItemsSource = new[]
        {
            "All", "Mobs", "Walls", "Floors", "Maps", "Metadata", "UI", "Items", "Effects", "Players / gear", "Cinematics", "Other"
        };
        WorkspaceBox.SelectedIndex = 0;
        MotionBox.ItemsSource = new[] { "All", "Idle", "Walk", "Attack", "Attack2", "Attack3" };
        MotionBox.SelectedIndex = 0;
        ZoomBox.ItemsSource = ViewZoom.Presets;
        ZoomBox.SelectedIndex = ViewZoom.ClosestPresetIndex(1);
        ArchiveList.ItemsSource = _archives;
        if (GalleryPageSizeBox is not null)
        {
            _ignorePageSize = true;
            GalleryPageSizeBox.ItemsSource = EntryGallery.PageSizeChoices;
            GalleryPageSizeBox.SelectedItem = 50;
            _ignorePageSize = false;
        }

        if (EntryPageSizeBox is not null)
        {
            _ignoreEntryPageSize = true;
            EntryPageSizeBox.ItemsSource = EntryPageSizeChoices;
            EntryPageSizeBox.SelectedItem = 200;
            _ignoreEntryPageSize = false;
        }
        ICollectionView view = CollectionViewSource.GetDefaultView(_archives);
        view.GroupDescriptions.Add(new PropertyGroupDescription("RoleLabel"));
        view.Filter = ArchiveFilter;
#if LOCAL_UNITY
        var unityItem = new MenuItem
        {
            Header = "Export Unity…",
            ToolTip = "Pack DATs, maps, and metadata into a Unity-ready folder of atlases and JSON"
        };
        unityItem.Click += UnityExportClicked;
        MoreMenu.Items.Insert(MoreMenu.Items.IndexOf(AboutSeparator), unityItem);
#endif

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += (_, _) =>
        {
            try
            {
                if (GalleryVisible && _playing)
                {
                    _gallery!.Advance();
                    UpdateGalleryFrameSlider();
                    return;
                }

                if (_preview?.Cinematic is not null)
                {
                    AdvanceCinematic();
                    return;
                }

                if (_playing && ContactSheetBox?.IsChecked != true && _preview is { CanAnimate: true, FrameCount: > 1 })
                {
                    _frame = (_frame + 1) % _preview.FrameCount;
                    RefreshPreview(showBusy: false);
                }
            }
            catch (Exception ex)
            {
                _playing = false;
                StatusText.Text = ex.Message;
            }
        };
        _timer.Start();

        Loaded += (_, _) =>
        {
            CollectionViewSource.GetDefaultView(_archives)?.Refresh();
            FillEntries();
        };
        Closed += (_, _) =>
        {
            _galleryCts?.Cancel();
            _itemEditor?.Close();
            _monsterEditor?.Close();
            _warpEditor?.Close();
            _lightEditor?.Close();
            CloseMapEditors();
            StopAudio();
            BinkDecoder.DisposeCache();
            _cache?.Dispose();
            _settings.Save();
        };

        FFmpegLocator.Prefer(_settings.FfmpegPath);
        BinkEncoderLocator.Prefer(_settings.RadVideoPath);
        FillClientBox();
        string? remembered = FirstExisting(_settings.ActiveClient, _settings.RootFolder);
        if (remembered is not null)
        {
            LoadRoot(remembered);
        }
        else
        {
            StatusText.Text = "Browse to a client folder to load its DATs, maps, and metadata.";
        }
    }

    private void MinimizeClicked(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxRestoreClicked(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseClicked(object sender, RoutedEventArgs e) => Close();

    private void WindowStateChanged(object sender, EventArgs e)
    {
        RootChrome.Margin = WindowState == WindowState.Maximized ? new Thickness(8) : new Thickness(0);
        UpdateCaptionButtons();
    }

    private void UpdateCaptionButtons()
    {
        if (MaxRestoreButton is null)
        {
            return;
        }

        bool maximized = WindowState == WindowState.Maximized;
        MaxRestoreButton.Content = maximized ? "\uE923" : "\uE922";
        MaxRestoreButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    private void BrowseClicked(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Select a full Dark Ages client folder (DAT, maps, metadata)",
            UseDescriptionForTitle = true
        };
        string current = CurrentRoot;
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
        {
            dialog.SelectedPath = current;
        }

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            _workspace.AddClient(dialog.SelectedPath);
            ReloadWorkspace();
        }
    }

    private void AddFolderClicked(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Add another full client folder. It is loaded alone, not mixed with the current version.",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            _workspace.AddClient(dialog.SelectedPath);
            ReloadWorkspace();
        }
    }

    private void ClientChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingClient || ClientBox.SelectedItem is not ClientProfile client)
        {
            return;
        }

        string full = Path.GetFullPath(client.Path);
        string current = _settings.ActiveClient;
        if (!string.IsNullOrWhiteSpace(current)
            && string.Equals(Path.GetFullPath(current), full, StringComparison.OrdinalIgnoreCase)
            && _archives.Count > 0)
        {
            return;
        }

        LoadRoot(client.Path);
    }

    private string CurrentRoot
    {
        get
        {
            if (ClientBox?.SelectedItem is ClientProfile selected && !string.IsNullOrWhiteSpace(selected.Path))
            {
                return Path.GetFullPath(selected.Path);
            }

            if (!string.IsNullOrWhiteSpace(_settings.ActiveClient) && Directory.Exists(_settings.ActiveClient))
            {
                return Path.GetFullPath(_settings.ActiveClient);
            }

            if (!string.IsNullOrWhiteSpace(_settings.RootFolder) && Directory.Exists(_settings.RootFolder))
            {
                return Path.GetFullPath(_settings.RootFolder);
            }

            return "";
        }
    }

    private void FillClientBox()
    {
        if (ClientBox is null)
        {
            return;
        }

        _syncingClient = true;
        try
        {
            var clients = _workspace.Clients.ToList();
            string want = _settings.ActiveClient;
            if (string.IsNullOrWhiteSpace(want))
            {
                want = _settings.RootFolder;
            }

            ClientBox.ItemsSource = clients;
            ClientProfile? match = null;
            if (!string.IsNullOrWhiteSpace(want) && Directory.Exists(want))
            {
                string full = Path.GetFullPath(want);
                match = clients.FirstOrDefault(c =>
                    string.Equals(Path.GetFullPath(c.Path), full, StringComparison.OrdinalIgnoreCase));
            }

            ClientBox.SelectedItem = match ?? clients.FirstOrDefault();
        }
        finally
        {
            _syncingClient = false;
        }
    }

    private void ReloadClicked(object sender, RoutedEventArgs e)
    {
        ReloadWorkspace();
    }

    private void SearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            RunSearch();
        }
        else if (e.Key == Key.Escape && SearchList is not null)
        {
            SearchList.Visibility = Visibility.Collapsed;
        }
    }

    private void SearchResultKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            JumpToSearch(SearchList.SelectedItem as SearchHit);
        }
    }

    private void SearchResultActivated(object sender, MouseButtonEventArgs e) =>
        JumpToSearch(SearchList.SelectedItem as SearchHit);

    private void RunSearch()
    {
        if (_cache is null || SearchBox is null)
        {
            return;
        }

        string query = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(query))
        {
            SearchList.Visibility = Visibility.Collapsed;
            return;
        }

        bool numeric = int.TryParse(query, out int id);
        bool wantFloors = numeric
            || query.Contains("floor", StringComparison.OrdinalIgnoreCase)
            || query.Contains("tilea", StringComparison.OrdinalIgnoreCase);
        var hits = new List<SearchHit>();
        foreach (CatalogEntryView catalog in _archives)
        {
            if (hits.Count >= 200)
            {
                break;
            }

            try
            {
                IArchive archive = _cache.Get(catalog);
                IEnumerable<ArchiveEntry> entries = IconSheets.Expand(archive.Entries);
                if (wantFloors && catalog.Role == ArchiveRole.Floors)
                {
                    entries = FloorTiles.Expand(entries);
                }

                foreach (ArchiveEntry entry in entries)
                {
                    if (hits.Count >= 200)
                    {
                        break;
                    }

                    if (catalog.Role == ArchiveRole.Metadata && MetaFile.IsKnownName(entry.Name) && MetaFile.LooksLike(entry))
                    {
                        if (MatchesSearch(entry, catalog, query, numeric, id))
                        {
                            hits.Add(new SearchHit(catalog, entry.Name, entry.FrameIndex, $"{catalog.Title} / {entry.DisplayName}"));
                        }

                        if (MetaFile.IsMapTable(entry.Name))
                        {
                            continue;
                        }

                        try
                        {
                            MetaFile meta = MetaFile.Load(entry, archive.SourcePath);
                            foreach (MetaNode node in meta.Nodes)
                            {
                                if (hits.Count >= 200)
                                {
                                    break;
                                }

                                if (node.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                                    || node.Properties.Any(p => p.Contains(query, StringComparison.OrdinalIgnoreCase)))
                                {
                                    hits.Add(new SearchHit(catalog, entry.Name, null, $"{entry.Name} / {node.Name}"));
                                }
                            }
                        }
                        catch
                        {
                            // skip unreadable meta
                        }

                        continue;
                    }

                    if (!MatchesSearch(entry, catalog, query, numeric, id))
                    {
                        continue;
                    }

                    hits.Add(new SearchHit(catalog, entry.Name, entry.FrameIndex, $"{catalog.Title} / {entry.DisplayName}"));
                }
            }
            catch
            {
                // skip archives that fail to open
            }
        }

        SearchList.ItemsSource = hits;
        SearchList.Visibility = hits.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = hits.Count == 0 ? $"No matches for \"{query}\"." : $"{hits.Count} matches (double-click to open).";
        if (hits.Count > 0)
        {
            SearchList.SelectedIndex = 0;
            SearchList.Focus();
        }
    }

    private static bool MatchesSearch(ArchiveEntry entry, CatalogEntry catalog, string query, bool numeric, int id)
    {
        if (entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || catalog.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!numeric)
        {
            return false;
        }

        if (entry.FrameIndex is int frame && frame + 1 == id)
        {
            return true;
        }

        string digits = new string(Path.GetFileNameWithoutExtension(entry.Name).Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out int n) && n == id;
    }

    private void JumpToSearch(SearchHit? hit)
    {
        if (hit is null)
        {
            return;
        }

        JumpToEntry(hit.Archive, hit.EntryName, hit.FrameIndex, openGallery: false);
        SearchList.Visibility = Visibility.Collapsed;
    }

    private void FavoriteClicked(object sender, RoutedEventArgs e)
    {
        if (GalleryVisible
            || ArchiveList.SelectedItem is not CatalogEntryView catalog
            || EntryList.SelectedItem is not ArchiveEntry entry)
        {
            return;
        }

        FavoriteEntry fav = MakeFavorite(catalog, entry);
        List<FavoriteEntry> list = CurrentFavorites();
        FavoriteEntry? existing = list.FirstOrDefault(f => f.SameAs(fav));
        if (existing is not null)
        {
            list.Remove(existing);
            StatusText.Text = $"Removed {fav.Label} from favorites.";
        }
        else
        {
            list.Insert(0, fav);
            if (list.Count > 500)
            {
                list.RemoveRange(500, list.Count - 500);
            }

            StatusText.Text = $"Saved {fav.Label} to favorites.";
        }

        _settings.Save();
        RefreshFavoriteList();
        UpdateActionBar();
    }

    private void FavoriteActivated(object sender, MouseButtonEventArgs e) =>
        OpenFavorite(FavoriteList.SelectedItem as FavoriteEntry);

    private void FavoriteKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OpenFavorite(FavoriteList.SelectedItem as FavoriteEntry);
            return;
        }

        if (e.Key == Key.Delete)
        {
            e.Handled = true;
            RemoveFavorite(FavoriteList.SelectedItem as FavoriteEntry);
        }
    }

    private void RemoveFavoriteClicked(object sender, RoutedEventArgs e) =>
        RemoveFavorite(FavoriteList.SelectedItem as FavoriteEntry);

    private void OpenFavorite(FavoriteEntry? favorite)
    {
        if (favorite is null)
        {
            return;
        }

        CatalogEntryView? catalog = FindFavoriteArchive(favorite);
        if (catalog is null)
        {
            StatusText.Text = $"Could not find archive for {favorite.Label}.";
            return;
        }

        JumpToEntry(catalog, favorite.EntryName, favorite.FrameIndex, openGallery: false);
        StatusText.Text = favorite.Label;
    }

    private void RemoveFavorite(FavoriteEntry? favorite)
    {
        if (favorite is null)
        {
            return;
        }

        List<FavoriteEntry> list = CurrentFavorites();
        if (list.RemoveAll(f => f.SameAs(favorite)) <= 0)
        {
            return;
        }

        _settings.Save();
        RefreshFavoriteList();
        UpdateActionBar();
        StatusText.Text = $"Removed {favorite.Label} from favorites.";
    }

    private void RefreshFavoriteList()
    {
        if (FavoriteList is null)
        {
            return;
        }

        List<FavoriteEntry> list = CurrentFavorites();
        FavoriteList.ItemsSource = list.ToList();
        bool any = list.Count > 0;
        FavoriteList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (FavoriteHeader is not null)
        {
            FavoriteHeader.Text = any ? $"Favorites ({list.Count})" : "Favorites";
        }
    }

    private List<FavoriteEntry> CurrentFavorites()
    {
        ClientProfile? client = _workspace.ActiveClient;
        if (client is null)
        {
            return [];
        }

        return client.Favorites ??= [];
    }

    private static FavoriteEntry MakeFavorite(CatalogEntryView catalog, ArchiveEntry entry) => new()
    {
        SourcePath = catalog.SourcePath,
        Role = catalog.Role,
        IsFolder = catalog.IsFolder,
        EntryName = entry.Name,
        FrameIndex = entry.FrameIndex,
        Label = $"{catalog.Title} / {entry.DisplayName}"
    };

    private CatalogEntryView? FindFavoriteArchive(FavoriteEntry favorite)
    {
        CatalogEntryView? exact = _archives.OfType<CatalogEntryView>().FirstOrDefault(a =>
            string.Equals(a.SourcePath, favorite.SourcePath, StringComparison.OrdinalIgnoreCase)
            && a.Role == favorite.Role
            && a.IsFolder == favorite.IsFolder);
        return exact ?? _archives.OfType<CatalogEntryView>().FirstOrDefault(a =>
            string.Equals(a.SourcePath, favorite.SourcePath, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsFavorite(CatalogEntryView catalog, ArchiveEntry entry)
    {
        FavoriteEntry fav = MakeFavorite(catalog, entry);
        return CurrentFavorites().Any(f => f.SameAs(fav));
    }

    private ArchiveEntry? FindListedEntry(string entryName, int? frameIndex) =>
        _entrySource.FirstOrDefault(x =>
            x.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)
            && x.FrameIndex == frameIndex);

    private void JumpToEntry(CatalogEntryView catalog, string entryName, int? frameIndex, bool openGallery)
    {
        _suppressAutoGallery = !openGallery;
        try
        {
            if (WorkspaceBox is not null)
            {
                WorkspaceBox.SelectedItem = "All";
            }

            if (ArchiveFilterBox is not null && !string.IsNullOrWhiteSpace(ArchiveFilterBox.Text))
            {
                ArchiveFilterBox.Text = "";
            }

            if (EntryFilterBox is not null && !string.IsNullOrWhiteSpace(EntryFilterBox.Text))
            {
                EntryFilterBox.Text = "";
            }

            ArchiveList.SelectedItem = catalog;
            ArchiveList.ScrollIntoView(catalog);
            FillEntries();
            if (!openGallery && GalleryBox is not null)
            {
                _ignoreGalleryToggle = true;
                GalleryBox.IsChecked = false;
                _ignoreGalleryToggle = false;
                HideGallery(updateBar: false);
            }

            ArchiveEntry? match = FindListedEntry(entryName, frameIndex);
            if (match is null)
            {
                string ext = Path.GetExtension(entryName);
                if (ext.Equals(".pal", StringComparison.OrdinalIgnoreCase) && HidePalBox?.IsChecked == true)
                {
                    HidePalBox.IsChecked = false;
                    match = FindListedEntry(entryName, frameIndex);
                }
                else if ((ext.Equals(".tbl", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                    && HideTableBox?.IsChecked == true)
                {
                    HideTableBox.IsChecked = false;
                    match = FindListedEntry(entryName, frameIndex);
                }
            }

            if (match is not null)
            {
                RevealListed(match);
            }
            else
            {
                StatusText.Text = $"Entry {entryName} was not in {catalog.Title}.";
            }
        }
        finally
        {
            _suppressAutoGallery = false;
            UpdateActionBar();
        }
    }

    private void OpenButtonMenu(object sender, RoutedEventArgs e) => ButtonMenu.Open(sender);

    private async void UnpackClicked(object sender, RoutedEventArgs e)
    {
        string? datPath = null;
        if (ArchiveList.SelectedItem is CatalogEntryView selected
            && !selected.IsFolder
            && selected.SourcePath.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)
            && File.Exists(selected.SourcePath))
        {
            datPath = selected.SourcePath;
        }
        else
        {
            var picker = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "DAT archives|*.dat|All files|*.*",
                Title = "Unpack DAT"
            };
            if (!string.IsNullOrWhiteSpace(CurrentRoot) && Directory.Exists(CurrentRoot))
            {
                picker.InitialDirectory = CurrentRoot;
            }

            if (picker.ShowDialog() != true)
            {
                return;
            }

            datPath = picker.FileName;
        }

        string dest = DatPacker.DefaultUnpackDirectory(datPath);
        using (var folder = new WinForms.FolderBrowserDialog
        {
            Description = "Folder to unpack into",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(dest) ? dest : Path.GetDirectoryName(datPath) ?? ""
        })
        {
            if (folder.ShowDialog() != WinForms.DialogResult.OK)
            {
                return;
            }

            dest = folder.SelectedPath;
        }

        if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
        {
            var confirm = System.Windows.MessageBox.Show(
                $"Unpack into\n{dest}\n\nExisting files with the same names will be overwritten.",
                "Unpack DAT",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK)
            {
                return;
            }
        }

        try
        {
            await Busy.RunAsync(this, "Unpacking DAT…", async () =>
            {
                var progress = new Progress<string>(msg =>
                {
                    StatusText.Text = msg;
                    Busy.Message(this, msg);
                });
                int count = await Task.Run(() => DatPacker.Unpack(datPath, dest, progress));
                StatusText.Text = $"Unpacked {count} files to {dest}";
            });
            ReloadWorkspace();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Unpack failed");
        }
    }

    private async void RepackClicked(object sender, RoutedEventArgs e)
    {
        string? folderPath = null;
        if (ArchiveList.SelectedItem is CatalogEntryView selected
            && selected.IsFolder
            && selected.Role is not ArchiveRole.Maps and not ArchiveRole.Metadata
            && Directory.Exists(selected.SourcePath))
        {
            folderPath = selected.SourcePath;
        }
        else
        {
            using var folder = new WinForms.FolderBrowserDialog
            {
                Description = "Folder of files to pack into a DAT",
                UseDescriptionForTitle = true
            };
            if (ArchiveList.SelectedItem is CatalogEntryView cat && Directory.Exists(cat.SourcePath))
            {
                folder.SelectedPath = cat.IsFolder ? cat.SourcePath : (Path.GetDirectoryName(cat.SourcePath) ?? "");
            }
            else if (!string.IsNullOrWhiteSpace(CurrentRoot) && Directory.Exists(CurrentRoot))
            {
                folder.SelectedPath = CurrentRoot;
            }

            if (folder.ShowDialog() != WinForms.DialogResult.OK)
            {
                return;
            }

            folderPath = folder.SelectedPath;
        }

        if (Directory.EnumerateFiles(folderPath, "*.map").Take(3).Count() >= 3)
        {
            System.Windows.MessageBox.Show("That folder looks like maps, not DAT contents. Pick an unpacked DAT folder.");
            return;
        }

        var save = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "DAT archives|*.dat|All files|*.*",
            Title = "Repack DAT",
            FileName = Path.GetFileName(DatPacker.DefaultPackPath(folderPath)),
            InitialDirectory = Path.GetDirectoryName(DatPacker.DefaultPackPath(folderPath))
        };
        if (save.ShowDialog() != true)
        {
            return;
        }

        if (File.Exists(save.FileName))
        {
            var confirm = System.Windows.MessageBox.Show(
                $"Overwrite {save.FileName}?",
                "Repack DAT",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK)
            {
                return;
            }
        }

        try
        {
            string packFolder = folderPath;
            string dest = save.FileName;
            await Busy.RunAsync(this, "Packing DAT…", async () =>
            {
                var progress = new Progress<string>(msg =>
                {
                    StatusText.Text = msg;
                    Busy.Message(this, msg);
                });
                int count = await Task.Run(() => DatPacker.PackDirectory(packFolder, dest, progress));
                StatusText.Text = $"Packed {count} files to {dest}";
            });
            ReloadWorkspace();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Repack failed");
        }
    }

#if LOCAL_UNITY
    private void UnityExportClicked(object sender, RoutedEventArgs e)
    {
        if (_cache is null || _archives.Count == 0)
        {
            System.Windows.MessageBox.Show("Load a DAT folder first.");
            return;
        }

        var window = new UnityExportWindow(
            _cache,
            _archives.ToList(),
            ArchiveList.SelectedItem as CatalogEntry,
            new UnityExportContext
            {
                Warps = _warps,
                Names = _mapNames,
                Tilesets = GetMapTilesets()
            })
        {
            Owner = this
        };
        window.ShowDialog();
    }
#endif

    private void MapEditorClicked(object sender, RoutedEventArgs e) => EnsureMapEditor();

    private void SecondMapEditorClicked(object sender, RoutedEventArgs e) => OpenSecondMapEditor();

    internal void OpenSecondMapEditor()
    {
        if (_mapEditors.Count == 0)
        {
            EnsureMapEditor();
        }

        MapEditorWindow? editor = _mapEditors.Count < 2
            ? CreateMapEditor(openCurrent: false)
            : _mapEditors[^1];
        editor?.Activate();
    }

    internal void WarpEditorClicked(object sender, RoutedEventArgs e) => OpenWarpEditor();

    internal void OpenWarpEditor()
    {
        if (ReferenceEquals(_warps, WarpTable.Empty) && _workspace.ActiveClient is null)
        {
            System.Windows.MessageBox.Show("Load a client folder first.");
            return;
        }

        if (_warpEditor is not null)
        {
            _warpEditor.ReloadRows();
            _warpEditor.Activate();
            return;
        }

        Busy.Run(this, "Opening Warp Editor…", () =>
        {
            _warpEditor = new WarpEditorWindow(
                _warps,
                _workspace,
                GoWarpSource,
                GoWarpDest,
                RefreshOpenMapEditors,
                _mapEditors.FirstOrDefault()?.PackedMaps)
            {
                Owner = this
            };
            _warpEditor.Closed += (_, _) => _warpEditor = null;
            _warpEditor.Show();
        });
    }

    private void GoWarpSource(MapWarp warp)
    {
        MapEditorWindow? editor = EnsureMapEditor();
        if (editor is null)
        {
            return;
        }

        if (!editor.TryShowWarp(warp))
        {
            System.Windows.MessageBox.Show($"Map {warp.MapId} is not in this client.", "Warp Editor");
        }
    }

    private void GoWarpDest(MapWarp warp)
    {
        bool beside = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
        MapEditorWindow? editor = beside && _mapEditors.Count > 0
            ? CreateMapEditor(openCurrent: false)
            : EnsureMapEditor();
        if (editor is null)
        {
            return;
        }

        editor.TryTravelWarp(warp);
    }

    internal bool TryOpenWarpDestBeside(MapEditorWindow source, MapWarp warp)
    {
        MapEditorWindow? dest = _mapEditors.FirstOrDefault(e => !ReferenceEquals(e, source))
            ?? CreateMapEditor(openCurrent: false);
        if (dest is null || ReferenceEquals(dest, source))
        {
            return false;
        }

        dest.TryTravelWarp(warp);
        return true;
    }

    private MapEditorWindow? EnsureMapEditor()
    {
        if (_mapEditors.Count > 0)
        {
            MapEditorWindow editor = _mapEditors[0];
            editor.Activate();
            return editor;
        }

        return CreateMapEditor(openCurrent: true);
    }

    private void RefreshOpenMapEditors()
    {
        foreach (MapEditorWindow editor in _mapEditors.ToList())
        {
            editor.RefreshWarps();
        }
    }

    private void CloseMapEditors()
    {
        foreach (MapEditorWindow editor in _mapEditors.ToList())
        {
            editor.Close();
        }

        _mapEditors.Clear();
    }

    private MapEditorWindow? CreateMapEditor(bool openCurrent)
    {
        if (_mapEditors.Count >= 2)
        {
            MapEditorWindow extra = _mapEditors[^1];
            extra.Activate();
            return extra;
        }

        if (_cache is null)
        {
            System.Windows.MessageBox.Show("Load a DAT folder first.");
            return null;
        }

        IReadOnlyList<MapAssets> tilesets = GetMapTilesets();
        MapTilesetOption? choice = TilesetBox?.SelectedItem as MapTilesetOption;
        MapAssets? tileset = choice?.Assets;
        if (tileset is null)
        {
            tileset = tilesets.OrderByDescending(s => s.FloorCount).ThenByDescending(s => s.MaxWallId).FirstOrDefault();
        }

        if (tileset is null)
        {
            System.Windows.MessageBox.Show("Need seo (TILEA.BMP floors) and ia (stc wall HPFs) under the current root.");
            return null;
        }

        string? currentMap = openCurrent ? CurrentPreviewMapPath() : null;
        string? packedMap = openCurrent ? CurrentPackedMapName() : null;
        List<IArchive> packedMaps = PackedMapArchives(tileset);
        if (openCurrent && choice?.Assets is null && currentMap is not null)
        {
            try
            {
                MapFile map = MapFile.Load(currentMap);
                tileset = MapAssets.Pick(tilesets, map, currentMap);
            }
            catch
            {
                // keep the default tileset
            }
        }
        else if (openCurrent && choice?.Assets is null && packedMap is not null)
        {
            try
            {
                ArchiveEntry? packedEntry = packedMaps
                    .Select(a => a.GetEntry(packedMap))
                    .FirstOrDefault(e => e is not null);
                if (packedEntry is not null)
                {
                    tileset = MapAssets.Pick(tilesets, MapFile.FromBytes(packedEntry.ReadAllBytes()), packedMap);
                }
            }
            catch
            {
                // keep the default tileset
            }
        }

        Busy.Show(this, "Opening Map Editor…");
        try
        {
            List<string> maps = _workspace.MapFolders.ToList();
            if (currentMap is not null)
            {
                string? dir = Path.GetDirectoryName(currentMap);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    maps.Add(dir);
                }
            }

            var editor = new MapEditorWindow(
                tileset.Seo,
                tileset.Ia,
                maps,
                currentMap,
                _workspace,
                choice?.Snow == true,
                FloorAtlasOffset(),
                packedMaps,
                packedMap,
                _warps,
                _cache,
                _serverWorld)
            {
                Owner = this
            };
            if (_mapEditors.Count > 0)
            {
                MapEditorWindow first = _mapEditors[0];
                editor.WindowStartupLocation = WindowStartupLocation.Manual;
                editor.Left = first.Left + 48;
                editor.Top = first.Top + 48;
            }

            editor.Closed += (_, _) => _mapEditors.Remove(editor);
            _mapEditors.Add(editor);
            editor.Show();
            return editor;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Map Editor");
            return null;
        }
        finally
        {
            Busy.Hide(this);
        }
    }

    private void ItemEditorClicked(object sender, RoutedEventArgs e)
    {
        if (_itemEditor is not null)
        {
            _itemEditor.Activate();
            return;
        }

        if (_cache is null)
        {
            System.Windows.MessageBox.Show("Load a client folder first.");
            return;
        }

        Busy.Run(this, "Opening Item Editor…", () =>
        {
            _itemEditor = new ItemEditorWindow(_workspace, _cache, _archives.ToList()) { Owner = this };
            _itemEditor.Closed += (_, _) => _itemEditor = null;
            _itemEditor.Show();
        });
    }

    private void MonsterEditorClicked(object sender, RoutedEventArgs e)
    {
        if (_monsterEditor is not null)
        {
            _monsterEditor.Activate();
            return;
        }

        if (_cache is null)
        {
            System.Windows.MessageBox.Show("Load a client folder first.");
            return;
        }

        Busy.Run(this, "Opening Monster Editor…", () =>
        {
            _monsterEditor = new MonsterEditorWindow(_workspace, _cache, _archives.ToList()) { Owner = this };
            _monsterEditor.Closed += (_, _) => _monsterEditor = null;
            _monsterEditor.Show();
        });
    }

    private void PaletteEditorClicked(object sender, RoutedEventArgs e)
    {
        if (_paletteEditor is not null)
        {
            _paletteEditor.Activate();
            return;
        }

        if (_cache is null)
        {
            System.Windows.MessageBox.Show("Load a client folder first.");
            return;
        }

        Busy.Run(this, "Opening Palette Editor…", () =>
        {
            _paletteEditor = new PaletteEditorWindow(_cache, _archives.ToList()) { Owner = this };
            _paletteEditor.Closed += (_, _) => _paletteEditor = null;
            _paletteEditor.Show();
        });
    }

    private void MetaEditorClicked(object sender, RoutedEventArgs e)
    {
        if (_metaEditor is not null)
        {
            _metaEditor.Activate();
            return;
        }

        if (_cache is null)
        {
            System.Windows.MessageBox.Show("Load a client folder first.");
            return;
        }

        Busy.Run(this, "Opening Metafile Editor…", () =>
        {
            _metaEditor = new MetaEditorWindow(_cache, _archives.ToList()) { Owner = this };
            _metaEditor.Closed += (_, _) => _metaEditor = null;
            _metaEditor.Show();
        });
    }

    internal void LightEditorClicked(object sender, RoutedEventArgs e) => OpenLightEditor();

    internal void OpenLightEditor()
    {
        if (_lightEditor is not null)
        {
            _lightEditor.Activate();
            return;
        }

        if (_cache is null)
        {
            System.Windows.MessageBox.Show("Load a client folder first.");
            return;
        }

        Busy.Run(this, "Opening Light Metafile…", () =>
        {
            _lightEditor = new LightEditorWindow(_cache, _archives.ToList(), _workspace.ActiveClient) { Owner = this };
            _lightEditor.Closed += (_, _) => _lightEditor = null;
            _lightEditor.Show();
        });
    }

    internal void NotifyServerWorldLoaded()
    {
        foreach (MapEditorWindow editor in _mapEditors.ToList())
        {
            editor.RefreshServerOverlay();
        }
    }

    private void LoadServerWorldForClient()
    {
        _serverWorld.Clear();
        string folder = _workspace.ActiveClient?.ServerWorldFolder ?? "";
        if (!Directory.Exists(folder))
        {
            return;
        }

        try
        {
            _serverWorld.LoadFrom(folder);
        }
        catch
        {
            _serverWorld.Clear();
        }
    }

    private void EffectsEditorClicked(object sender, RoutedEventArgs e) =>
        OpenGraphicEditor(GraphicEditorWindow.Kind.Effects);

    private void UiEditorClicked(object sender, RoutedEventArgs e) =>
        OpenGraphicEditor(GraphicEditorWindow.Kind.Ui);

    private void FloorEditorClicked(object sender, RoutedEventArgs e) =>
        OpenGraphicEditor(GraphicEditorWindow.Kind.Floors);

    private void WallEditorClicked(object sender, RoutedEventArgs e) =>
        OpenGraphicEditor(GraphicEditorWindow.Kind.Walls);

    private void PortraitEditorClicked(object sender, RoutedEventArgs e) =>
        OpenGraphicEditor(GraphicEditorWindow.Kind.Portraits);

    private void ConvertClicked(object sender, RoutedEventArgs e)
    {
        if (_convertWindow is not null)
        {
            _convertWindow.Activate();
            return;
        }

        if (_cache is null)
        {
            System.Windows.MessageBox.Show("Load a client folder first.");
            return;
        }

        _convertWindow = new ConvertWindow(_cache, _archives.ToList()) { Owner = this };
        _convertWindow.Closed += (_, _) => _convertWindow = null;
        _convertWindow.Show();
    }

    private void SplicerClicked(object sender, RoutedEventArgs e)
    {
        if (_splicerWindow is not null)
        {
            _splicerWindow.Activate();
            return;
        }

        if (_cache is null)
        {
            System.Windows.MessageBox.Show("Load a client folder first.");
            return;
        }

        _splicerWindow = new TileSplicerWindow(_cache, _archives.ToList()) { Owner = this };
        _splicerWindow.Closed += (_, _) => _splicerWindow = null;
        _splicerWindow.Show();
    }

    private void CharacterAssemblerClicked(object sender, RoutedEventArgs e)
    {
        if (_characterAssembler is not null)
        {
            _characterAssembler.Activate();
            return;
        }

        if (_cache is null)
        {
            System.Windows.MessageBox.Show("Load a client folder first.");
            return;
        }

        Busy.Run(this, "Opening Character Assembler…", () =>
        {
            _characterAssembler = new CharacterAssemblerWindow(_cache, _archives.ToList()) { Owner = this };
            _characterAssembler.Closed += (_, _) => _characterAssembler = null;
            _characterAssembler.Show();
        });
    }

    private void MusicBrowserClicked(object sender, RoutedEventArgs e)
    {
        if (_musicBrowser is not null)
        {
            _musicBrowser.Activate();
            return;
        }

        if (_cache is null)
        {
            System.Windows.MessageBox.Show("Load a client folder first.");
            return;
        }

        Busy.Run(this, "Opening Music / Sound…", () =>
        {
            _musicBrowser = new MusicBrowserWindow(_cache, _archives.ToList()) { Owner = this };
            _musicBrowser.Closed += (_, _) => _musicBrowser = null;
            _musicBrowser.Show();
        });
    }

    private void OpenGraphicEditor(GraphicEditorWindow.Kind kind)
    {
        if (_graphicEditor is not null)
        {
            _graphicEditor.ShowKind(kind);
            return;
        }

        if (_cache is null)
        {
            System.Windows.MessageBox.Show("Load a client folder first.");
            return;
        }

        Busy.Run(this, "Opening Graphic Editor…", () =>
        {
            _graphicEditor = new GraphicEditorWindow(_cache, _archives.ToList(), kind, _workspace.ActiveClient) { Owner = this };
            _graphicEditor.Closed += (_, _) => _graphicEditor = null;
            _graphicEditor.Show();
        });
    }

    private string? CurrentPreviewMapPath()
    {
        if (GalleryVisible || _cache is null)
        {
            return null;
        }

        if (ArchiveList.SelectedItem is not CatalogEntryView catalog
            || EntryList.SelectedItem is not ArchiveEntry entry)
        {
            return null;
        }

        if (!entry.Extension.Equals(".map", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            IArchive archive = _cache.Get(catalog);
            string path = Path.IsPathRooted(entry.Name)
                ? entry.Name
                : Path.Combine(archive.SourcePath, entry.Name);
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private string? CurrentPackedMapName()
    {
        if (GalleryVisible)
        {
            return null;
        }

        if (ArchiveList.SelectedItem is not CatalogEntryView catalog
            || EntryList.SelectedItem is not ArchiveEntry entry)
        {
            return null;
        }

        if (catalog.IsFolder
            || !entry.Extension.Equals(".map", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return CurrentPreviewMapPath() is null ? entry.Name : null;
    }

    private List<IArchive> PackedMapArchives(MapAssets tileset)
    {
        var result = new List<IArchive>();
        if (_cache is null)
        {
            return result;
        }

        foreach (CatalogEntryView catalog in _archives.OfType<CatalogEntryView>())
        {
            if (catalog.Role != ArchiveRole.Maps || catalog.IsFolder)
            {
                continue;
            }

            try
            {
                IArchive archive = _cache.Get(catalog);
                if (!archive.Entries.Any(e => e.Extension.Equals(".map", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                result.Add(archive);
            }
            catch
            {
                // skip unreadable packed map archives
            }
        }

        return result
            .OrderByDescending(a => string.Equals(a.SourcePath, tileset.SeoPath, StringComparison.OrdinalIgnoreCase))
            .ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool HasTileAtlas(CatalogEntryView catalog)
    {
        try
        {
            if (_cache is null)
            {
                return false;
            }

            IArchive archive = _cache.Get(catalog);
            return archive.GetEntry("TILEA.BMP") is not null
                || archive.GetEntry("tile_00001.rgba") is not null
                || catalog.Role == ArchiveRole.Floors
                    && archive.Entries.Any(e => e.Name.StartsWith("tile_", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private IReadOnlyList<MapAssets> GetMapTilesets()
    {
        if (_mapTilesets.Count > 0 || _cache is null)
        {
            return _mapTilesets;
        }

        var seos = _archives.OfType<CatalogEntryView>().Where(HasTileAtlas).ToList();
        var ias = _archives.OfType<CatalogEntryView>().Where(HasWalls).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogEntryView seoCat in seos.OrderBy(PreferPackedArchive))
        {
            try
            {
                if (seoCat.IsFolder && seos.Any(s => !s.IsFolder && SameTilesetFamily(s, seoCat)))
                {
                    continue;
                }

                IArchive seo = _cache.Get(seoCat);
                ArchiveEntry? tilea = seo.GetEntry("TILEA.BMP") ?? seo.GetEntry("tile_00001.rgba");
                if (tilea is null)
                {
                    continue;
                }

                CatalogEntryView? iaCat = ias.FirstOrDefault(i => SameTilesetFamily(i, seoCat))
                    ?? ias.FirstOrDefault();
                if (iaCat is null)
                {
                    continue;
                }

                IArchive ia = _cache.Get(iaCat);
                if (!seen.Add(seo.SourcePath + "\n" + ia.SourcePath))
                {
                    continue;
                }

                var assets = new MapAssets(seo, ia, $"{seoCat.Title}  +  {iaCat.Title}");
                _mapTilesets.Add(assets);
            }
            catch
            {
                // skip unreadable copies
            }
        }

        _mapTilesets = _mapTilesets
            .OrderByDescending(s => s.FloorCount)
            .ThenByDescending(s => s.MaxWallId)
            .ToList();
        return _mapTilesets;
    }

    private bool HasWalls(CatalogEntryView catalog)
    {
        try
        {
            return _cache?.Get(catalog).Entries.Any(e => e.Name.StartsWith("stc", StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch
        {
            return catalog.Role == ArchiveRole.Walls;
        }
    }

    private static bool SameTilesetFamily(CatalogEntryView a, CatalogEntryView b)
    {
        if (string.Equals(a.SourcePath, b.SourcePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(a.Variant) && string.Equals(a.Variant, b.Variant, StringComparison.OrdinalIgnoreCase)
            && !a.Variant.Contains(" / ", StringComparison.Ordinal))
        {
            return true;
        }

        bool aStoned = a.SourcePath.Contains("STONED", StringComparison.OrdinalIgnoreCase);
        bool bStoned = b.SourcePath.Contains("STONED", StringComparison.OrdinalIgnoreCase);
        if (aStoned || bStoned)
        {
            return aStoned == bStoned;
        }

        return string.IsNullOrWhiteSpace(a.Variant) == string.IsNullOrWhiteSpace(b.Variant);
    }

    private static int PreferPackedArchive(CatalogEntryView entry) =>
        entry.IsFolder ? 1 : 0;

    private void LoadRoot(string root)
    {
        _workspace.SetRoot(root);
        ReloadWorkspace();
    }

    private void ReloadWorkspace()
    {
        Busy.Run(this, "Loading client…", ReloadWorkspaceCore);
    }

    private void ReloadWorkspaceCore()
    {
        try
        {
            _itemEditor?.Close();
            _monsterEditor?.Close();
            _warpEditor?.Close();
            _lightEditor?.Close();
            CloseMapEditors();
            _cache?.Dispose();
            _cache = new ArchiveCache();
            _mapTilesets = [];
            _warps = WarpTable.Empty;
            _archives.Clear();
            var roots = _workspace.Roots.ToList();
            FillClientBox();
            IReadOnlyList<CatalogEntry> catalogs = Busy.Pump(() => DatCatalog.Scan(roots));
            foreach (CatalogEntry entry in catalogs)
            {
                _archives.Add(new CatalogEntryView(entry));
            }

            CollectionViewSource.GetDefaultView(_archives).Refresh();
            _warps = Busy.Pump(() => WarpTable.Load(
                _workspace.MetaFolders,
                WarpStore.FileFor(_workspace.ActiveClient),
                _workspace.WarpFolders));
            _mapNames = Busy.Pump(() => MapNameIndex.FromWorkspace(_workspace));
            LoadServerWorldForClient();
            RefreshFavoriteList();
            string name = _workspace.ActiveClient?.Name ?? Path.GetFileName(CurrentRoot);
            StatusText.Text = string.IsNullOrWhiteSpace(CurrentRoot)
                ? "Pick a client folder."
                : $"{_archives.Count} archives in {name} — {CurrentRoot}";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Scan failed");
        }
    }

    private void WorkspaceChanged(object sender, SelectionChangedEventArgs e) => FiltersChanged(sender, e);

    private void FiltersChanged(object sender, EventArgs e)
    {
        if (!IsLoaded || EntryList is null)
        {
            return;
        }

        CollectionViewSource.GetDefaultView(_archives)?.Refresh();
        FillEntries();
    }

    private bool ArchiveFilter(object obj)
    {
        if (obj is not CatalogEntryView entry)
        {
            return false;
        }

        string workspace = WorkspaceBox.SelectedItem as string ?? "All";
        if (workspace != "All" && entry.RoleLabel != workspace && !(workspace == "Other" && entry.Role is ArchiveRole.Unknown or ArchiveRole.Misc))
        {
            return false;
        }

        string filter = ArchiveFilterBox?.Text ?? "";
        if (!string.IsNullOrWhiteSpace(filter) &&
            entry.Title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
            entry.SourcePath.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        return true;
    }

    private void ArchiveSelected(object sender, SelectionChangedEventArgs e) => FillEntries();

    private void FillEntries()
    {
        if (EntryList is null || OverlayList is null || PaletteBox is null || ArchiveList is null)
        {
            return;
        }

        EntryList.ItemsSource = null;
        OverlayList.ItemsSource = null;
        PaletteBox.ItemsSource = null;
        if (ArchiveList.SelectedItem is not CatalogEntryView item || _cache is null)
        {
            _entrySource = [];
            BindEntryPage();
            return;
        }

        try
        {
            IArchive archive = _cache.Get(item);
            IEnumerable<ArchiveEntry> entries = archive.Entries;
            string workspace = WorkspaceBox.SelectedItem as string ?? "All";
            entries = FilterEntries(entries, workspace);
            entries = IconSheets.Expand(entries);
            entries = FloorTiles.Expand(entries);
            if (HidePalBox?.IsChecked == true)
            {
                entries = entries.Where(x => !x.Extension.Equals(".pal", StringComparison.OrdinalIgnoreCase));
            }

            if (HideTableBox?.IsChecked == true)
            {
                entries = entries.Where(x =>
                    !x.Extension.Equals(".tbl", StringComparison.OrdinalIgnoreCase) &&
                    !x.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase));
            }

            string filter = EntryFilterBox?.Text ?? "";
            if (!string.IsNullOrWhiteSpace(filter))
            {
                entries = entries.Where(x =>
                    x.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
            }

            var list = entries.OrderBy(e => e.DisplayName, NaturalStringComparer.OrdinalIgnoreCase).ToList();
            _entrySource = list;
            _entryPage = 0;
            BindEntryPage();
            var palettes = new List<string> { "(auto)" };
            palettes.AddRange(PaletteLocator.Palettes(archive).Select(p => p.Name));
            if (item.Role == ArchiveRole.Maps)
            {
                IReadOnlyList<MapAssets> tilesets = GetMapTilesets();
                palettes.AddRange(tilesets
                    .SelectMany(t => PaletteLocator.Palettes(t.Seo))
                    .Select(p => p.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
                if (TilesetBox is not null)
                {
                    var options = new List<MapTilesetOption> { MapTilesetOption.Auto };
                    foreach (MapAssets set in tilesets)
                    {
                        options.Add(MapTilesetOption.Ground(set));
                        if (set.SnowCount > 0)
                        {
                            options.Add(MapTilesetOption.SnowFloors(set));
                        }
                    }

                    TilesetBox.DisplayMemberPath = nameof(MapTilesetOption.Label);
                    TilesetBox.ItemsSource = options;
                    TilesetBox.SelectedIndex = 0;
                }
            }
            else if (TilesetBox is not null)
            {
                TilesetBox.DisplayMemberPath = nameof(MapTilesetOption.Label);
                TilesetBox.ItemsSource = new[] { MapTilesetOption.Auto };
                TilesetBox.SelectedIndex = 0;
            }

            _colorVariantKey = null;
            PaletteBox.ItemsSource = palettes;
            PaletteBox.SelectedIndex = 0;

            OverlayList.ItemsSource = archive.Entries.Where(x => x.Extension.Equals(".epf", StringComparison.OrdinalIgnoreCase)).Take(400).ToList();
            if (item.Role == ArchiveRole.PlayerSprites)
            {
                var motions = new List<object> { "Idle" };
                motions.AddRange(KhanMotions.Presets.Cast<object>());
                MotionBox.ItemsSource = motions;
                MotionBox.SelectedIndex = 0;
            }
            else
            {
                MotionBox.ItemsSource = new[] { "All", "Idle", "Walk", "Attack", "Attack2", "Attack3" };
                MotionBox.SelectedIndex = 0;
            }

            bool tileGallery = item.Role is ArchiveRole.Floors or ArchiveRole.Items or ArchiveRole.Mobs
                or ArchiveRole.Walls or ArchiveRole.PlayerSprites or ArchiveRole.Effects or ArchiveRole.Ui;
            if (!_suppressAutoGallery && tileGallery && GalleryBox is not null)
            {
                _ignoreGalleryToggle = true;
                GalleryBox.IsChecked = true;
                _ignoreGalleryToggle = false;
            }

            if (list.Count > 0 && !_suppressAutoGallery)
            {
                EntryList.SelectedIndex = 0;
            }

            UpdateActionBar();
            if (!_suppressAutoGallery && GalleryBox?.IsChecked == true && GalleryBox.Visibility == Visibility.Visible)
            {
                _ = StartGallery(GalleryEntries(item, _entrySource), "Gallery");
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private static IEnumerable<ArchiveEntry> FilterEntries(IEnumerable<ArchiveEntry> entries, string workspace)
    {
        return workspace switch
        {
            "Mobs" => entries.Where(e => e.Extension.Equals(".mpf", StringComparison.OrdinalIgnoreCase) || e.Extension.Equals(".pal", StringComparison.OrdinalIgnoreCase)),
            "Walls" or "Floors" => entries.Where(e =>
                e.Extension.Equals(".hpf", StringComparison.OrdinalIgnoreCase) ||
                e.Extension.Equals(".spf", StringComparison.OrdinalIgnoreCase) ||
                e.Extension.Equals(".pcx", StringComparison.OrdinalIgnoreCase) ||
                e.Extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
                e.Extension.Equals(".hea", StringComparison.OrdinalIgnoreCase) ||
                e.Extension.Equals(".pal", StringComparison.OrdinalIgnoreCase) ||
                e.Extension.Equals(".tbl", StringComparison.OrdinalIgnoreCase)),
            "UI" => entries.Where(e => e.Extension.Equals(".epf", StringComparison.OrdinalIgnoreCase) || e.Extension.Equals(".pal", StringComparison.OrdinalIgnoreCase)),
            "Items" => entries.Where(e =>
                e.Name.StartsWith("item", StringComparison.OrdinalIgnoreCase) ||
                e.Extension.Equals(".epf", StringComparison.OrdinalIgnoreCase) && e.Name.Contains("item", StringComparison.OrdinalIgnoreCase) ||
                e.Name.Equals("itempal.tbl", StringComparison.OrdinalIgnoreCase) ||
                e.Name.StartsWith("item", StringComparison.OrdinalIgnoreCase) && e.Extension.Equals(".pal", StringComparison.OrdinalIgnoreCase)),
            "Effects" => entries.Where(e =>
                e.Name.StartsWith("efct", StringComparison.OrdinalIgnoreCase) ||
                e.Extension.Equals(".efa", StringComparison.OrdinalIgnoreCase) ||
                e.Extension.Equals(".epf", StringComparison.OrdinalIgnoreCase) ||
                e.Extension.Equals(".pal", StringComparison.OrdinalIgnoreCase) ||
                e.Extension.Equals(".tbl", StringComparison.OrdinalIgnoreCase)),
            "Players / gear" => entries.Where(e =>
                e.Extension.Equals(".epf", StringComparison.OrdinalIgnoreCase) ||
                e.Extension.Equals(".pal", StringComparison.OrdinalIgnoreCase)),
            "Maps" => entries.Where(e => e.Extension.Equals(".map", StringComparison.OrdinalIgnoreCase)),
            "Metadata" => entries,
            _ => entries
        };
    }

    private void EntrySelected(object sender, SelectionChangedEventArgs e)
    {
        if (GalleryBox?.IsChecked == true)
        {
            return;
        }

        if (GalleryVisible)
        {
            _gallery = null;
            HideGallery();
        }

        _frame = IdleFrameFor(EntryList.SelectedItem as ArchiveEntry);
        RefreshPreview();
    }

    private void GalleryToggled(object sender, RoutedEventArgs e)
    {
        if (_ignoreGalleryToggle || !IsLoaded || EntryList is null)
        {
            return;
        }

        if (GalleryBox.IsChecked == true)
        {
            IEnumerable<ArchiveEntry> source = _entrySource;
            if (ArchiveList.SelectedItem is CatalogEntryView catalog)
            {
                source = GalleryEntries(catalog, source);
            }

            _ = StartGallery(source, "Gallery");
        }
        else
        {
            _galleryCts?.Cancel();
            _gallery = null;
            HideGallery();
            RefreshPreview();
        }
    }

    private void GalleryCellClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GalleryItem item } || string.IsNullOrWhiteSpace(item.Name))
        {
            return;
        }

        e.Handled = true;
        OpenSoloFromGallery(item);
    }

    private void GalleryCellHovered(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GalleryItem item } && !string.IsNullOrWhiteSpace(item.Name))
        {
            StatusText.Text = item.Name;
        }
    }

    private void PreviewOptionsChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || EntryList is null)
        {
            return;
        }

        if (sender == ZoomBox)
        {
            if (!_syncingZoom)
            {
                SetPreviewZoom(ViewZoom.Parse(ZoomBox.SelectedItem));
            }

            return;
        }

        if ((GalleryBox?.IsChecked == true || GalleryVisible) && _gallery is not null)
        {
            _gallery.SetPalette(SelectedPaletteName());
            _gallery.SetTileset(TilesetBox?.SelectedItem as MapTilesetOption);
            _gallery.SetFloorOffset(FloorAtlasOffset());
            _ = LoadGalleryPage(_gallery.Page, "Gallery");
            return;
        }

        RefreshPreview();
    }

    private void RefreshPreview(bool showBusy = true)
    {
        if (showBusy && IsLoaded)
        {
            Busy.Show(this, "Loading preview…");
        }

        try
        {
            RefreshPreviewCore();
        }
        finally
        {
            if (showBusy && IsLoaded)
            {
                Busy.Hide(this);
            }
        }
    }

    private void RefreshPreviewCore()
    {
        if (GalleryBox?.IsChecked == true || GalleryVisible)
        {
            return;
        }

        if (ArchiveList.SelectedItem is not CatalogEntryView item || EntryList.SelectedItem is not ArchiveEntry entry || _cache is null)
        {
            return;
        }

        try
        {
            StopAudio();
            IArchive archive = _cache.Get(item);
            var selected = EntryList.SelectedItems.Cast<ArchiveEntry>().ToList();
            bool sheet = ContactSheetBox.IsChecked == true;
            if (sheet && selected.Count > 1)
            {
                _playing = false;
                _ = StartGallery(selected, "Selection");
                return;
            }

            var overlays = OverlayList.SelectedItems.Cast<ArchiveEntry>().Where(o => o.Name != entry.Name).ToList();
            string motionGroup = MotionBox.SelectedItem as string ?? "All";
            ArchiveEntry previewEntry = entry;
            if (MotionBox.SelectedItem is KhanMotion motion && KhanPieces.TryParse(entry.Name, out KhanPieceInfo khan))
            {
                motionGroup = "All";
                string posed = $"{khan.Gender}{khan.Slot}{khan.Id:D3}{KhanMotions.FileSuffix(motion)}.epf";
                previewEntry = archive.GetEntry(posed) ?? entry;
            }

            BindColorVariants(archive, previewEntry);
            _preview = PreviewBuilder.Build(
                archive,
                previewEntry,
                _frame,
                SelectedPaletteName(),
                motionGroup,
                AdditiveBox.IsChecked == true,
                overlays,
                sheet,
                GetMapTilesets(),
                (TilesetBox?.SelectedItem as MapTilesetOption)?.Label,
                item.SourcePath,
                TilesetBox?.SelectedItem as MapTilesetOption,
                FloorAtlasOffset(),
                _warps);
            ShowPreview(_preview);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ShowPreview(PreviewResult result)
    {
        HideGallery();
        ErrorText.Visibility = Visibility.Collapsed;
        HexBox.Visibility = Visibility.Collapsed;
        PreviewImage.Visibility = Visibility.Collapsed;
        HidePreviewArea();
        if (MetaGrid is not null)
        {
            MetaGrid.Visibility = Visibility.Collapsed;
            MetaGrid.ItemsSource = null;
            MetaGrid.Columns.Clear();
        }

        MetaBox.Text = result.Metadata;
        UpdateFrameUi(result);
        ApplyPreviewClock(result);
        if (result.Error is not null)
        {
            ErrorText.Text = result.Error;
            ErrorText.Visibility = Visibility.Visible;
            UpdateActionBar();
            return;
        }

        if (result.TableHeaders is not null && result.TableRows is not null && MetaGrid is not null)
        {
            var view = new List<Dictionary<string, string>>(result.TableRows.Count);
            foreach (string[] row in result.TableRows)
            {
                var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int i = 0; i < result.TableHeaders.Count; i++)
                {
                    dict[result.TableHeaders[i]] = i < row.Length ? row[i] ?? "" : "";
                }

                view.Add(dict);
            }

            for (int i = 0; i < result.TableHeaders.Count; i++)
            {
                string header = result.TableHeaders[i];
                MetaGrid.Columns.Add(new DataGridTextColumn
                {
                    Header = header,
                    Binding = new System.Windows.Data.Binding("[" + header + "]"),
                    Width = i == 0 ? 220 : DataGridLength.Auto
                });
            }

            MetaGrid.ItemsSource = view;
            MetaGrid.Visibility = Visibility.Visible;
            UpdateActionBar();
            return;
        }

        if (result.Hex is not null && result.Image is null)
        {
            HexBox.Text = result.Hex;
            HexBox.Visibility = Visibility.Visible;
            UpdateActionBar();
            return;
        }

        if (result.Image is not null)
        {
            if (result.Cinematic is BinkClip clip)
            {
                BindCinematicSurface(clip, result.Pixels);
                PreviewImage.Source = _cinematicBmp;
                _playing = true;
                StartPreviewAudio(result, restart: true);
            }
            else
            {
                _cinematicBmp = null;
                _cinematicPixels = null;
                PreviewImage.Source = result.Image;
            }

            PreviewImage.Visibility = Visibility.Visible;
            ViewZoom.Apply(PreviewScale, _previewZoom);
            ApplyPendingViewerLanding();
            LayoutPreviewWarpLabels();
            LayoutPreviewArea();
        }
        else
        {
            _cinematicBmp = null;
            _cinematicPixels = null;
            WarpHoverTip.Hide(WarpHoverPopup);
            ClearPreviewWarpLabels();
            HidePreviewArea();
        }

        UpdateActionBar();
    }

    private void PreviewWheelZoom(object sender, MouseWheelEventArgs e)
    {
        if (GalleryVisible || PreviewScroller is null || PreviewImage?.Visibility != Visibility.Visible || PreviewImage.Source is null)
        {
            return;
        }

        System.Windows.Point mouse = e.GetPosition(PreviewScroller);
        System.Windows.Point content = e.GetPosition(PreviewImage);
        SetPreviewZoom(ViewZoom.Step(_previewZoom, e.Delta), mouse, content);
        e.Handled = true;
    }

    private void SetPreviewZoom(double zoom, System.Windows.Point? mouse = null, System.Windows.Point? content = null)
    {
        zoom = ViewZoom.Clamp(zoom);
        _previewZoom = zoom;
        ViewZoom.Apply(PreviewScale, _previewZoom);
        if (mouse is { } view && content is { } local)
        {
            ViewZoom.ScrollToward(PreviewScroller, view, local, _previewZoom);
        }

        if (ZoomBox is not null)
        {
            _syncingZoom = true;
            ZoomBox.SelectedIndex = ViewZoom.ClosestPresetIndex(_previewZoom);
            _syncingZoom = false;
        }
    }

    private void ShowError(string message)
    {
        HideGallery();
        PreviewImage.Visibility = Visibility.Collapsed;
        HexBox.Visibility = Visibility.Collapsed;
        _cinematicBmp = null;
        _cinematicPixels = null;
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        MetaBox.Text = message;
        ApplyPreviewClock(null);
        UpdateActionBar();
    }

    private async Task StartGallery(IEnumerable<ArchiveEntry> entries, string label)
    {
        if (ArchiveList.SelectedItem is not CatalogEntryView item || _cache is null)
        {
            return;
        }

        _galleryCts?.Cancel();
        _galleryCts = new CancellationTokenSource();
        CancellationToken ct = _galleryCts.Token;
        var list = entries.ToList();
        try
        {
            IArchive archive = _cache.Get(item);
            _playing = false;
            StopAudio();
            bool maps = item.Role == ArchiveRole.Maps
                || list.Any(e => e.Extension.Equals(".map", StringComparison.OrdinalIgnoreCase));
            if (!_galleryPageSizePicked && GalleryPageSizeBox is not null)
            {
                _ignorePageSize = true;
                GalleryPageSizeBox.SelectedItem = maps ? 10 : 50;
                _ignorePageSize = false;
            }

            int pageSize = GalleryPageSizeBox?.SelectedItem is int chosen ? chosen : maps ? 10 : 50;
            _gallery = new EntryGallery { PageSize = pageSize };
            _gallery.SetSource(
                archive,
                list,
                GetMapTilesets(),
                (TilesetBox?.SelectedItem as MapTilesetOption)?.Label,
                item.SourcePath,
                TilesetBox?.SelectedItem as MapTilesetOption,
                FloorAtlasOffset(),
                SelectedPaletteName(),
                _warps);
            PreviewScroller.Visibility = Visibility.Collapsed;
            PreviewImage.Visibility = Visibility.Collapsed;
            HexBox.Visibility = Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Collapsed;
            GalleryHost.ItemsSource = _gallery.Rows;
            GalleryPanel.Visibility = Visibility.Visible;
            FrameLabel.Text = "Frame (gallery)";
            UpdateActionBar();
            await LoadGalleryPage(0, label, ct);
        }
        catch (OperationCanceledException)
        {
            // replaced or closed
        }
        catch (Exception ex)
        {
            HideGallery();
            ShowError(ex.Message);
        }
    }

    private void OpenSoloFromGallery(GalleryItem item)
    {
        _galleryCts?.Cancel();
        _gallery = null;
        HideGallery();
        if (GalleryBox.IsChecked == true)
        {
            _ignoreGalleryToggle = true;
            GalleryBox.IsChecked = false;
            _ignoreGalleryToggle = false;
        }

        ArchiveEntry? match = item.Entry
            ?? _entrySource.FirstOrDefault(x => x.DisplayName.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
        if (match is null && ArchiveList.SelectedItem is CatalogEntryView catalog && _cache is not null)
        {
            match = _cache.Get(catalog).GetEntry(item.Name);
        }

        if (match is not null)
        {
            RevealListed(match);
        }

        _frame = IdleFrameFor(match);
        RefreshPreview();
    }

    private static int IdleFrameFor(ArchiveEntry? entry) =>
        entry is not null && KhanPieces.TryParse(entry.Name, out _) ? KhanPieces.IdleDirection : 0;

    private void HideGallery(bool updateBar = true)
    {
        GalleryHost.ItemsSource = null;
        if (GalleryPanel is not null)
        {
            GalleryPanel.Visibility = Visibility.Collapsed;
        }

        PreviewScroller.Visibility = Visibility.Visible;
        if (MetaGrid is not null)
        {
            MetaGrid.Visibility = Visibility.Collapsed;
        }

        if (updateBar)
        {
            UpdateActionBar();
        }
    }

    private void UpdateFrameUi(PreviewResult result)
    {
        _syncingFrame = true;
        int count = Math.Max(1, result.FrameCount);
        FrameSlider.Maximum = Math.Max(0, count - 1);
        FrameSlider.Value = Math.Clamp(_frame, 0, Math.Max(0, count - 1));
        FrameLabel.Text = $"Frame {_frame + 1} / {count}";
        _syncingFrame = false;
    }

    private void PreviewPanDown(object sender, MouseButtonEventArgs e)
    {
        if (GalleryVisible || PreviewScroller is null || PreviewImage?.Visibility != Visibility.Visible || PreviewImage.Source is null)
        {
            return;
        }

        if (e.OriginalSource is System.Windows.Controls.Primitives.Thumb
            or System.Windows.Controls.Primitives.RepeatButton
            or System.Windows.Controls.Primitives.Track)
        {
            return;
        }

        _previewPressed = true;
        _panning = false;
        _panStart = e.GetPosition(PreviewScroller);
        _panOffsetX = PreviewScroller.HorizontalOffset;
        _panOffsetY = PreviewScroller.VerticalOffset;
        PreviewScroller.CaptureMouse();
        e.Handled = true;
    }

    private void PreviewPanMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_previewPressed && PreviewScroller is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            System.Windows.Point now = e.GetPosition(PreviewScroller);
            if (!_panning && (Math.Abs(now.X - _panStart.X) > 6 || Math.Abs(now.Y - _panStart.Y) > 6))
            {
                _panning = true;
                WarpHoverTip.Hide(WarpHoverPopup);
                PreviewScroller.Cursor = System.Windows.Input.Cursors.SizeAll;
                PreviewImage.Cursor = System.Windows.Input.Cursors.SizeAll;
            }

            if (_panning)
            {
                PreviewScroller.ScrollToHorizontalOffset(_panOffsetX - (now.X - _panStart.X));
                PreviewScroller.ScrollToVerticalOffset(_panOffsetY - (now.Y - _panStart.Y));
                e.Handled = true;
            }

            return;
        }

        UpdatePreviewWarpCursor(e);
    }

    private void PreviewPanUp(object sender, MouseButtonEventArgs e)
    {
        if (_previewPressed && !_panning)
        {
            TryPreviewWarpClick(e);
            e.Handled = true;
        }
        else if (_panning)
        {
            e.Handled = true;
        }

        StopPreviewPan();
    }

    private void PreviewPanLost(object sender, System.Windows.Input.MouseEventArgs e) => StopPreviewPan();

    private void StopPreviewPan()
    {
        WarpHoverTip.Hide(WarpHoverPopup);
        _previewPressed = false;
        _panning = false;
        if (PreviewScroller is not null)
        {
            if (PreviewScroller.IsMouseCaptured)
            {
                PreviewScroller.ReleaseMouseCapture();
            }

            PreviewScroller.Cursor = System.Windows.Input.Cursors.Hand;
        }

        if (PreviewImage is not null)
        {
            PreviewImage.Cursor = System.Windows.Input.Cursors.Hand;
        }
    }

    private void UpdatePreviewWarpCursor(System.Windows.Input.MouseEventArgs e)
    {
        if (PreviewImage?.Visibility != Visibility.Visible || _preview?.Map is null || _preview.MapExits.Count == 0)
        {
            return;
        }

        if (TryHitPreviewWarp(e.GetPosition(PreviewImage), out MapWarp warp, out int x, out int y))
        {
            PreviewImage.Cursor = System.Windows.Input.Cursors.Hand;
            int sourceId = WarpTable.TryParseMapId(_preview?.MapKey, out int parsed)
                ? parsed
                : WarpTable.GuessMapId(_preview?.MapKey);
            string tip = WarpHoverTip.ToLine(warp, _mapNames, _warps, sourceId);
            StatusText.Text = $"Warp ({x},{y})  {tip}. Click to open the destination.";
            UIElement host = (UIElement?)PreviewHost ?? PreviewScroller;
            WarpHoverTip.Show(WarpHoverPopup, WarpHoverText, host, e.GetPosition(host), tip);
            return;
        }

        PreviewImage.Cursor = System.Windows.Input.Cursors.Hand;
        WarpHoverTip.Hide(WarpHoverPopup);
    }

    private void PreviewWarpLeave(object sender, System.Windows.Input.MouseEventArgs e) =>
        WarpHoverTip.Hide(WarpHoverPopup);

    private void TryPreviewWarpClick(MouseButtonEventArgs e)
    {
        if (!TryHitPreviewWarp(e.GetPosition(PreviewImage), out MapWarp warp, out _, out _))
        {
            return;
        }

        OpenViewerWarpDest(warp);
    }

    private bool TryHitPreviewWarp(System.Windows.Point imagePoint, out MapWarp warp, out int tileX, out int tileY)
    {
        warp = default;
        tileX = 0;
        tileY = 0;
        if (_preview?.Map is null || PreviewImage is null)
        {
            return false;
        }

        double zoom = PreviewScale?.ScaleX > 0 ? PreviewScale.ScaleX : 1;
        int px = (int)Math.Floor(imagePoint.X / zoom);
        int py = (int)Math.Floor(imagePoint.Y / zoom);
        if (!MapAssets.TryHit(px, py, _preview.Map, _preview.MapView, out tileX, out tileY))
        {
            return false;
        }

        MapWarp? hit = _warps.At(_preview.MapKey ?? _preview.Map.Name, tileX, tileY);
        if (hit is null)
        {
            foreach (MapWarp exit in _preview.MapExits)
            {
                if (exit.Covers(tileX, tileY))
                {
                    hit = exit;
                    break;
                }
            }
        }

        if (hit is null)
        {
            return false;
        }

        warp = hit.Value;
        return true;
    }

    private void OpenViewerWarpDest(MapWarp warp)
    {
        RememberViewerMap();
        int destId = warp.DestMapId > 0 ? warp.DestMapId : WarpTable.GuessMapId(warp.Destination);
        int landX = warp.DestX;
        int landY = warp.DestY;
        if (!warp.HasLanding)
        {
            int sourceId = WarpTable.TryParseMapId(_preview?.MapKey, out int parsed)
                ? parsed
                : WarpTable.GuessMapId(_preview?.MapKey);
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

        if (!TryJumpViewerMap(destId, warp.Destination, landX, landY))
        {
            StatusText.Text = destId > 0
                ? $"Warp destination map {destId} is not in this client."
                : $"Warp \"{warp.Destination}\" has no dest map id.";
        }
    }

    private bool TryJumpViewerMap(int mapId, string destName, int landX, int landY)
    {
        (CatalogEntryView Catalog, ArchiveEntry Entry)? hit = FindMapEntry(mapId, destName);
        if (hit is null)
        {
            return false;
        }

        _pendingViewerLanding = landX >= 0 && landY >= 0 ? (landX, landY) : null;
        JumpToEntry(hit.Value.Catalog, hit.Value.Entry.Name, hit.Value.Entry.FrameIndex, openGallery: false);
        return true;
    }

    private void WarpCheckClicked(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(_warps, WarpTable.Empty) && _workspace.ActiveClient is null)
        {
            System.Windows.MessageBox.Show("Load a client folder first.");
            return;
        }

        Busy.Run(this, "Checking warps…", () =>
        {
            var window = new WarpCheckWindow(
                _warps,
                _workspace,
                _mapEditors.FirstOrDefault()?.PackedMaps,
                GoWarpSource,
                GoWarpDest,
                _mapNames)
            {
                Owner = this
            };
            window.Show();
        });
    }

    private void SotpEditorClicked(object sender, RoutedEventArgs e)
    {
        MapAssets? tileset = GetMapTilesets()
            .OrderByDescending(s => s.MaxWallId)
            .FirstOrDefault();
        if (tileset is null)
        {
            System.Windows.MessageBox.Show("Need ia.dat (stc walls + sotp.dat) in this client.");
            return;
        }

        Busy.Run(this, "Opening walk flags…", () =>
        {
            var window = new SotpEditorWindow(tileset, _cache, _workspace.ActiveClient) { Owner = this };
            window.Show();
        });
    }

    private void ChangeLogClicked(object sender, RoutedEventArgs e)
    {
        var window = new ChangeLogWindow(_workspace.ActiveClient) { Owner = this };
        window.Show();
    }

    private void AboutClicked(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }

    private void PreviewBackClicked(object sender, RoutedEventArgs e)
    {
        if (_viewerBack.Count == 0)
        {
            return;
        }

        var visit = _viewerBack.Pop();
        UpdatePreviewBackButton();
        _restoringViewer = true;
        try
        {
            _pendingViewerLanding = visit.X >= 0 && visit.Y >= 0 ? (visit.X, visit.Y) : null;
            JumpToEntry(visit.Catalog, visit.Entry, visit.Frame, openGallery: false);
        }
        finally
        {
            _restoringViewer = false;
        }
    }

    private void PreviewWarpLabelsToggled(object sender, RoutedEventArgs e) => LayoutPreviewWarpLabels();

    private void RememberViewerMap()
    {
        if (_restoringViewer
            || ArchiveList.SelectedItem is not CatalogEntryView catalog
            || EntryList.SelectedItem is not ArchiveEntry entry)
        {
            return;
        }

        _viewerBack.Push((catalog, entry.Name, entry.FrameIndex, _pendingViewerLanding?.X ?? 0, _pendingViewerLanding?.Y ?? 0));
        if (_viewerBack.Count > 40)
        {
            var keep = _viewerBack.Reverse().Take(40).Reverse().ToList();
            _viewerBack.Clear();
            foreach (var visit in keep)
            {
                _viewerBack.Push(visit);
            }
        }

        UpdatePreviewBackButton();
    }

    private void UpdatePreviewBackButton()
    {
        if (PreviewBackButton is not null)
        {
            PreviewBackButton.IsEnabled = _viewerBack.Count > 0;
        }
    }

    private void ClearPreviewWarpLabels() => PreviewWarpLabels?.Children.Clear();

    private void HidePreviewArea()
    {
        if (AreaOverlay is not null)
        {
            AreaOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void LayoutPreviewArea()
    {
        if (AreaOverlay is null || AreaOverlayText is null)
        {
            return;
        }

        if (_preview?.Map is null)
        {
            HidePreviewArea();
            return;
        }

        int id = WarpTable.TryParseMapId(_preview.MapKey, out int parsed)
            ? parsed
            : WarpTable.GuessMapId(_preview.MapKey);
        AreaOverlayText.Text = AreaCaption.Format(
            id,
            _mapNames.Area(id),
            _mapNames,
            _preview.MapKey,
            _preview.Map.Width,
            _preview.Map.Height,
            _serverWorld.Get(id));
        AreaOverlay.Visibility = Visibility.Visible;
    }

    private void LayoutPreviewWarpLabels()
    {
        if (PreviewWarpLabels is null)
        {
            return;
        }

        PreviewWarpLabels.Children.Clear();
        if (WarpLabelsBox?.IsChecked != true
            || _preview?.Map is not MapFile map
            || _preview.MapExits.Count == 0
            || PreviewImage.Source is null)
        {
            return;
        }

        if (PreviewImage.Source is BitmapSource bmp)
        {
            PreviewWarpLabels.Width = bmp.PixelWidth;
            PreviewWarpLabels.Height = bmp.PixelHeight;
        }
        int sourceId = WarpTable.TryParseMapId(_preview.MapKey, out int parsed)
            ? parsed
            : WarpTable.GuessMapId(_preview.MapKey);
        foreach (MapWarp warp in _preview.MapExits)
        {
            (int x, int y) = (Math.Min(warp.X1, warp.X2), Math.Min(warp.Y1, warp.Y2));
            if ((uint)x >= (uint)map.Width || (uint)y >= (uint)map.Height)
            {
                continue;
            }

            (int sx, int sy) = MapAssets.TileToScreen(
                x, y, _preview.MapView.OriginX, _preview.MapView.OriginY, _preview.MapView.StepX, _preview.MapView.StepY);
            var label = new TextBlock
            {
                Text = WarpHoverTip.ToLine(warp, _mapNames, _warps, sourceId),
                Foreground = System.Windows.Media.Brushes.White,
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(200, 37, 40, 48)),
                FontSize = 11,
                Padding = new Thickness(3, 1, 3, 1)
            };
            Canvas.SetLeft(label, sx);
            Canvas.SetTop(label, Math.Max(0, sy - 2));
            PreviewWarpLabels.Children.Add(label);
        }
    }

    private (CatalogEntryView Catalog, ArchiveEntry Entry)? FindMapEntry(int mapId, string destName)
    {
        bool Matches(ArchiveEntry entry)
        {
            if (!entry.Extension.Equals(".map", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int id = WarpTable.TryParseMapId(entry.Name, out int parsed) ? parsed : WarpTable.GuessMapId(entry.Name);
            if (mapId > 0 && id == mapId)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(destName))
            {
                return false;
            }

            string stem = Path.GetFileNameWithoutExtension(entry.Name);
            return stem.Equals(destName, StringComparison.OrdinalIgnoreCase)
                || entry.DisplayName.Equals(destName, StringComparison.OrdinalIgnoreCase);
        }

        if (ArchiveList.SelectedItem is CatalogEntryView current)
        {
            ArchiveEntry? listed = _entrySource.FirstOrDefault(Matches);
            if (listed is not null)
            {
                return (current, listed);
            }

            if (_cache is not null)
            {
                try
                {
                    ArchiveEntry? packed = _cache.Get(current).Entries.FirstOrDefault(Matches);
                    if (packed is not null)
                    {
                        return (current, packed);
                    }
                }
                catch
                {
                    // keep searching other catalogs
                }
            }
        }

        if (_cache is null)
        {
            return null;
        }

        foreach (CatalogEntryView catalog in _archives.OfType<CatalogEntryView>())
        {
            try
            {
                ArchiveEntry? entry = _cache.Get(catalog).Entries.FirstOrDefault(Matches);
                if (entry is not null)
                {
                    return (catalog, entry);
                }
            }
            catch
            {
                // skip unreadable archives
            }
        }

        return null;
    }

    private void ApplyPendingViewerLanding()
    {
        if (_pendingViewerLanding is not (int x, int y) || _preview?.Map is null)
        {
            return;
        }

        _pendingViewerLanding = null;
        Dispatcher.BeginInvoke(() => CenterPreviewOnTile(x, y), DispatcherPriority.Loaded);
    }

    private void CenterPreviewOnTile(int x, int y)
    {
        if (PreviewScroller is null || _preview?.Map is null)
        {
            return;
        }

        MapAssets.MapView view = _preview.MapView;
        x = Math.Clamp(x, 0, Math.Max(0, _preview.Map.Width - 1));
        y = Math.Clamp(y, 0, Math.Max(0, _preview.Map.Height - 1));
        (int sx, int sy) = MapAssets.TileToScreen(x, y, view.OriginX, view.OriginY, view.StepX, view.StepY);
        double zoom = PreviewScale?.ScaleX > 0 ? PreviewScale.ScaleX : 1;
        double cx = (sx + view.TileW / 2.0) * zoom;
        double cy = (sy + view.TileH / 2.0) * zoom;
        PreviewScroller.UpdateLayout();
        PreviewScroller.ScrollToHorizontalOffset(Math.Max(0, cx - PreviewScroller.ViewportWidth / 2));
        PreviewScroller.ScrollToVerticalOffset(Math.Max(0, cy - PreviewScroller.ViewportHeight / 2));
        StatusText.Text = $"Arrived ({x},{y}) on {Path.GetFileName(_preview.MapKey ?? _preview.Map.Name)}.";
    }

    private void BindEntryPage()
    {
        if (EntryList is null)
        {
            return;
        }

        int size = Math.Max(1, _entryPageSize);
        int pages = Math.Max(1, (int)Math.Ceiling(_entrySource.Count / (double)size));
        _entryPage = Math.Clamp(_entryPage, 0, pages - 1);
        int start = _entryPage * size;
        int take = Math.Min(size, Math.Max(0, _entrySource.Count - start));
        EntryList.ItemsSource = take <= 0 ? Array.Empty<ArchiveEntry>() : _entrySource.GetRange(start, take);
        if (EntryPager is not null)
        {
            EntryPager.Visibility = _entrySource.Count > size ? Visibility.Visible : Visibility.Collapsed;
        }

        if (EntryPageLabel is not null)
        {
            EntryPageLabel.Text = _entrySource.Count == 0
                ? ""
                : $"{start + 1}–{start + take} of {_entrySource.Count}";
        }
    }

    private bool RevealListed(ArchiveEntry match)
    {
        int index = _entrySource.FindIndex(x =>
            ReferenceEquals(x, match)
            || (x.Name.Equals(match.Name, StringComparison.OrdinalIgnoreCase)
                && x.FrameIndex == match.FrameIndex
                && string.Equals(x.DisplayName, match.DisplayName, StringComparison.OrdinalIgnoreCase)));
        if (index < 0)
        {
            index = _entrySource.FindIndex(x =>
                x.Name.Equals(match.Name, StringComparison.OrdinalIgnoreCase)
                && x.FrameIndex == match.FrameIndex);
        }

        if (index < 0)
        {
            return false;
        }

        int size = Math.Max(1, _entryPageSize);
        _entryPage = index / size;
        BindEntryPage();
        ArchiveEntry shown = _entrySource[index];
        EntryList.SelectedItem = shown;
        EntryList.ScrollIntoView(shown);
        return true;
    }

    private void EntryPrevClicked(object sender, RoutedEventArgs e)
    {
        _entryPage = Math.Max(0, _entryPage - 1);
        BindEntryPage();
    }

    private void EntryNextClicked(object sender, RoutedEventArgs e)
    {
        _entryPage++;
        BindEntryPage();
    }

    private void EntryPageSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ignoreEntryPageSize || !IsLoaded || EntryPageSizeBox?.SelectedItem is not int size)
        {
            return;
        }

        _entryPageSize = Math.Max(1, size);
        BindEntryPage();
    }

    private void GalleryFirstClicked(object sender, RoutedEventArgs e) => _ = LoadGalleryPage(0);

    private void GalleryPrevClicked(object sender, RoutedEventArgs e)
    {
        if (_gallery is not null)
        {
            _ = LoadGalleryPage(_gallery.Page - 1);
        }
    }

    private void GalleryNextClicked(object sender, RoutedEventArgs e)
    {
        if (_gallery is not null)
        {
            _ = LoadGalleryPage(_gallery.Page + 1);
        }
    }

    private void GalleryLastClicked(object sender, RoutedEventArgs e)
    {
        if (_gallery is not null)
        {
            _ = LoadGalleryPage(_gallery.PageCount - 1);
        }
    }

    private void GalleryPageSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ignorePageSize || !IsLoaded || GalleryPageSizeBox?.SelectedItem is not int size)
        {
            return;
        }

        _galleryPageSizePicked = true;
        if (_gallery is null || GalleryPanel?.Visibility != Visibility.Visible)
        {
            return;
        }

        _gallery.PageSize = size;
        _ = LoadGalleryPage(Math.Min(_gallery.Page, _gallery.PageCount - 1));
    }

    private void GalleryPageKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _gallery is null)
        {
            return;
        }

        e.Handled = true;
        if (int.TryParse(GalleryPageBox.Text.Trim(), out int page))
        {
            _ = LoadGalleryPage(page - 1);
        }
    }

    private async Task LoadGalleryPage(int page, string? label = null, CancellationToken? token = null)
    {
        if (_gallery is null || ArchiveList.SelectedItem is not CatalogEntryView)
        {
            return;
        }

        CancellationToken ct;
        if (token is CancellationToken existing)
        {
            ct = existing;
        }
        else
        {
            _galleryCts?.Cancel();
            _galleryCts = new CancellationTokenSource();
            ct = _galleryCts.Token;
        }

        page = Math.Clamp(page, 0, _gallery.PageCount - 1);
        string title = label ?? "Gallery";
        Busy.Show(this, $"{title}: loading page {page + 1} of {_gallery.PageCount}…", determinate: true);
        try
        {
            StatusText.Text = $"{title}: loading page {page + 1} of {_gallery.PageCount}...";
            int take = Math.Min(_gallery.PageSize, Math.Max(1, _gallery.TotalCount - (page * _gallery.PageSize)));
            IProgress<int> progress = Busy.Count(this, take, $"{title}: page {page + 1}");
            await _gallery.FillPageAsync(page, ct, progress);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            GalleryPageBox.Text = (_gallery.Page + 1).ToString();
            GalleryPageLabel.Text = $"of {_gallery.PageCount}   ({_gallery.PageStart}–{_gallery.PageEnd} of {_gallery.TotalCount})";
            StatusText.Text = $"{title}: {_gallery.PageStart}–{_gallery.PageEnd} of {_gallery.TotalCount}  (page {_gallery.Page + 1}/{_gallery.PageCount}). Click a cell to open it.";
            MetaBox.Text = $"{title}\n{_gallery.TotalCount} entries, {_gallery.PageSize} per page\nShowing {_gallery.PageStart}–{_gallery.PageEnd}\nClick a cell to open the solo view";
            if (GalleryHost.Items.Count > 0)
            {
                GalleryHost.ScrollIntoView(GalleryHost.Items[0]);
            }
            UpdateGalleryFrameSlider();
        }
        catch (OperationCanceledException)
        {
            // replaced
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            Busy.Hide(this);
        }
    }

    private void UpdateGalleryFrameSlider()
    {
        if (_gallery is null)
        {
            return;
        }

        _syncingFrame = true;
        int count = Math.Max(1, _gallery.MaxFrameCount);
        _frame = Math.Clamp(_gallery.Frame, 0, count - 1);
        FrameSlider.Maximum = Math.Max(0, count - 1);
        FrameSlider.Value = _frame;
        FrameLabel.Text = $"Frame {_frame + 1} / {count}";
        _syncingFrame = false;
    }

    private void FrameSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingFrame || !IsLoaded)
        {
            return;
        }

        _frame = (int)e.NewValue;
        _playing = false;
        if (GalleryVisible)
        {
            Busy.Run(this, "Loading gallery frames…", () =>
            {
                _gallery!.EnsureAllFrames();
                _gallery.SetFrame(_frame);
                UpdateGalleryFrameSlider();
            });
            return;
        }

        if (_preview?.Cinematic is not null)
        {
            SeekCinematicAudio();
            ShowCinematicFrame();
            return;
        }

        RefreshPreview(showBusy: false);
    }

    private void UpdateActionBar()
    {
        if (PlayPauseButton is null)
        {
            return;
        }

        ArchiveEntry? entry = EntryList?.SelectedItem as ArchiveEntry;
        CatalogEntryView? catalog = ArchiveList?.SelectedItem as CatalogEntryView;
        string ext = (entry?.Extension ?? "").ToLowerInvariant();
        bool gallery = GalleryVisible;
        bool cinematic = _preview?.Cinematic is not null;
        bool audio = IsAudioExt(ext) || _preview?.Audio is not null;
        bool image = gallery || _preview?.Image is not null;
        bool multiFrame = gallery || _preview is { FrameCount: > 1 } || cinematic;
        bool animate = gallery || _preview?.CanAnimate == true || cinematic;
        bool map = ext == ".map" || catalog?.Role == ArchiveRole.Maps;
        bool epf = ext == ".epf";
        bool khan = catalog?.Role == ArchiveRole.PlayerSprites
            || (entry is not null && KhanPieces.TryParse(entry.Name, out _));
        bool paletted = gallery || IsPalettedExt(ext) || map || khan;
        bool table = _preview?.TableHeaders is not null;
        bool hexOnly = _preview?.Hex is not null && _preview.Image is null;
        bool tileArchive = catalog?.Role is ArchiveRole.Walls or ArchiveRole.Floors
            || ListHasExtension(".hpf", ".spf", ".pcx");
        bool sprite = ext is ".epf" or ".mpf" or ".spf";
        bool dataView = table || hexOnly;
        bool canGallery = CountGalleryEntries() >= 2 && !dataView && (!audio || cinematic);
        bool galleryOpen = GalleryPanel?.Visibility == Visibility.Visible;

        if (!canGallery && GalleryBox is not null && (GalleryBox.IsChecked == true || galleryOpen))
        {
            _ignoreGalleryToggle = true;
            GalleryBox.IsChecked = false;
            _ignoreGalleryToggle = false;
            _galleryCts?.Cancel();
            _gallery = null;
            HideGallery(updateBar: false);
        }

        ShowIf(PlayPauseButton, (animate && !audio && !dataView) || cinematic);
        ShowIf(PrevFrameButton, (multiFrame && !audio && !dataView) || cinematic);
        ShowIf(NextFrameButton, (multiFrame && !audio && !dataView) || cinematic);
        ShowIf(TileGridItem, tileArchive && !gallery);
        ShowIf(SheetItem, epf && !gallery);
        ShowIf(ExportPngItem, image && (!audio || cinematic) && !dataView);
        ShowIf(ExtractItem, entry is not null && !gallery);
        bool actions = (tileArchive && !gallery) || (epf && !gallery) || (image && !audio && !dataView) || (entry is not null && !gallery) || gallery || _archives.Count > 0;
        ShowIf(PreviewActionsButton, actions);
        bool canFavorite = !gallery && entry is not null && catalog is not null;
        ShowIf(FavoriteButton, canFavorite);
        if (FavoriteButton is not null && canFavorite)
        {
            bool saved = IsFavorite(catalog!, entry!);
            FavoriteButton.Content = saved ? "★ Unfavorite" : "★ Favorite";
            FavoriteButton.ToolTip = saved
                ? "Remove this entry from this client's favorites."
                : "Save this entry for this client and jump back to it later.";
        }
        ShowIf(PlaySoundButton, audio && !cinematic);
        ShowIf(GalleryBox, canGallery);
        ShowIf(ContactSheetBox, sprite && !gallery);
        ShowIf(AdditiveBox, paletted && !audio && !dataView);
        ShowIf(PalettePanel, paletted && !audio && !dataView);
        ShowIf(TilesetPanel, map);
        ShowIf(MotionPanel, khan && !gallery);
        ShowIf(FramePanel, multiFrame && !audio && !dataView);
        ShowIf(ZoomPanel, image && !gallery && !dataView);
        ShowIf(OverlayGroup, khan && !gallery);
    }

    private void BindColorVariants(IArchive archive, ArchiveEntry entry)
    {
        if (ColorVariantHost is null || ColorVariantLabel is null || ColorVariantScroller is null)
        {
            return;
        }

        bool mpf = entry.Extension.Equals(".mpf", StringComparison.OrdinalIgnoreCase);
        if (!mpf)
        {
            _colorVariantKey = null;
            ColorVariantHost.ItemsSource = null;
            ColorVariantLabel.Visibility = Visibility.Collapsed;
            ColorVariantScroller.Visibility = Visibility.Collapsed;
            return;
        }

        string key = archive.SourcePath + "\0" + entry.Name;
        if (key == _colorVariantKey)
        {
            return;
        }

        _colorVariantKey = key;
        IReadOnlyList<MonsterColorVariant> variants = MonsterColors.For(archive, entry);
        ColorVariantHost.ItemsSource = variants;
        Visibility vis = variants.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ColorVariantLabel.Visibility = vis;
        ColorVariantScroller.Visibility = vis;
    }

    private void ColorVariantClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not MonsterColorVariant variant)
        {
            return;
        }

        if (variant.SiblingFile is not null)
        {
            ArchiveEntry? sibling = _entrySource.FirstOrDefault(entry =>
                entry.Name.Equals(variant.SiblingFile, StringComparison.OrdinalIgnoreCase));
            if (sibling is not null)
            {
                RevealListed(sibling);
                return;
            }

            return;
        }

        if (variant.PaletteName is null || PaletteBox is null)
        {
            return;
        }

        if (!PaletteBox.Items.Contains(variant.PaletteName))
        {
            var pals = PaletteBox.Items.Cast<string>().ToList();
            pals.Add(variant.PaletteName);
            PaletteBox.ItemsSource = pals;
        }

        PaletteBox.SelectedItem = variant.PaletteName;
    }

    private string? SelectedPaletteName()
    {
        if (PaletteBox?.SelectedItem is not string palName
            || string.IsNullOrWhiteSpace(palName)
            || palName.Equals("(auto)", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return palName;
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
            PreviewOptionsChanged(sender, e);
            e.Handled = true;
        }
    }

    private void SkipSharedFloorsClicked(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<MapAssets> sets = GetMapTilesets();
        if (sets.Count == 0)
        {
            return;
        }

        MapAssets current = (TilesetBox?.SelectedItem as MapTilesetOption)?.Assets
            ?? sets.OrderByDescending(s => s.FloorCount).First();
        int shared = sets.Min(s => s.FloorCount);
        int offset = current.FloorCount > shared ? shared : 0;
        if (FloorOffsetBox is not null)
        {
            FloorOffsetBox.Text = offset.ToString();
        }

        PreviewOptionsChanged(sender, e);
    }

    private static bool IsAudioExt(string ext) =>
        ext is ".mp3" or ".wav";

    private static bool IsGalleryExt(string ext)
    {
        ext = ext.ToLowerInvariant();
        return ext is ".epf" or ".mpf" or ".hpf" or ".spf" or ".bmp" or ".pcx" or ".map"
            or ".efa" or ".hea" or ".jpf" or ".jpg" or ".jpeg" or ".png" or ".rgba" or ".bik"
            or ".fnt" or ".lft" or ".upf";
    }

    private static IEnumerable<ArchiveEntry> GalleryEntries(CatalogEntryView catalog, IEnumerable<ArchiveEntry> entries)
    {
        return catalog.Role switch
        {
            ArchiveRole.Floors => entries.Where(FloorTiles.IsFloorName),
            ArchiveRole.Items => entries.Where(LegendItems.IsItemGraphic),
            ArchiveRole.Mobs => entries.Where(e =>
                e.Extension.Equals(".mpf", StringComparison.OrdinalIgnoreCase)
                || e.Extension.Equals(".rgba", StringComparison.OrdinalIgnoreCase)),
            ArchiveRole.Walls => entries.Where(e =>
                e.Extension.Equals(".hpf", StringComparison.OrdinalIgnoreCase)
                || e.Name.StartsWith("stc", StringComparison.OrdinalIgnoreCase)),
            _ => entries
        };
    }

    private int CountGalleryEntries()
    {
        IEnumerable<ArchiveEntry> source = _entrySource;
        if (ArchiveList?.SelectedItem is CatalogEntryView catalog)
        {
            source = GalleryEntries(catalog, source);
        }

        int count = 0;
        foreach (ArchiveEntry entry in source)
        {
            if (IsGalleryExt(entry.Extension) || FloorTiles.IsFloorName(entry) || LegendItems.IsItemGraphic(entry))
            {
                if (++count >= 2)
                {
                    return count;
                }
            }
        }

        return count;
    }

    private static bool IsPalettedExt(string ext) =>
        ext is ".epf" or ".mpf" or ".hpf" or ".spf" or ".pal" or ".bmp" or ".pcx" or ".map"
            or ".upf" or ".fnt" or ".lft" or ".hea";

    private bool ListHasExtension(params string[] extensions)
    {
        int seen = 0;
        foreach (ArchiveEntry entry in _entrySource)
        {
            if (extensions.Any(ext => entry.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (++seen > 80)
            {
                break;
            }
        }

        return false;
    }

    private static void ShowIf(UIElement? element, bool visible)
    {
        if (element is not null)
        {
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void PlayClicked(object sender, RoutedEventArgs e)
    {
        if (GalleryVisible)
        {
            if (!_playing)
            {
                Busy.Run(this, "Loading gallery frames…", () => _gallery!.EnsureAllFrames());
            }

            _playing = !_playing;
            StatusText.Text = _playing
                ? $"Gallery: {_gallery!.Count} images — playing"
                : $"Gallery: {_gallery!.Count} images — paused";
            UpdateGalleryFrameSlider();
            return;
        }

        if (_preview?.Cinematic is BinkClip clip)
        {
            if (_playing)
            {
                _playing = false;
                _player?.Pause();
                return;
            }

            if (_wave is not null && _wave.Position >= Math.Max(0, _wave.Length - 1))
            {
                _wave.Position = 0;
                _frame = 0;
            }
            else if (_frame >= clip.FrameCount - 1)
            {
                _frame = 0;
                SeekCinematicAudio();
            }

            _playing = true;
            if (_player is not null)
            {
                _player.Play();
            }
            else
            {
                StartPreviewAudio(_preview, restart: false);
            }

            ShowCinematicFrame();
            return;
        }

        _playing = !_playing;
    }

    private void PrevClicked(object sender, RoutedEventArgs e)
    {
        if (GalleryVisible)
        {
            _playing = false;
            Busy.Run(this, "Loading gallery frames…", () =>
            {
                _gallery!.EnsureAllFrames();
                _gallery.Retreat();
            });
            UpdateGalleryFrameSlider();
            return;
        }

        if (_preview is { FrameCount: > 0 })
        {
            _playing = false;
            _player?.Pause();
            _frame = (_frame - 1 + _preview.FrameCount) % _preview.FrameCount;
            if (_preview.Cinematic is not null)
            {
                SeekCinematicAudio();
                ShowCinematicFrame();
                return;
            }

            RefreshPreview(showBusy: false);
        }
    }

    private void NextClicked(object sender, RoutedEventArgs e)
    {
        if (GalleryVisible)
        {
            _playing = false;
            Busy.Run(this, "Loading gallery frames…", () =>
            {
                _gallery!.EnsureAllFrames();
                _gallery.Advance();
            });
            UpdateGalleryFrameSlider();
            return;
        }

        if (_preview is { FrameCount: > 0 })
        {
            _playing = false;
            _player?.Pause();
            _frame = (_frame + 1) % _preview.FrameCount;
            if (_preview.Cinematic is not null)
            {
                SeekCinematicAudio();
                ShowCinematicFrame();
                return;
            }

            RefreshPreview(showBusy: false);
        }
    }

    private void TileGridClicked(object sender, RoutedEventArgs e)
    {
        if (ArchiveList.SelectedItem is not CatalogEntryView item || _cache is null)
        {
            return;
        }

        IArchive archive = _cache.Get(item);
        IEnumerable<ArchiveEntry> tiles = archive.Entries.Where(x =>
            x.Extension.Equals(".hpf", StringComparison.OrdinalIgnoreCase) ||
            x.Extension.Equals(".spf", StringComparison.OrdinalIgnoreCase) ||
            x.Extension.Equals(".pcx", StringComparison.OrdinalIgnoreCase));
        _playing = false;
        _ = StartGallery(tiles, "Tile grid");
    }

    private void SheetClicked(object sender, RoutedEventArgs e)
    {
        if (ArchiveList.SelectedItem is not CatalogEntryView item || EntryList.SelectedItem is not ArchiveEntry entry || _cache is null)
        {
            return;
        }

        try
        {
            IArchive archive = _cache.Get(item);
            Palette? pal = PaletteLocator.Named(archive, SelectedPaletteName());

            _preview = PreviewBuilder.UiOrItemSheet(archive, entry, pal);
            _playing = false;
            ShowPreview(_preview);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ExportClicked(object sender, RoutedEventArgs e)
    {
        BitmapSource? image = PreviewImage.Source as BitmapSource ?? _preview?.Image;
        if (image is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "PNG|*.png", FileName = "export.png" };
        if (dialog.ShowDialog() == true)
        {
            WpfImages.SavePng(image, dialog.FileName);
            StatusText.Text = $"Saved {dialog.FileName}";
        }
    }

    private void ExportDataClicked(object sender, RoutedEventArgs e)
    {
        TableDump? dump = CurrentDataDump();
        if (dump is null || dump.Rows.Count == 0)
        {
            System.Windows.MessageBox.Show(this, "Nothing to export from the current list.");
            return;
        }

        string? jsonOverride = null;
#if LOCAL_UNITY
        if (_preview?.Map is MapFile current)
        {
            int id = WarpTable.TryParseMapId(_preview.MapKey, out int parsed)
                ? parsed
                : WarpTable.GuessMapId(_preview.MapKey);
            jsonOverride = UnityJson.Map(current, id, _preview.MapKey ?? current.Name);
        }
#endif

        TableExport.Save(this, dump.Table, dump, sender, StatusText, jsonOverride);
    }

    private TableDump? CurrentDataDump()
    {
        if (_preview?.Map is MapFile map)
        {
            int id = WarpTable.TryParseMapId(_preview.MapKey, out int parsed)
                ? parsed
                : WarpTable.GuessMapId(_preview.MapKey);
            return DataDumps.MapTiles(map, id, Path.GetFileNameWithoutExtension(_preview.MapKey ?? map.Name));
        }

        if (_preview?.TableHeaders is { Count: > 0 } headers && _preview.TableRows is not null)
        {
            return TableDump.Create(
                "Table",
                headers.ToList(),
                _preview.TableRows.Select(row => (IReadOnlyList<object?>)row.Cast<object?>().ToList()));
        }

        if (GalleryVisible && _gallery is not null)
        {
            return TableDump.Create(
                "Gallery",
                ["Name", "Frames"],
                _gallery.Cells.Select(c => (IReadOnlyList<object?>)new object?[] { c.Name, c.FrameCount }));
        }

        if (EntryList?.Items.Count > 0)
        {
            string archive = ArchiveList.SelectedItem is CatalogEntryView cat ? cat.Title : "entries";
            IEnumerable<ArchiveEntry> entries = EntryList.SelectedItems.Count > 1
                ? EntryList.SelectedItems.Cast<ArchiveEntry>()
                : _entrySource;
            return TableDump.Create(
                "Entries",
                ["Archive", "Name", "Extension", "Size"],
                entries.Select(e => (IReadOnlyList<object?>)new object?[] { archive, e.Name, e.Extension, e.Size }));
        }

        if (_archives.Count > 0)
        {
            return TableDump.Create(
                "Archives",
                ["Title", "Role", "Path", "Entries"],
                _archives.OfType<CatalogEntryView>().Select(a => (IReadOnlyList<object?>)new object?[] { a.Title, a.RoleLabel, a.SourcePath, a.EntryCount }));
        }

        return null;
    }

    private void ExtractClicked(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not ArchiveEntry entry)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = entry.Name, Filter = "All files|*.*" };
        if (dialog.ShowDialog() == true)
        {
            File.WriteAllBytes(dialog.FileName, entry.ReadAllBytes());
            StatusText.Text = $"Extracted {entry.Name}";
        }
    }

    private void FfmpegPathClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Locate ffmpeg.exe",
            Filter = "FFmpeg|ffmpeg.exe|Executables|*.exe|All files|*.*",
            FileName = "ffmpeg.exe"
        };
        if (!string.IsNullOrWhiteSpace(_settings.FfmpegPath) && File.Exists(_settings.FfmpegPath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(_settings.FfmpegPath);
            dialog.FileName = Path.GetFileName(_settings.FfmpegPath);
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _settings.FfmpegPath = dialog.FileName;
        _settings.Save();
        FFmpegLocator.Prefer(dialog.FileName);
        StatusText.Text = $"Using FFmpeg at {dialog.FileName}";
        if (EntryList?.SelectedItem is ArchiveEntry { } entry
            && entry.Extension.Equals(".bik", StringComparison.OrdinalIgnoreCase))
        {
            RefreshPreview();
        }
    }

    private void RadVideoPathClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Locate radvideo64.exe",
            Filter = "RAD Video Tools|radvideo64.exe;radvideo.exe;bink.exe|Executables|*.exe|All files|*.*",
            FileName = "radvideo64.exe"
        };
        if (!string.IsNullOrWhiteSpace(_settings.RadVideoPath) && File.Exists(_settings.RadVideoPath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(_settings.RadVideoPath);
            dialog.FileName = Path.GetFileName(_settings.RadVideoPath);
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _settings.RadVideoPath = dialog.FileName;
        _settings.Save();
        BinkEncoderLocator.Prefer(dialog.FileName);
        StatusText.Text = $"Using RAD Video Tools at {dialog.FileName}";
    }

    private void ImportCinematicClicked(object sender, RoutedEventArgs e)
    {
        if (_cache is null)
        {
            System.Windows.MessageBox.Show(this, "Load a client folder first.", "Import cinematic");
            return;
        }

        string? name = EntryList?.SelectedItem is ArchiveEntry entry
            && entry.Extension.Equals(".bik", StringComparison.OrdinalIgnoreCase)
            ? entry.Name
            : null;
        CatalogEntry? catalog = ArchiveList?.SelectedItem as CatalogEntry;
        var window = new ImportCinematicWindow(_cache, _archives.ToList(), catalog, name) { Owner = this };
        if (window.ShowDialog() == true)
        {
            FillEntries();
            StatusText.Text = "Imported cinematic into the DAT.";
        }
    }

    private void PlaySoundClicked(object sender, RoutedEventArgs e)
    {
        if (_preview?.Audio is null)
        {
            RefreshPreview();
        }

        StartPreviewAudio(_preview, restart: true);
    }

    private void StartPreviewAudio(PreviewResult? result, bool restart)
    {
        if (result?.Audio is null)
        {
            return;
        }

        try
        {
            if (!restart && _player is not null)
            {
                _player.Play();
                return;
            }

            StopAudio();
            var ms = new MemoryStream(result.Audio);
            _wave = result.AudioName?.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) == true
                ? new WaveFileReader(ms)
                : new Mp3FileReader(ms);
            _player = new WaveOutEvent();
            _player.Init(_wave);
            _player.Play();
        }
        catch (Exception ex)
        {
            StopAudio();
            StatusText.Text = ex.Message;
        }
    }

    private void StopAudio()
    {
        _player?.Stop();
        _player?.Dispose();
        _wave?.Dispose();
        _player = null;
        _wave = null;
    }

    private void ApplyPreviewClock(PreviewResult? result)
    {
        if (_timer is null)
        {
            return;
        }

        if (result?.Cinematic is BinkClip clip)
        {
            int ms = result.Audio is not null ? 16 : Math.Max(16, clip.FrameDelayMs);
            _timer.Interval = TimeSpan.FromMilliseconds(ms);
            return;
        }

        _timer.Interval = TimeSpan.FromMilliseconds(120);
    }

    private void BindCinematicSurface(BinkClip clip, PixelBuffer? pixels)
    {
        _cinematicPixels = pixels is { } ready && ready.Width == clip.Width && ready.Height == clip.Height
            ? ready
            : clip.ReadFrame(Math.Clamp(_frame, 0, clip.FrameCount - 1));
        _cinematicBmp = WpfImages.ToWriteable(_cinematicPixels);
    }

    private void AdvanceCinematic()
    {
        if (_preview?.Cinematic is not BinkClip clip || !_playing)
        {
            return;
        }

        if (_wave is not null)
        {
            double fps = clip.FramesPerSecond <= 0 ? 15 : clip.FramesPerSecond;
            _frame = Math.Clamp((int)(_wave.CurrentTime.TotalSeconds * fps), 0, clip.FrameCount - 1);
            if (_wave.Position >= _wave.Length)
            {
                _playing = false;
                _frame = clip.FrameCount - 1;
                _player?.Pause();
            }
        }
        else if (_frame >= clip.FrameCount - 1)
        {
            _playing = false;
        }
        else
        {
            _frame++;
        }

        ShowCinematicFrame();
    }

    private void SeekCinematicAudio()
    {
        if (_preview?.Cinematic is not BinkClip clip || _wave is null)
        {
            return;
        }

        double fps = clip.FramesPerSecond <= 0 ? 15 : clip.FramesPerSecond;
        try
        {
            _wave.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(_frame, 0, clip.FrameCount - 1) / fps);
        }
        catch
        {
            // some WAV streams cannot seek
        }
    }

    private void ShowCinematicFrame()
    {
        if (_preview?.Cinematic is not BinkClip clip)
        {
            return;
        }

        try
        {
            _cinematicPixels ??= new PixelBuffer(clip.Width, clip.Height);
            if (_cinematicPixels.Width != clip.Width || _cinematicPixels.Height != clip.Height)
            {
                _cinematicPixels = new PixelBuffer(clip.Width, clip.Height);
            }

            clip.CopyFrame(_frame, _cinematicPixels);
            if (_cinematicBmp is null
                || _cinematicBmp.PixelWidth != clip.Width
                || _cinematicBmp.PixelHeight != clip.Height)
            {
                _cinematicBmp = WpfImages.ToWriteable(_cinematicPixels);
                PreviewImage.Source = _cinematicBmp;
            }
            else
            {
                WpfImages.UpdateFull(_cinematicBmp, _cinematicPixels);
            }

            PreviewImage.Visibility = Visibility.Visible;
            UpdateFrameUi(_preview);
        }
        catch (Exception ex)
        {
            _playing = false;
            StatusText.Text = ex.Message;
        }
    }

    private static string? FirstExisting(params string?[] paths)
    {
        foreach (string? path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    string full = Path.GetFullPath(path);
                    if (Directory.Exists(full))
                    {
                        return full;
                    }
                }
                catch
                {
                    // skip
                }
            }
        }

        return null;
    }
}

public sealed class SearchHit
{
    public SearchHit(CatalogEntryView archive, string entryName, int? frameIndex, string label)
    {
        Archive = archive;
        EntryName = entryName;
        FrameIndex = frameIndex;
        Label = label;
    }

    public CatalogEntryView Archive { get; }
    public string EntryName { get; }
    public int? FrameIndex { get; }
    public string Label { get; }
}

public sealed class CatalogEntryView : CatalogEntry
{
    public CatalogEntryView(CatalogEntry inner)
    {
        SourcePath = inner.SourcePath;
        DisplayName = inner.DisplayName;
        Variant = inner.Variant;
        Role = inner.Role;
        IsFolder = inner.IsFolder;
        EntryCount = inner.EntryCount;
    }

    public string RoleLabel => ArchiveRoleNames.Label(Role);
}
