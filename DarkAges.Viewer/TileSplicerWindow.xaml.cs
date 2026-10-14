using System.IO;
using System.Windows;
using System.Windows.Controls;
using DarkAges.Formats.Catalog;
using DarkAges.Formats.Data;
using DarkAges.Formats.Drawing;
using DarkAges.Formats.Imaging;
using DarkAges.Viewer.Imaging;
using DarkAges.Viewer.Services;

namespace DarkAges.Viewer;

public partial class TileSplicerWindow : Window
{
    private readonly ArchiveCache? _cache;
    private readonly List<CatalogEntry> _catalogs;
    private PixelBuffer? _sheet;
    private IReadOnlyList<TileSlice> _slices = [];

    public TileSplicerWindow(ArchiveCache? cache, IReadOnlyList<CatalogEntry> catalogs)
    {
        InitializeComponent();
        _cache = cache;
        _catalogs = catalogs.Where(c => DatSave.IsWritable(c)).ToList();
        LayoutBox.ItemsSource = Enum.GetValues<TileSheetLayout>();
        LayoutBox.SelectedItem = TileSheetLayout.IsoDiamond;
        CatalogBox.ItemsSource = _catalogs;
        CatalogEntry? floors = _catalogs.FirstOrDefault(c => c.Role == ArchiveRole.Floors)
            ?? _catalogs.FirstOrDefault();
        if (floors is not null)
        {
            CatalogBox.SelectedItem = floors;
        }
    }

    private void OpenClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.png;*.bmp;*.jpg;*.jpeg|All files|*.*" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        PixelBuffer? pixels = WpfImages.TryDecodeImage(File.ReadAllBytes(dialog.FileName));
        if (pixels is null)
        {
            System.Windows.MessageBox.Show(this, "Could not read that image.", "Splicer");
            return;
        }

        _sheet = pixels;
        RefreshPreview();
        StatusText.Text = $"{pixels.Width}×{pixels.Height}  {Path.GetFileName(dialog.FileName)}";
    }

    private void OptionsChanged(object sender, RoutedEventArgs e) => RefreshPreview();

    private void RefreshPreview()
    {
        if (_sheet is null)
        {
            return;
        }

        TileSheetLayout layout = SelectedLayout();
        int.TryParse(OriginXBox.Text, out int ox);
        int.TryParse(OriginYBox.Text, out int oy);
        int.TryParse(ColsBox.Text, out int cols);
        int.TryParse(RowsBox.Text, out int rows);
        int.TryParse(WallHBox.Text, out int wallH);
        _slices = TileSplicer.Extract(_sheet, layout, ox, oy, cols, rows, wallH);
        PixelBuffer overlay = TileSplicer.OverlayGrid(_sheet, layout, ox, oy, cols, rows, wallH);
        PreviewImage.Source = WpfImages.ToBitmap(overlay);
        if (StatusText is not null)
        {
            StatusText.Text = $"{_slices.Count} tiles  layout {layout}";
        }
    }

    private void ImportFloorsClicked(object sender, RoutedEventArgs e)
    {
        if (_cache is null || CatalogBox.SelectedItem is not CatalogEntry catalog)
        {
            System.Windows.MessageBox.Show(this, "Pick a writable DAT.", "Splicer");
            return;
        }

        RefreshPreview();
        if (_slices.Count == 0)
        {
            System.Windows.MessageBox.Show(this, "No tiles found on that sheet.", "Splicer");
            return;
        }

        string atlasName = (AtlasBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "TILEA.BMP";
        int.TryParse(StartBox.Text, out int start);
        start = Math.Max(0, start);
        try
        {
            IArchive archive = _cache.Get(catalog);
            ArchiveEntry? entry = archive.GetEntry(atlasName) ?? archive.GetEntry("TILEA.BMP");
            string name = entry?.Name ?? atlasName;
            byte[] atlas = entry is not null ? FloorTiles.LoadAtlas(entry) : [];
            Palette pal = PaletteForFloor(archive, name, start);
            atlas = TileSplicer.ImportFloors(atlas, _slices, pal, start, DitherBox.IsChecked == true);
            DatSave.Write(_cache, catalog, name, atlas);
            StatusText.Text = $"Imported {_slices.Count} floors into {name} at tile {start + 1}.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Import floors");
        }
    }

    private void ImportWallsClicked(object sender, RoutedEventArgs e)
    {
        if (_cache is null || CatalogBox.SelectedItem is not CatalogEntry catalog)
        {
            System.Windows.MessageBox.Show(this, "Pick a writable DAT (ia / walls).", "Splicer");
            return;
        }

        LayoutBox.SelectedItem = TileSheetLayout.WallColumns;
        RefreshPreview();
        if (_slices.Count == 0)
        {
            System.Windows.MessageBox.Show(this, "No wall columns found.", "Splicer");
            return;
        }

        int.TryParse(WallIdBox.Text, out int wallId);
        wallId = Math.Max(1, wallId);
        try
        {
            IArchive archive = _cache.Get(catalog);
            var files = new List<(string Name, byte[] Data)>();
            for (int i = 0; i < _slices.Count; i++)
            {
                int id = wallId + i;
                Palette pal = PaletteLocator.ForWallTile(archive, id) ?? PaletteLocator.Find(archive) ?? new Palette();
                byte[] indexed = TileSplicer.ToWallTile(_slices[i].Pixels, pal, DitherBox.IsChecked == true);
                files.Add(($"stc{id:D5}.hpf", HpfWriter.Write(new byte[8], indexed)));
            }

            DatSave.Write(_cache, catalog, files);
            StatusText.Text = $"Wrote {files.Count} walls starting at stc{wallId:D5}.hpf.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Import walls");
        }
    }

    private TileSheetLayout SelectedLayout() =>
        LayoutBox.SelectedItem is TileSheetLayout layout ? layout : TileSheetLayout.IsoDiamond;

    private static Palette PaletteForFloor(IArchive archive, string atlasName, int start)
    {
        string stem = Path.GetFileNameWithoutExtension(atlasName);
        if (stem.Equals("TILEAS", StringComparison.OrdinalIgnoreCase))
        {
            return PaletteLocator.ForSnowFloor(archive) ?? PaletteLocator.Find(archive) ?? new Palette();
        }

        return PaletteLocator.ForFloorTile(archive, start + 1) ?? PaletteLocator.Find(archive) ?? new Palette();
    }
}
