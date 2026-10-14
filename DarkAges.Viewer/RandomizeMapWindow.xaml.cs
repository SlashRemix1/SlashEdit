using System.Windows;
using System.Windows.Controls;
using DarkAges.Formats.Imaging;
using DarkAges.Formats.Maps;
using DarkAges.Viewer.Imaging;
using DarkAges.Viewer.Maps;

namespace DarkAges.Viewer;

public partial class RandomizeMapWindow : Window
{
    private readonly MapAssets _assets;
    private readonly IReadOnlyList<string> _folders;
    private MapCatalogStats? _stats;

    public RandomizeMapWindow(MapAssets assets, IEnumerable<string> mapFolders, int width, int height, int floorId, int wallId)
    {
        InitializeComponent();
        _assets = assets;
        _folders = mapFolders.ToList();
        WidthBox.Text = Math.Clamp(width, 4, MapFile.MaxDimension).ToString();
        HeightBox.Text = Math.Clamp(height, 4, MapFile.MaxDimension).ToString();
        FloorBox.Text = Math.Max(1, floorId).ToString();
        WallBox.Text = wallId > 2 ? wallId.ToString() : "4209";
        Loaded += (_, _) =>
        {
            _stats = MapCatalogStats.Load(_folders);
            ScanLabel.Text = _stats.MapsSkipped > 0
                ? $"Read {_stats.MapsScanned} maps (skipped {_stats.MapsSkipped} wall-spam storage maps), {_stats.PairKinds} wall-pair presets."
                : $"Read {_stats.MapsScanned} maps, {_stats.PairKinds} wall-pair presets.";
            RefreshPreviews();
        };
    }

    public MapGeneratorOptions Options { get; private set; } = new();
    public MapCatalogStats Stats => _stats ?? MapCatalogStats.Load(_folders);

    private void BuildingChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BuildingLabel is not null)
        {
            BuildingLabel.Text = ((int)BuildingSlider.Value).ToString();
        }
    }

    private void PathChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PathLabel is not null)
        {
            PathLabel.Text = ((int)PathSlider.Value).ToString();
        }
    }

    private void IdsChanged(object sender, TextChangedEventArgs e) => RefreshPreviews();

    private void RefreshPreviews()
    {
        if (!IsLoaded || FloorPreview is null)
        {
            return;
        }

        int.TryParse(FloorBox.Text.Trim(), out int floor);
        int.TryParse(PathBox.Text.Trim(), out int path);
        int.TryParse(WallBox.Text.Trim(), out int wall);
        PixelBuffer? floorImg = _assets.Floor(Math.Max(1, floor), snow: false);
        FloorPreview.Source = floorImg is null ? null : WpfImages.ToBitmap(floorImg);

        MapCatalogStats stats = Stats;
        WallStyle style = stats.ResolveWalls(wall, _assets.WallIds);
        PixelBuffer? left = _assets.Wall(style.CornerLeft);
        PixelBuffer? right = _assets.Wall(style.CornerRight);
        WallPreview.Source = CompositeWalls(left, right);
        ushort groundId = (ushort)Math.Max(1, floor);
        ushort pathId = MapGenerator.ResolveStyledPath(path > 1 ? (ushort)path : (ushort)0, groundId, _assets, stats);
        if (FloorBox is not null)
        {
            FloorBox.ToolTip = stats.DescribeFloors(groundId, pathId);
        }

        WallHint.Text =
            $"Seed family L{style.CornerLeft}/R{style.CornerRight}  catalog {stats.PairKinds} pairs (mixed per building)  {stats.DescribeFloors(groundId, pathId)}";
    }

    private static System.Windows.Media.Imaging.BitmapSource? CompositeWalls(PixelBuffer? left, PixelBuffer? right)
    {
        if (left is null && right is null)
        {
            return null;
        }

        int w = (left?.Width ?? 28) + (right?.Width ?? 28);
        int h = Math.Max(left?.Height ?? 1, right?.Height ?? 1);
        var canvas = new PixelBuffer(w, h);
        if (left is not null)
        {
            canvas.Blit(left, 0, h - left.Height);
        }

        if (right is not null)
        {
            canvas.Blit(right, left?.Width ?? 28, h - right.Height);
        }

        return WpfImages.ToBitmap(canvas);
    }

    private void GenerateClicked(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WidthBox.Text.Trim(), out int width) || width < 4 || width > MapFile.MaxDimension
            || !int.TryParse(HeightBox.Text.Trim(), out int height) || height < 4 || height > MapFile.MaxDimension)
        {
            System.Windows.MessageBox.Show(this, $"Width and height must be between 4 and {MapFile.MaxDimension}.");
            return;
        }

        ushort.TryParse(FloorBox.Text.Trim(), out ushort floor);
        ushort.TryParse(PathBox.Text.Trim(), out ushort path);
        int.TryParse(WallBox.Text.Trim(), out int wall);
        int.TryParse(SeedBox.Text.Trim(), out int seed);
        Options = new MapGeneratorOptions
        {
            Width = width,
            Height = height,
            Floor = floor == 0 ? (ushort)1 : floor,
            PathFloor = path,
            WallSeed = wall,
            BuildingCount = (int)BuildingSlider.Value,
            PathCount = (int)PathSlider.Value,
            Seed = seed
        };
        DialogResult = true;
    }
}
