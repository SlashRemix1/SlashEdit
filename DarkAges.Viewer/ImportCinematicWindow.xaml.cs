using System.IO;
using System.Windows;
using DarkAges.Formats.Catalog;
using DarkAges.Formats.Data;
using DarkAges.Viewer.Preview;
using DarkAges.Viewer.Services;

namespace DarkAges.Viewer;

public partial class ImportCinematicWindow : Window
{
    private readonly ArchiveCache? _cache;

    public ImportCinematicWindow(
        ArchiveCache? cache,
        IReadOnlyList<CatalogEntry> catalogs,
        CatalogEntry? preferredCatalog = null,
        string? preferredName = null)
    {
        InitializeComponent();
        _cache = cache;
        var writable = catalogs.Where(DatSave.IsWritable).ToList();
        CatalogBox.ItemsSource = writable;
        CatalogEntry? cinematic = preferredCatalog is not null && DatSave.IsWritable(preferredCatalog)
            ? preferredCatalog
            : writable.FirstOrDefault(c => c.Role == ArchiveRole.Cinematics) ?? writable.FirstOrDefault();
        if (cinematic is not null)
        {
            CatalogBox.SelectedItem = cinematic;
        }

        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            NameBox.Text = Path.GetFileNameWithoutExtension(preferredName) + ".bik";
        }

        RefreshHint();
    }

    private void BrowseClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Video|*.bik;*.mp4;*.avi;*.mkv;*.mov;*.webm;*.wmv|Bink|*.bik|MP4|*.mp4|AVI|*.avi|All files|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        PathBox.Text = dialog.FileName;
        if (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text.Equals("intro.bik", StringComparison.OrdinalIgnoreCase))
        {
            NameBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName) + ".bik";
        }

        RefreshHint();
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        string source = PathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            System.Windows.MessageBox.Show(this, "Pick a video or .bik file.", "Import cinematic");
            return;
        }

        if (CatalogBox.SelectedItem is not CatalogEntry catalog || _cache is null)
        {
            System.Windows.MessageBox.Show(this, "Pick a writable DAT.", "Import cinematic");
            return;
        }

        string name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Path.GetFileNameWithoutExtension(source) + ".bik";
        }

        if (!name.EndsWith(".bik", StringComparison.OrdinalIgnoreCase))
        {
            name = Path.GetFileNameWithoutExtension(name) + ".bik";
        }

        try
        {
            byte[] data = default!;
            Busy.Run(this, "Importing cinematic…", () => data = BinkImporter.Import(source));
            DatSave.Write(_cache, catalog, name, data);
            DialogResult = true;
            System.Windows.MessageBox.Show(this, $"Wrote {name} into {catalog.DisplayName}.", "Import cinematic");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Import cinematic");
        }
    }

    private void RefreshHint()
    {
        bool bik = BinkImporter.IsBink(PathBox.Text);
        string? rad = BinkEncoderLocator.Find();
        if (bik || string.IsNullOrWhiteSpace(PathBox.Text))
        {
            ToolHint.Text = rad is null
                ? "Existing .bik files can be saved without RAD. Conversion from MP4/AVI needs radvideo64.exe."
                : "RAD Video Tools: " + rad;
            return;
        }

        ToolHint.Text = rad is null
            ? BinkEncoderLocator.InstallHint
            : "Will compress with " + rad;
    }
}
