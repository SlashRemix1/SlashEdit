using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using DarkAges.Formats.Catalog;
using DarkAges.Formats.Imaging;
using DarkAges.Viewer.Imaging;
using DarkAges.Viewer.Services;

namespace DarkAges.Viewer;

public partial class ReverseImageSearchWindow : Window
{
    private readonly ArchiveCache _cache;
    private readonly IReadOnlyList<CatalogEntry> _catalogs;
    private readonly Action<IReadOnlyList<ImageMatchHit>> _showGroup;
    private readonly Action<ImageMatchHit> _goTo;
    private readonly ObservableCollection<HitRow> _hits = [];
    private PixelBuffer? _query;
    private CancellationTokenSource? _cts;
    private bool _running;

    public ReverseImageSearchWindow(
        ArchiveCache cache,
        IReadOnlyList<CatalogEntry> catalogs,
        Action<IReadOnlyList<ImageMatchHit>> showGroup,
        Action<ImageMatchHit> goTo)
    {
        InitializeComponent();
        _cache = cache;
        _catalogs = catalogs;
        _showGroup = showGroup;
        _goTo = goTo;
        HitList.ItemsSource = _hits;
        BlockBox.ItemsSource = new[]
        {
            new BlockChoice(1, "1 px (fine)"),
            new BlockChoice(2, "2 px"),
            new BlockChoice(4, "4 px"),
            new BlockChoice(8, "8 px (fast)"),
            new BlockChoice(10, "10 px"),
            new BlockChoice(16, "16 px (coarse)")
        };
        BlockBox.DisplayMemberPath = nameof(BlockChoice.Label);
        BlockBox.SelectedIndex = 3; // 8 px
    }

    private sealed record BlockChoice(int Size, string Label);

    private float ReadMinScore()
    {
        string raw = (ScoreBox?.Text ?? "0.40").Trim();
        if (raw.EndsWith('%'))
        {
            raw = raw[..^1].Trim();
        }

        if (!float.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value)
            && !float.TryParse(raw, out value))
        {
            return 0.40f;
        }

        // Allow "40" or "40%" as percent.
        if (value > 1f)
        {
            value /= 100f;
        }

        return Math.Clamp(value, 0f, 1f);
    }

    private void BrowseClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            LoadQuery(dialog.FileName);
        }
    }

    private void WindowDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void WindowDropped(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return;
        }

        LoadQuery(files[0]);
    }

    private void LoadQuery(string path)
    {
        try
        {
            byte[] data = File.ReadAllBytes(path);
            PixelBuffer? pixels = WpfImages.TryDecodeImage(data);
            if (pixels is null)
            {
                System.Windows.MessageBox.Show(this, "Could not decode that image.");
                return;
            }

            _query = pixels;
            PathBox.Text = path;
            QueryImage.Source = WpfImages.ToBitmap(pixels.Contain(400, 400));
            DropHint.Visibility = Visibility.Collapsed;
            StatusLabel.Text = $"{pixels.Width}×{pixels.Height} — click Search to scan the loaded client.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Find from image");
        }
    }

    private async void SearchClicked(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            _cts?.Cancel();
            return;
        }

        if (_query is null)
        {
            System.Windows.MessageBox.Show(this, "Drop or browse to an image first.");
            return;
        }

        if (_catalogs.Count == 0)
        {
            System.Windows.MessageBox.Show(this, "Load a client folder first.");
            return;
        }

        float minScore = ReadMinScore();
        ScoreBox.Text = minScore.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        int blockSize = BlockBox.SelectedItem is BlockChoice block ? block.Size : 8;
        var options = new ReverseImageSearchOptions
        {
            Items = ItemsBox.IsChecked == true,
            Players = PlayersBox.IsChecked == true,
            Composites = CompositesBox.IsChecked == true,
            Ui = UiBox.IsChecked == true,
            Mobs = MobsBox.IsChecked == true,
            Effects = EffectsBox.IsChecked == true,
            MinScore = minScore,
            BlockSize = blockSize
        };

        _running = true;
        _cts = new CancellationTokenSource();
        SearchButton.Content = "Cancel";
        ShowGroupButton.IsEnabled = false;
        GoButton.IsEnabled = false;
        _hits.Clear();
        ProgressBar.IsIndeterminate = true;
        var progress = new Progress<string>(line => StatusLabel.Text = line);

        try
        {
            PixelBuffer query = _query;
            CancellationToken ct = _cts.Token;
            IReadOnlyList<ImageMatchHit> hits = await Task.Run(
                () => ReverseImageSearch.Search(query, _catalogs, _cache, options, progress, ct),
                ct);
            foreach (ImageMatchHit hit in hits)
            {
                _hits.Add(HitRow.From(hit));
            }

            StatusLabel.Text = ct.IsCancellationRequested
                ? "Search cancelled."
                : hits.Count == 0
                    ? "No strong matches. Try a tighter crop of the item, enable more categories, or lower Min score slightly."
                    : $"Found {hits.Count} match(es) (best {hits[0].Score:0%}). Click through thumbs — lower scores are less reliable.";
            ShowGroupButton.IsEnabled = hits.Count > 0;
            GoButton.IsEnabled = HitList.SelectedItem is not null;
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "Search cancelled.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = ex.Message;
            System.Windows.MessageBox.Show(this, ex.Message, "Find from image");
        }
        finally
        {
            _running = false;
            SearchButton.Content = "Search";
            ProgressBar.IsIndeterminate = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void HitSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        GoButton.IsEnabled = HitList.SelectedItem is HitRow;

    private void HitDoubleClicked(object sender, MouseButtonEventArgs e) => GoClicked(sender, e);

    private void GoClicked(object sender, RoutedEventArgs e)
    {
        if (HitList.SelectedItem is not HitRow row)
        {
            return;
        }

        _goTo(row.Hit);
    }

    private void ShowGroupClicked(object sender, RoutedEventArgs e)
    {
        if (_hits.Count == 0)
        {
            return;
        }

        _showGroup(_hits.Select(h => h.Hit).ToList());
    }

    private void CloseClicked(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            _cts?.Cancel();
            return;
        }

        Close();
    }

    private sealed class HitRow
    {
        public required ImageMatchHit Hit { get; init; }
        public required string Title { get; init; }
        public required string Subtitle { get; init; }
        public required string ScoreText { get; init; }
        public BitmapSource? ThumbImage { get; init; }

        public static HitRow From(ImageMatchHit hit) => new()
        {
            Hit = hit,
            Title = hit.IsComposite ? $"Composite · {hit.Title}" : hit.Title,
            Subtitle = hit.IsComposite
                ? $"{hit.Catalog.Title} → {hit.Entry.DisplayName}"
                : hit.Catalog.Title,
            ScoreText = $"{hit.Score:0%}",
            ThumbImage = hit.Thumb is null ? null : WpfImages.ToBitmap(hit.Thumb)
        };
    }
}
