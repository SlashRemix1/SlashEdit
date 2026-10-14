using System.Windows;
using System.Windows.Controls;
using DarkAges.Formats.Data;
using DarkAges.Formats.Imaging;
using DarkAges.Viewer.Imaging;
using DarkAges.Viewer.Maps;

namespace DarkAges.Viewer;

public partial class OverlaySourceWindow : Window
{
    private readonly List<OverlaySourceItem> _all;

    public OverlaySourceWindow(IEnumerable<OverlaySourceItem> items)
    {
        InitializeComponent();
        _all = items.ToList();
        BindList();
        if (SourceList.Items.Count > 0)
        {
            SourceList.SelectedIndex = 0;
        }
    }

    public PixelBuffer? Pixels { get; private set; }
    public OverlayApplyMode Mode { get; private set; } = OverlayApplyMode.Entire;
    public string? ChosenName { get; private set; }

    private void BindList()
    {
        string filter = FilterBox?.Text?.Trim() ?? "";
        IEnumerable<OverlaySourceItem> rows = _all;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            rows = rows.Where(i => i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        SourceList.ItemsSource = rows.Take(400).ToList();
    }

    private void FilterChanged(object sender, TextChangedEventArgs e) => BindList();

    private void SourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceList.SelectedItem is not OverlaySourceItem item)
        {
            Pixels = null;
            PreviewImage.Source = null;
            PreviewLabel.Text = "Select an effect.";
            return;
        }

        PixelBuffer? pixels = OverlayGraphic.TryRender(item.Archive, item.Entry);
        Pixels = pixels;
        ChosenName = item.Name;
        if (pixels is null)
        {
            PreviewImage.Source = null;
            PreviewLabel.Text = item.Name + "\nCould not decode.";
            return;
        }

        PreviewImage.Source = WpfImages.Checkerboard(
            Math.Min(160, Math.Max(32, pixels.Width)),
            Math.Min(160, Math.Max(32, pixels.Height)),
            pixels.Contain(160, 160));
        PreviewLabel.Text = $"{item.Name}\n{pixels.Width}×{pixels.Height}";
    }

    private void ApplyEntireClicked(object sender, RoutedEventArgs e) => Finish(OverlayApplyMode.Entire);

    private void SourceDoubleClicked(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        Finish(OverlayApplyMode.Entire);

    private void StampClicked(object sender, RoutedEventArgs e) => Finish(OverlayApplyMode.Stamp);

    private void Finish(OverlayApplyMode mode)
    {
        if (Pixels is null)
        {
            System.Windows.MessageBox.Show(this, "Select a graphic that can be decoded.", "Apply overlay");
            return;
        }

        Mode = mode;
        DialogResult = true;
    }
}

public enum OverlayApplyMode
{
    Entire,
    Stamp
}

public sealed record OverlaySourceItem(string Name, IArchive Archive, ArchiveEntry Entry);
