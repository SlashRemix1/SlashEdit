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

public partial class ConvertWindow : Window
{
    private readonly ArchiveCache? _cache;
    private readonly List<CatalogEntry> _catalogs;
    private PixelBuffer? _source;
    private string? _sourceName;

    public ConvertWindow(ArchiveCache? cache, IReadOnlyList<CatalogEntry> catalogs)
    {
        InitializeComponent();
        _cache = cache;
        _catalogs = catalogs.Where(c => DatSave.IsWritable(c)).ToList();
        FormatBox.ItemsSource = Enum.GetValues<GraphicTarget>();
        FormatBox.SelectedItem = GraphicTarget.Epf;
        BlendBox.ItemsSource = Enum.GetValues<EfaBlend>();
        BlendBox.SelectedItem = EfaBlend.Additive;
        CatalogBox.ItemsSource = _catalogs;
        if (_catalogs.Count > 0)
        {
            CatalogBox.SelectedIndex = 0;
        }

        LoadPalettes();
        NameBox.Text = "new001.epf";
    }

    private void LoadPalettes()
    {
        var pals = new List<PalChoice> { new("(quantize new 256-color pal)", null) };
        if (_cache is not null)
        {
            foreach (CatalogEntry catalog in _catalogs)
            {
                try
                {
                    IArchive archive = _cache.Get(catalog);
                    foreach (ArchiveEntry entry in PaletteLocator.Palettes(archive))
                    {
                        pals.Add(new PalChoice($"{entry.Name} ({catalog.DisplayName})", entry.Name, catalog));
                    }
                }
                catch
                {
                    // skip
                }
            }
        }

        PaletteBox.ItemsSource = pals;
        PaletteBox.SelectedIndex = 0;
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
            System.Windows.MessageBox.Show(this, "Could not read that image.", "Convert");
            return;
        }

        _source = pixels;
        _sourceName = Path.GetFileNameWithoutExtension(dialog.FileName);
        PreviewImage.Source = WpfImages.ToBitmap(pixels);
        if (FormatBox.SelectedItem is GraphicTarget target)
        {
            NameBox.Text = _sourceName + GraphicConverter.Convert(pixels, target).Extension;
        }

        StatusText.Text = $"{pixels.Width}×{pixels.Height}  {Path.GetFileName(dialog.FileName)}";
    }

    private void OptionsChanged(object sender, RoutedEventArgs e) => RefreshName();

    private void RefreshName()
    {
        if (FormatBox.SelectedItem is not GraphicTarget target)
        {
            return;
        }

        string ext = GraphicConverter.Extension(target);
        string stem = string.IsNullOrWhiteSpace(_sourceName) ? "new001" : _sourceName;
        if (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
        {
            NameBox.Text = stem + ext;
        }
    }

    private void SaveDatClicked(object sender, RoutedEventArgs e)
    {
        if (!TryConvert(out GraphicConvertResult result, out _))
        {
            return;
        }

        if (CatalogBox.SelectedItem is not CatalogEntry catalog || _cache is null)
        {
            System.Windows.MessageBox.Show(this, "Pick a writable DAT.", "Convert");
            return;
        }

        string name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "new001" + result.Extension;
        }

        if (!name.EndsWith(result.Extension, StringComparison.OrdinalIgnoreCase))
        {
            name = Path.GetFileNameWithoutExtension(name) + result.Extension;
        }

        try
        {
            var files = new List<(string Name, byte[] Data)> { (name, result.Data) };
            if (result.PaletteBytes is not null && PaletteBox.SelectedItem is PalChoice { EntryName: null })
            {
                files.Add((Path.GetFileNameWithoutExtension(name) + ".pal", result.PaletteBytes));
            }

            DatSave.Write(_cache, catalog, files);
            StatusText.Text = $"Wrote {name} into {catalog.DisplayName}.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Save to DAT");
        }
    }

    private void SaveFilesClicked(object sender, RoutedEventArgs e)
    {
        if (!TryConvert(out GraphicConvertResult result, out _))
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = $"Converted|*{result.Extension}|All files|*.*",
            FileName = string.IsNullOrWhiteSpace(NameBox.Text) ? "new001" + result.Extension : NameBox.Text
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        File.WriteAllBytes(dialog.FileName, result.Data);
        if (result.PaletteBytes is not null)
        {
            File.WriteAllBytes(Path.ChangeExtension(dialog.FileName, ".pal"), result.PaletteBytes);
        }

        StatusText.Text = $"Wrote {Path.GetFileName(dialog.FileName)}.";
    }

    private bool TryConvert(out GraphicConvertResult result, out Palette? usedPal)
    {
        result = default;
        usedPal = null;
        if (_source is null)
        {
            System.Windows.MessageBox.Show(this, "Open a PNG first.", "Convert");
            return false;
        }

        if (FormatBox.SelectedItem is not GraphicTarget target)
        {
            return false;
        }

        usedPal = ResolvePalette();
        if (usedPal is null)
        {
            usedPal = ColorQuantizer.Build(_source);
            if (DyeBox.IsChecked == true)
            {
                DyeSlots.ApplyLayout(usedPal);
            }
        }

        short.TryParse(OriginXBox.Text, out short ox);
        short.TryParse(OriginYBox.Text, out short oy);
        int.TryParse(PalNumBox.Text, out int palNum);
        var blend = BlendBox.SelectedItem is EfaBlend b ? b : EfaBlend.Additive;
        bool dither = DitherBox.IsChecked == true;
        result = GraphicConverter.Convert(_source, target, usedPal, dither, palNum, ox, oy, blend);
        PreviewImage.Source = WpfImages.ToBitmap(_source);
        return true;
    }

    private Palette? ResolvePalette()
    {
        if (PaletteBox.SelectedItem is not PalChoice choice || choice.EntryName is null || choice.Catalog is null || _cache is null)
        {
            return null;
        }

        try
        {
            IArchive archive = _cache.Get(choice.Catalog);
            return PaletteLocator.Named(archive, choice.EntryName);
        }
        catch
        {
            return null;
        }
    }

    private sealed class PalChoice
    {
        public PalChoice(string name, string? entryName, CatalogEntry? catalog = null)
        {
            Name = name;
            EntryName = entryName;
            Catalog = catalog;
        }

        public string Name { get; }
        public string? EntryName { get; }
        public CatalogEntry? Catalog { get; }
    }
}
