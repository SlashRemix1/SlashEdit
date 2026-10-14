using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DarkAges.Formats.Catalog;
using DarkAges.Formats.Data;
using DarkAges.Formats.Drawing;
using DarkAges.Formats.Imaging;
using DarkAges.Formats.IO;
using DarkAges.Viewer.Fonts;
using DarkAges.Viewer.Imaging;
using DarkAges.Viewer.Maps;
using DarkAges.Viewer.Services;
using Microsoft.Win32;

namespace DarkAges.Viewer;

public partial class GraphicEditorWindow : Window
{
    public enum Kind
    {
        Effects,
        Ui,
        Floors,
        Walls,
        Portraits
    }

    private readonly ArchiveCache? _cache;
    private readonly List<CatalogEntry> _catalogs;
    private readonly ObservableCollection<FileRow> _files = [];
    private readonly List<FileRow> _all = [];
    private Kind _kind;
    private FileRow? _current;
    private bool _syncing;
    private readonly ClientProfile? _client;
    private int _galleryPage;
    private IReadOnlyList<FloorFamily> _floorFamilies = FloorFamilies.Seeds;
    private IReadOnlyList<WallFamily> _wallFamilies = [];

    public GraphicEditorWindow(ArchiveCache? cache, IReadOnlyList<CatalogEntry> catalogs, Kind kind, ClientProfile? client = null)
    {
        InitializeComponent();
        _cache = cache;
        _catalogs = catalogs.ToList();
        _client = client;
        _kind = kind;
        KindBox.ItemsSource = Enum.GetValues<Kind>();
        KindBox.SelectedItem = kind;
        BlendBox.ItemsSource = Enum.GetValues<EfaBlend>();
        BlendBox.SelectedItem = EfaBlend.Additive;
        FileList.ItemsSource = _files;
        Paint.PixelsChanged += PaintPixelsChanged;
        Title = $"Graphic Editor — {kind}";
        LoadFiles();
    }

    public void ShowKind(Kind kind)
    {
        KindBox.SelectedItem = kind;
        Activate();
    }

    private void KindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (KindBox.SelectedItem is not Kind kind || kind == _kind)
        {
            return;
        }

        _kind = kind;
        Title = $"Graphic Editor — {kind}";
        UpdateEffectBar();
        LoadFiles();
    }

    private void LoadFiles()
    {
        Busy.Run(this, "Loading graphics…", LoadFilesCore);
    }

    private void LoadFilesCore()
    {
        _all.Clear();
        if (_cache is null)
        {
            StatusText.Text = "Load a client first.";
            return;
        }

        foreach (CatalogEntry catalog in _catalogs.Where(MatchesCatalog))
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

            foreach (ArchiveEntry entry in archive.Entries.Where(MatchesEntry))
            {
                _all.Add(new FileRow
                {
                    Name = entry.Name,
                    Catalog = catalog,
                    Entry = entry
                });
            }
        }

        ApplyFilter();
        BindCategories();
        LoadGallery();
        UpdateEffectBar();
        StatusText.Text = _all.Count == 0
            ? _kind == Kind.Floors
                ? "No TILEA.BMP / TILEAS.BMP or AoC tile_*.rgba in this client."
                : $"No {_kind.ToString().ToLowerInvariant()} files (classic EPF/HPF/SPF/EFA or AoC .rgba) in this client."
            : $"{_all.Count} {_kind.ToString().ToLowerInvariant()} files.";
    }

    private bool MatchesCatalog(CatalogEntry catalog) => _kind switch
    {
        Kind.Effects => catalog.Role is ArchiveRole.Effects or ArchiveRole.Misc or ArchiveRole.Unknown,
        Kind.Ui => catalog.Role is ArchiveRole.Ui or ArchiveRole.Misc or ArchiveRole.Unknown,
        Kind.Floors => catalog.Role is ArchiveRole.Floors or ArchiveRole.Misc or ArchiveRole.Unknown,
        Kind.Walls => catalog.Role is ArchiveRole.Walls or ArchiveRole.Misc or ArchiveRole.Unknown,
        Kind.Portraits => catalog.Role is ArchiveRole.Ui or ArchiveRole.PlayerSprites or ArchiveRole.Misc or ArchiveRole.Unknown,
        _ => true
    };

    private bool MatchesEntry(ArchiveEntry entry)
    {
        string ext = entry.Extension;
        string name = entry.Name;
        return _kind switch
        {
            Kind.Effects => ext.Equals(".efa", StringComparison.OrdinalIgnoreCase)
                || (ext.Equals(".epf", StringComparison.OrdinalIgnoreCase)
                    && name.StartsWith("efct", StringComparison.OrdinalIgnoreCase))
                || (ext.Equals(".rgba", StringComparison.OrdinalIgnoreCase)
                    && name.StartsWith("efct", StringComparison.OrdinalIgnoreCase)),
            Kind.Ui => (
                    ext.Equals(".epf", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".pcx", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".spf", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".hea", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".jpf", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".fnt", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".lft", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".upf", StringComparison.OrdinalIgnoreCase)
                    || (ext.Equals(".rgba", StringComparison.OrdinalIgnoreCase)
                        && (name.StartsWith("skill", StringComparison.OrdinalIgnoreCase)
                            || name.StartsWith("spell", StringComparison.OrdinalIgnoreCase)
                            || name.StartsWith("hud", StringComparison.OrdinalIgnoreCase)
                            || name.StartsWith("ui_", StringComparison.OrdinalIgnoreCase)))
                ) && !name.StartsWith("item", StringComparison.OrdinalIgnoreCase)
                  && !name.StartsWith("efct", StringComparison.OrdinalIgnoreCase),
            Kind.Floors => FloorTiles.IsFloorName(entry),
            Kind.Walls => (ext.Equals(".hpf", StringComparison.OrdinalIgnoreCase)
                    && (name.StartsWith("stc", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("sts", StringComparison.OrdinalIgnoreCase)))
                || (ext.Equals(".rgba", StringComparison.OrdinalIgnoreCase)
                    && name.StartsWith("stc", StringComparison.OrdinalIgnoreCase)),
            Kind.Portraits => ext.Equals(".spf", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("portrait", StringComparison.OrdinalIgnoreCase)
                || (ext.Equals(".rgba", StringComparison.OrdinalIgnoreCase)
                    && name.StartsWith("aisling", StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }

    private void FilterChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
        JumpFloorTile();
        LoadGallery();
    }

    private void ApplyFilter()
    {
        string q = FilterBox.Text.Trim();
        if (_kind == Kind.Floors && int.TryParse(q, out _))
        {
            q = "";
        }

        FileList.ItemsSource = null;
        _files.Clear();
        foreach (FileRow row in _all.Where(r => r.Matches(q)))
        {
            _files.Add(row);
        }

        FileList.ItemsSource = _files;
    }

    private void JumpFloorTile()
    {
        if (_kind != Kind.Floors || _current?.FloorAtlas is null)
        {
            return;
        }

        if (!int.TryParse(FilterBox.Text.Trim(), out int tile) || tile < 1)
        {
            return;
        }

        int index = Math.Clamp(tile - 1, 0, Math.Max(0, _current.FrameCount - 1));
        FrameSlider.Value = index;
    }

    private void FileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItem is not FileRow row || _cache is null)
        {
            return;
        }

        _current = row;
        try
        {
            EnsureLoaded(row);
            _syncing = true;
            int frames = Math.Max(1, row.FrameCount);
            FrameSlider.Maximum = frames - 1;
            FrameSlider.Value = 0;
            _syncing = false;
            ShowFrame(0);
            StatusText.Text = FloorStatus(row, 0);
            LoadGallery();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Graphic");
        }
    }

    private void FrameChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || _current is null)
        {
            return;
        }

        int index = (int)FrameSlider.Value;
        FrameLabel.Text = $"Frame {index}";
        ShowFrame(index);
    }

    private void ShowFrame(int index)
    {
        if (_current is null)
        {
            return;
        }

        EnsureLoaded(_current);
        PixelBuffer? pixels = FramePixels(_current, index);
        Palette? pal = _current.RgbaFrames is not null
            ? null
            : _current.FloorAtlas is not null
                ? FloorPalette(_current, index)
                : _current.Palette;
        Paint.Load(pixels, pal);
        FrameLabel.Text = _current.FloorAtlas is not null ? $"Tile {index + 1}" : $"Frame {index}";
        SyncEffectFields(index);
        if (_current.FloorAtlas is not null)
        {
            StatusText.Text = FloorStatus(_current, index);
        }
        else if (_current.EfaFrames is not null)
        {
            (short ox, short oy) = CurrentOrigin(index);
            StatusText.Text = $"{_current.Name}  frame {index + 1}/{_current.FrameCount}  blend {_current.Blend}  origin ({ox},{oy})";
        }
    }

    private void PaintPixelsChanged(object? sender, EventArgs e)
    {
        if (_syncing || _current is null || Paint.Pixels is null)
        {
            return;
        }

        ReplaceFrame(_current, (int)FrameSlider.Value, Paint.Pixels.Clone());
        _current.Dirty = true;
        StatusText.Text = $"Painted {_current.Name}. Save to DAT when ready.";
    }

    private void ReplaceFontClicked(object sender, RoutedEventArgs e)
    {
        if (_current is null || _cache is null)
        {
            return;
        }

        EnsureLoaded(_current);
        if (_current.Fnt is null && _current.Lft is null)
        {
            System.Windows.MessageBox.Show(
                this,
                "Select a .fnt or .lft first (UI kind, files like da.lft or eng00.fnt).",
                "Replace font");
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Fonts|*.ttf;*.otf;*.ttc;*.fnt;*.lft|TrueType / OpenType|*.ttf;*.otf;*.ttc|Dark Ages fonts|*.fnt;*.lft|All files|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            string path = dialog.FileName;
            string ext = Path.GetExtension(path);
            if (DaFontRasterizer.IsTrueType(path))
            {
                DaFontImportResult imported = default!;
                Busy.Run(this, "Rasterizing font…", () =>
                {
                    IProgress<(int Done, int Total, string Name)> progress = Busy.Tracker(this, "Rasterizing");
                    imported = _current.Fnt is not null
                        ? DaFontRasterizer.Replace(path, _current.Fnt, progress)
                        : DaFontRasterizer.Replace(path, _current.Lft!, progress);
                });
                ApplyImportedFont(_current, imported);
                return;
            }

            if (ext.Equals(".fnt", StringComparison.OrdinalIgnoreCase) && _current.Fnt is not null)
            {
                var loaded = FntFile.Load(File.ReadAllBytes(path));
                if (loaded.Kind != _current.Fnt.Kind)
                {
                    System.Windows.MessageBox.Show(
                        this,
                        $"That FNT is {loaded.Kind} ({loaded.GlyphWidth}×{loaded.GlyphHeight}); this entry is {_current.Fnt.Kind}.",
                        "Replace font");
                    return;
                }

                ApplyImportedFont(_current, new DaFontImportResult(loaded, null, Path.GetFileName(path)));
                return;
            }

            if (ext.Equals(".lft", StringComparison.OrdinalIgnoreCase) && _current.Lft is not null)
            {
                var loaded = LftFile.Load(File.ReadAllBytes(path));
                ApplyImportedFont(_current, new DaFontImportResult(null, loaded, Path.GetFileName(path)));
                return;
            }

            System.Windows.MessageBox.Show(this, "Pick a TTF/OTF, or a .fnt/.lft that matches the selected entry.", "Replace font");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Replace font");
        }
    }

    private void ApplyImportedFont(FileRow row, DaFontImportResult imported)
    {
        foreach (FileRow target in FontSaveTargets(row))
        {
            EnsureLoaded(target);
            if (imported.Fnt is not null)
            {
                target.Fnt = imported.Fnt;
                target.Width = imported.Fnt.GlyphWidth;
                target.Height = imported.Fnt.GlyphHeight;
                target.FrameCount = imported.Fnt.Count;
            }

            if (imported.Lft is not null)
            {
                target.Lft = imported.Lft;
                target.Width = imported.Lft.NominalWidth;
                target.Height = imported.Lft.NominalHeight;
                target.FrameCount = 256;
            }

            target.Dirty = true;
            target.Loaded = true;
        }

        FileList.Items.Refresh();
        ShowFrame((int)FrameSlider.Value);
        StatusText.Text = $"{imported.Summary}. Save to DAT when ready.";
    }

    private IEnumerable<FileRow> FontSaveTargets(FileRow row)
    {
        yield return row;
        if (row.Lft is null)
        {
            yield break;
        }

        string twin = row.Name.Equals("da.lft", StringComparison.OrdinalIgnoreCase)
            ? "lod.lft"
            : row.Name.Equals("lod.lft", StringComparison.OrdinalIgnoreCase)
                ? "da.lft"
                : "";
        if (twin.Length == 0)
        {
            yield break;
        }

        FileRow? other = _all.FirstOrDefault(f =>
            f.Name.Equals(twin, StringComparison.OrdinalIgnoreCase)
            && f.Catalog.SourcePath.Equals(row.Catalog.SourcePath, StringComparison.OrdinalIgnoreCase));
        if (other is not null)
        {
            yield return other;
        }
    }

    private void ImportClicked(object sender, RoutedEventArgs e)
    {
        if (_current is null || _cache is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Images|*.png;*.bmp;*.jpg;*.jpeg|All files|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        PixelBuffer? pixels = WpfImages.TryDecodeImage(File.ReadAllBytes(dialog.FileName));
        if (pixels is null)
        {
            System.Windows.MessageBox.Show(this, "Could not read that image.", "Import");
            return;
        }

        try
        {
            EnsureLoaded(_current);
            int index = (int)FrameSlider.Value;
            ReplaceFrame(_current, index, pixels);
            _current.Dirty = true;
            ShowFrame(index);
            StatusText.Text = $"Imported {Path.GetFileName(dialog.FileName)} into frame {index}.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Import");
        }
    }

    private void ImportNewClicked(object sender, RoutedEventArgs e)
    {
        CatalogEntry? catalog = _current?.Catalog ?? _catalogs.FirstOrDefault(MatchesCatalog);
        if (catalog is null || _cache is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Images|*.png;*.bmp;*.jpg;*.jpeg|All files|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        PixelBuffer? pixels = WpfImages.TryDecodeImage(File.ReadAllBytes(dialog.FileName));
        if (pixels is null)
        {
            System.Windows.MessageBox.Show(this, "Could not read that image.", "Import");
            return;
        }

        try
        {
            IArchive archive = _cache.Get(catalog);
            if (_kind == Kind.Floors && !LrarArchive.IsLrarPath(catalog.SourcePath))
            {
                ImportFloorPixels(catalog, archive, pixels);
                return;
            }

            GraphicTarget target = _kind switch
            {
                Kind.Walls => GraphicTarget.Hpf,
                Kind.Effects => GraphicTarget.Efa,
                Kind.Portraits => GraphicTarget.SpfPalettized,
                _ => GraphicTarget.Epf
            };
            Palette? pal = PaletteLocator.Find(archive);
            GraphicConvertResult converted = GraphicConverter.Convert(pixels, target, pal, dither: false);
            string name = NextName(catalog, converted.Extension);
            var files = new List<(string Name, byte[] Data)> { (name, converted.Data) };
            if (converted.PaletteBytes is not null && archive.GetEntry(Path.ChangeExtension(name, ".pal")) is null)
            {
                files.Add((Path.ChangeExtension(name, ".pal"), converted.PaletteBytes));
            }

            if (target == GraphicTarget.Efa)
            {
                files.Add((Path.ChangeExtension(name, ".tbl"), EffectCenterTable.ForFrames(1).ToBytes()));
            }

            DatSave.Write(_cache, catalog, files);
            LoadFiles();
            FileRow? row = _files.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (row is not null)
            {
                FileList.SelectedItem = row;
            }

            StatusText.Text = $"Inserted {name} into {catalog.DisplayName}.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Import as new");
        }
    }

    private void RemapClicked(object sender, RoutedEventArgs e)
    {
        if (_current is null || _cache is null)
        {
            return;
        }

        EnsureLoaded(_current);
        if (_current.Palette is null)
        {
            System.Windows.MessageBox.Show(this, "This graphic is not palettized.", "Remap");
            return;
        }

        IArchive archive = _cache.Get(_current.Catalog);
        Palette? dest = PickPalette(archive);
        if (dest is null)
        {
            return;
        }

        try
        {
            RemapRow(_current, dest);
            _current.Dirty = true;
            ShowFrame((int)FrameSlider.Value);
            StatusText.Text = $"Remapped {_current.Name} onto the selected palette. Save to DAT when ready.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Remap");
        }
    }

    private void EffectSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || _current?.EfaFrames is null)
        {
            return;
        }

        if (BlendBox.SelectedItem is EfaBlend blend)
        {
            _current.Blend = blend;
        }

        if (short.TryParse(OriginXBox.Text, out short ox) && short.TryParse(OriginYBox.Text, out short oy))
        {
            int index = (int)FrameSlider.Value;
            SetOrigin(_current, index, ox, oy);
        }

        _current.Dirty = true;
    }

    private void NewClicked(object sender, RoutedEventArgs e)
    {
        CatalogEntry? catalog = _current?.Catalog ?? _catalogs.FirstOrDefault(MatchesCatalog);
        if (catalog is null || _cache is null)
        {
            return;
        }

        try
        {
            string name = NextName(catalog);
            if (_kind == Kind.Floors && !LrarArchive.IsLrarPath(catalog.SourcePath))
            {
                AppendClassicFloor(catalog);
                return;
            }

            byte[] data = _kind switch
            {
                Kind.Floors => RgbaImage.Write(new PixelBuffer(FloorTiles.TileWidth, FloorTiles.TileHeight)),
                Kind.Walls => HpfWriter.Write(new byte[8], new byte[HpfWriter.TileWidth * 14]),
                Kind.Portraits => BlankSpf(),
                Kind.Effects => EfaWriter.FromPixels(
                    new PixelBuffer(32, 32),
                    EffectCenterTable.DefaultX,
                    EffectCenterTable.DefaultY),
                _ => EpfWriter.Write(32, 32, [new EpfFrame(0, 0, 32, 32, new byte[32 * 32])])
            };
            var files = new List<(string Name, byte[] Data)> { (name, data) };
            if (_kind == Kind.Effects)
            {
                files.Add((Path.ChangeExtension(name, ".tbl"), EffectCenterTable.ForFrames(1).ToBytes()));
            }

            DatSave.Write(_cache, catalog, files);
            LoadFiles();
            FileRow? row = _files.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (row is not null)
            {
                FileList.SelectedItem = row;
            }

            StatusText.Text = $"Inserted {name} into {catalog.DisplayName}.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "New file");
        }
    }

    private void OpenButtonMenu(object sender, RoutedEventArgs e) => ButtonMenu.Open(sender);

    private void ExportDataClicked(object sender, RoutedEventArgs e)
    {
        TableDump dump = TableDump.Create(
            "Graphics",
            ["Name", "Kind", "Archive", "Width", "Height", "Frames"],
            _files.Select(f => (IReadOnlyList<object?>)new object?[]
            {
                f.Name, _kind.ToString(), f.Catalog.DisplayName, f.Width, f.Height, f.FrameCount
            }));
        TableExport.Save(this, _kind.ToString().ToLowerInvariant(), dump, sender, StatusText);
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (_current is null || _cache is null)
        {
            return;
        }

        try
        {
            EnsureLoaded(_current);
            var files = new List<(string Name, byte[] Data)>();
            var saved = new List<FileRow>();
            foreach (FileRow row in FontSaveTargets(_current).Where(r => r.Dirty || r == _current))
            {
                EnsureLoaded(row);
                files.Add((row.Name, BuildBytes(row)));
                saved.Add(row);
            }

            if (_current.EfaFrames is not null && _current.Centers is not null)
            {
                files.Add((Path.ChangeExtension(_current.Name, ".tbl"), _current.Centers.ToBytes()));
            }

            DatSave.Write(_cache, _current.Catalog, files);
            foreach (FileRow row in saved)
            {
                row.Dirty = false;
            }

            FileList.Items.Refresh();
            ClientChangeLog.Append(_client, "graphic", $"Saved {string.Join(", ", saved.Select(s => s.Name))} into {_current.Catalog.DisplayName}.");
            StatusText.Text = $"Saved {string.Join(", ", saved.Select(s => s.Name))} into {_current.Catalog.DisplayName}.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Save to DAT");
        }
    }

    private void EnsureLoaded(FileRow row)
    {
        if (row.Loaded || _cache is null)
        {
            return;
        }

        IArchive archive = _cache.Get(row.Catalog);
        ArchiveEntry? entry = archive.GetEntry(row.Name) ?? row.Entry;
        if (entry is null)
        {
            return;
        }

        Palette pal = PaletteLocator.ForEntry(archive, entry) ?? PaletteLocator.Find(archive) ?? new Palette();
        row.Palette = pal;
        string ext = Path.GetExtension(row.Name);
        if (FloorTiles.IsAtlas(entry))
        {
            row.FloorAtlas = FloorTiles.LoadAtlas(entry);
            row.Width = FloorTiles.TileWidth;
            row.Height = FloorTiles.TileHeight;
            row.FrameCount = Math.Max(1, FloorTiles.Count(row.FloorAtlas.Length));
            row.Loaded = true;
            return;
        }

        if (ext.Equals(".rgba", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<RgbaImage> images = RgbaImage.DecodeAll(entry.ReadAllBytes());
            row.RgbaFrames = images.Select(i => i.Pixels).ToList();
            row.Width = row.RgbaFrames.FirstOrDefault()?.Width ?? 1;
            row.Height = row.RgbaFrames.FirstOrDefault()?.Height ?? 1;
            row.FrameCount = Math.Max(1, row.RgbaFrames.Count);
            row.Loaded = true;
            return;
        }

        if (ext.Equals(".pcx", StringComparison.OrdinalIgnoreCase))
        {
            var pcx = new PcxFile(entry);
            row.RgbaFrames = [pcx.Image];
            row.SaveAsPcx = true;
            row.Width = pcx.Width;
            row.Height = pcx.Height;
            row.FrameCount = 1;
            row.Loaded = true;
            return;
        }

        if (ext.Equals(".hea", StringComparison.OrdinalIgnoreCase))
        {
            var hea = new HeaFile(entry);
            row.Hea = hea;
            PixelBuffer? rendered = hea.TryRender(pal);
            row.RgbaFrames = [rendered ?? new PixelBuffer(Math.Max(1, hea.Width), Math.Max(1, hea.Height))];
            row.Width = row.RgbaFrames[0].Width;
            row.Height = row.RgbaFrames[0].Height;
            row.FrameCount = 1;
            row.Loaded = true;
            return;
        }

        if (ext.Equals(".fnt", StringComparison.OrdinalIgnoreCase))
        {
            row.Fnt = FntFile.Load(entry);
            row.Width = row.Fnt.GlyphWidth;
            row.Height = row.Fnt.GlyphHeight;
            row.FrameCount = row.Fnt.Count;
            row.Loaded = true;
            return;
        }

        if (ext.Equals(".lft", StringComparison.OrdinalIgnoreCase))
        {
            row.Lft = LftFile.Load(entry);
            row.Width = row.Lft.NominalWidth;
            row.Height = row.Lft.NominalHeight;
            row.FrameCount = 256;
            row.Loaded = true;
            return;
        }

        if (ext.Equals(".upf", StringComparison.OrdinalIgnoreCase))
        {
            row.Upf = UpfFile.Load(entry);
            row.Width = row.Upf.Width;
            row.Height = row.Upf.Height;
            row.FrameCount = Math.Max(1, row.Upf.Count);
            row.Loaded = true;
            return;
        }

        if (ext.Equals(".hpf", StringComparison.OrdinalIgnoreCase))
        {
            var hpf = new HpfFile(entry);
            row.Hpf = hpf;
            row.Indexed = (byte[])hpf.Data.Clone();
            row.Width = hpf.Width;
            row.Height = hpf.Height;
            row.FrameCount = 1;
        }
        else if (ext.Equals(".spf", StringComparison.OrdinalIgnoreCase))
        {
            var spf = new SpfFile(entry);
            row.Spf = spf;
            row.Width = spf.Frames.FirstOrDefault()?.Width ?? 1;
            row.Height = spf.Frames.FirstOrDefault()?.Height ?? 1;
            row.FrameCount = Math.Max(1, spf.Frames.Count);
        }
        else if (ext.Equals(".efa", StringComparison.OrdinalIgnoreCase))
        {
            var efa = new EfaFile(entry);
            row.Efa = efa;
            row.Blend = efa.Blend;
            row.EfaFrames = efa.Frames.ToList();
            row.Width = efa.Frames.FirstOrDefault()?.PixelWidth ?? 1;
            row.Height = efa.Frames.FirstOrDefault()?.PixelHeight ?? 1;
            row.FrameCount = Math.Max(1, efa.Frames.Count);
            ArchiveEntry? tbl = archive.GetEntry(Path.ChangeExtension(row.Name, ".tbl"));
            row.Centers = tbl is not null
                ? EffectCenterTable.Load(tbl, row.FrameCount)
                : EffectCenterTable.ForFrames(row.FrameCount);
            for (int i = 0; i < row.EfaFrames.Count; i++)
            {
                EfaFrame frame = row.EfaFrames[i];
                if (frame.Header.OriginX == 0 && frame.Header.OriginY == 0)
                {
                    (short ox, short oy) = row.Centers[i];
                    SetOrigin(row, i, ox, oy, markDirty: false);
                }
            }
        }
        else
        {
            var epf = new EpfFile(entry);
            row.Epf = epf;
            row.EpfFrames = epf.Frames.ToList();
            row.Width = epf.Width;
            row.Height = epf.Height;
            row.FrameCount = Math.Max(1, epf.Frames.Count);
        }

        row.Loaded = true;
    }

    private PixelBuffer? FramePixels(FileRow row, int index)
    {
        if (row.FloorAtlas is not null)
        {
            byte[] tile = FloorTiles.ReadTile(row.FloorAtlas, index);
            return FloorPalette(row, index).Render(tile, FloorTiles.TileWidth, FloorTiles.TileHeight);
        }

        if (row.RgbaFrames is { Count: > 0 })
        {
            return row.RgbaFrames[Math.Clamp(index, 0, row.RgbaFrames.Count - 1)];
        }

        if (row.Fnt is not null)
        {
            return row.Fnt.Render(Math.Clamp(index, 0, row.Fnt.Count - 1));
        }

        if (row.Lft is not null)
        {
            return row.Lft.Render(Math.Clamp(index, 0, 255));
        }

        if (row.Upf is not null)
        {
            return row.Upf.Render(index, row.Palette);
        }

        if (row.Hpf is not null && row.Indexed is not null && row.Palette is not null)
        {
            return row.Palette.Render(row.Indexed, row.Width, row.Height);
        }

        if (row.Spf is not null)
        {
            if (row.SpfFrames is { Count: > 0 })
            {
                SpfFrame frame = row.SpfFrames[Math.Clamp(index, 0, row.SpfFrames.Count - 1)];
                return frame.Render(row.Spf, row.Palette);
            }

            return row.Spf.Render(index, row.Palette);
        }

        if (row.EfaFrames is { Count: > 0 })
        {
            return row.EfaFrames[Math.Clamp(index, 0, row.EfaFrames.Count - 1)].Render(row.Blend);
        }

        if (row.EpfFrames is { Count: > 0 } && row.Palette is not null)
        {
            PaletteBlit blit = PaletteBlit.Opaque;
            if (_cache is not null && row.Entry is not null)
            {
                try
                {
                    blit = PaletteLocator.BlitFor(_cache.Get(row.Catalog), row.Entry);
                }
                catch
                {
                    // keep opaque
                }
            }

            return row.EpfFrames[Math.Clamp(index, 0, row.EpfFrames.Count - 1)].Render(row.Palette, blit);
        }

        return null;
    }

    private void ReplaceFrame(FileRow row, int index, PixelBuffer pixels)
    {
        if (row.FloorAtlas is not null)
        {
            var fitted = new PixelBuffer(FloorTiles.TileWidth, FloorTiles.TileHeight);
            fitted.Blit(pixels, 0, 0);
            byte[] indexed = FloorPalette(row, index).Quantize(fitted);
            row.FloorAtlas = FloorTiles.WriteTile(row.FloorAtlas, index, indexed);
            row.Width = FloorTiles.TileWidth;
            row.Height = FloorTiles.TileHeight;
            row.FrameCount = FloorTiles.Count(row.FloorAtlas.Length);
            return;
        }

        if (row.RgbaFrames is not null)
        {
            while (row.RgbaFrames.Count <= index)
            {
                row.RgbaFrames.Add(new PixelBuffer(1, 1));
            }

            row.RgbaFrames[index] = pixels;
            row.Width = Math.Max(row.Width, pixels.Width);
            row.Height = Math.Max(row.Height, pixels.Height);
            row.FrameCount = row.RgbaFrames.Count;
            return;
        }

        if (row.Fnt is not null)
        {
            row.Fnt = row.Fnt.WithGlyph(index, pixels);
            row.Width = row.Fnt.GlyphWidth;
            row.Height = row.Fnt.GlyphHeight;
            row.FrameCount = row.Fnt.Count;
            return;
        }

        if (row.Lft is not null)
        {
            row.Lft = row.Lft.WithGlyph(Math.Clamp(index, 0, 255), pixels);
            return;
        }

        if (row.Upf is not null && row.Palette is not null)
        {
            row.Upf = row.Upf.WithFrame(index, pixels, row.Palette);
            row.FrameCount = row.Upf.Count;
            return;
        }

        if (row.Hpf is not null && row.Palette is not null)
        {
            byte[] indexed = row.Palette.Quantize(pixels);
            int height = Math.Max(1, indexed.Length / HpfWriter.TileWidth);
            var data = new byte[HpfWriter.TileWidth * height];
            Buffer.BlockCopy(indexed, 0, data, 0, Math.Min(indexed.Length, data.Length));
            row.Indexed = data;
            row.Width = HpfWriter.TileWidth;
            row.Height = height;
            return;
        }

        if (row.Spf is not null)
        {
            var frames = row.Spf.Frames.ToList();
            while (frames.Count <= index)
            {
                frames.Add(new SpfFrame(0, 0, pixels.Width, pixels.Height, []));
            }

            if (row.Spf.Format == SpfFormat.Colorized)
            {
                frames[index] = new SpfFrame(0, 0, pixels.Width, pixels.Height, [], pixels);
            }
            else
            {
                var pal = new Palette();
                for (int i = 0; i < Math.Min(256, row.Spf.EmbeddedPalette.Count); i++)
                {
                    var (r, g, b) = row.Spf.EmbeddedPalette[i];
                    pal.Set(i, r, g, b);
                }

                frames[index] = new SpfFrame(0, 0, pixels.Width, pixels.Height, pal.Quantize(pixels));
            }

            row.SpfFrames = frames;
            row.Width = pixels.Width;
            row.Height = pixels.Height;
            row.FrameCount = frames.Count;
            return;
        }

        if (row.EfaFrames is not null)
        {
            while (row.EfaFrames.Count <= index)
            {
                row.EfaFrames.Add(new EfaFrame(new EfaFrameHeader { Raw = new byte[64] }, []));
            }

            byte[] data = new byte[pixels.Width * pixels.Height * 2];
            for (int i = 0; i < pixels.Width * pixels.Height; i++)
            {
                int p = i * 4;
                ushort packed = pixels.Bgra[p + 3] < 16
                    ? (ushort)0
                    : SpfWriter.ToRgb565(pixels.Bgra[p + 2], pixels.Bgra[p + 1], pixels.Bgra[p]);
                data[i * 2] = (byte)packed;
                data[i * 2 + 1] = (byte)(packed >> 8);
            }

            (short ox, short oy) = CurrentOrigin(index);
            row.EfaFrames[index] = new EfaFrame(new EfaFrameHeader
            {
                Width = pixels.Width * 2,
                ByteCount = data.Length,
                RawSize = data.Length,
                OriginX = ox,
                OriginY = oy,
                Raw = new byte[64]
            }, data);
            row.Width = pixels.Width;
            row.Height = pixels.Height;
            return;
        }

        if (row.EpfFrames is not null && row.Palette is not null)
        {
            while (row.EpfFrames.Count <= index)
            {
                row.EpfFrames.Add(new EpfFrame(0, 0, 0, 0, []));
            }

            byte[] indexed = row.Palette.Quantize(pixels);
            row.EpfFrames[index] = new EpfFrame(0, 0, pixels.Height, pixels.Width, indexed);
            row.Width = Math.Max(row.Width, pixels.Width);
            row.Height = Math.Max(row.Height, pixels.Height);
        }
    }

    private static byte[] BuildBytes(FileRow row)
    {
        if (row.FloorAtlas is not null)
        {
            return row.FloorAtlas;
        }

        if (row.Hea is not null && row.RgbaFrames is { Count: > 0 })
        {
            return HeaWriter.Write(row.Hea, row.RgbaFrames[0]);
        }

        if (row.SaveAsPcx && row.RgbaFrames is { Count: > 0 })
        {
            return PcxWriter.Write(row.RgbaFrames[0], row.Palette);
        }

        if (row.Fnt is not null)
        {
            return row.Fnt.ToBytes();
        }

        if (row.Lft is not null)
        {
            return row.Lft.ToBytes();
        }

        if (row.Upf is not null)
        {
            return row.Upf.ToBytes();
        }

        if (row.RgbaFrames is not null)
        {
            if (row.RgbaFrames.Count == 1)
            {
                return RgbaImage.Write(row.RgbaFrames[0]);
            }

            return RgbaImage.Pack(row.RgbaFrames.Select(RgbaImage.Write).ToList());
        }

        if (row.Hpf is not null)
        {
            return HpfWriter.Write(row.Hpf.Header, row.Indexed ?? row.Hpf.Data);
        }

        if (row.Spf is not null)
        {
            return SpfWriter.Write(row.Spf, row.SpfFrames);
        }

        if (row.EfaFrames is not null)
        {
            return EfaWriter.Write(row.EfaFrames, row.Blend);
        }

        return EpfWriter.Write(Math.Max(1, row.Width), Math.Max(1, row.Height), row.EpfFrames ?? []);
    }

    private string NextName(CatalogEntry catalog, string? extension = null)
    {
        IArchive archive = _cache!.Get(catalog);
        string prefix = _kind switch
        {
            Kind.Floors => "tile_",
            Kind.Walls => "stc",
            Kind.Effects => "efct",
            Kind.Ui => "gui",
            _ => "npc"
        };
        string ext = extension ?? _kind switch
        {
            Kind.Floors => ".rgba",
            Kind.Walls => ".hpf",
            Kind.Portraits => ".spf",
            Kind.Effects => ".efa",
            _ => ".epf"
        };
        int n = 1;
        while (archive.GetEntry($"{prefix}{n:D5}{ext}") is not null || archive.GetEntry($"{prefix}{n:D3}{ext}") is not null)
        {
            n++;
        }

        return _kind is Kind.Walls or Kind.Floors ? $"{prefix}{n:D5}{ext}" : $"{prefix}{n:D3}{ext}";
    }

    private void AppendClassicFloor(CatalogEntry catalog)
    {
        string name = _current?.FloorAtlas is not null ? _current.Name : "TILEA.BMP";
        IArchive archive = _cache!.Get(catalog);
        ArchiveEntry? entry = archive.GetEntry(name) ?? archive.GetEntry("TILEA.BMP");
        byte[] atlas = entry is not null ? FloorTiles.LoadAtlas(entry) : [];
        int index = FloorTiles.Count(atlas.Length);
        atlas = FloorTiles.WriteTile(atlas, index, FloorTiles.BlankTile());
        DatSave.Write(_cache, catalog, entry?.Name ?? "TILEA.BMP", atlas);
        LoadFiles();
        FileRow? row = _files.FirstOrDefault(f => f.Name.Equals(entry?.Name ?? "TILEA.BMP", StringComparison.OrdinalIgnoreCase));
        if (row is not null)
        {
            FileList.SelectedItem = row;
            EnsureLoaded(row);
            FrameSlider.Maximum = Math.Max(0, row.FrameCount - 1);
            FrameSlider.Value = index;
        }

        StatusText.Text = $"Appended floor tile {index + 1} to {entry?.Name ?? "TILEA.BMP"}.";
    }

    private Palette FloorPalette(FileRow row, int index)
    {
        if (_cache is not null)
        {
            try
            {
                IArchive archive = _cache.Get(row.Catalog);
                string stem = Path.GetFileNameWithoutExtension(row.Name);
                Palette? pal = stem.Equals("TILEAS", StringComparison.OrdinalIgnoreCase)
                    ? PaletteLocator.ForSnowFloor(archive)
                    : PaletteLocator.ForFloorTile(archive, index + 1);
                if (pal is not null)
                {
                    return pal;
                }
            }
            catch
            {
                // fall through
            }
        }

        return row.Palette ?? new Palette();
    }

    private static string FloorStatus(FileRow row, int index)
    {
        if (row.FloorAtlas is not null)
        {
            return $"{row.Name}  tile {index + 1}/{row.FrameCount}  {FloorTiles.TileWidth}×{FloorTiles.TileHeight}";
        }

        return $"{row.Name}  {row.Width}×{row.Height}  {row.FrameCount} frame(s).";
    }

    private static byte[] BlankSpf()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(new byte[1024]);
        writer.Write(0u);
        writer.Write(0u);
        return stream.ToArray();
    }

    private void CategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        _galleryPage = 0;
        LoadGallery();
    }

    private void GalleryPrevClicked(object sender, RoutedEventArgs e)
    {
        _galleryPage = Math.Max(0, _galleryPage - 1);
        LoadGallery();
    }

    private void GalleryNextClicked(object sender, RoutedEventArgs e)
    {
        _galleryPage++;
        LoadGallery();
    }

    private void GalleryTileClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not GalleryTile tile)
        {
            return;
        }

        if (tile.Row is not null && !ReferenceEquals(FileList.SelectedItem, tile.Row))
        {
            FileList.SelectedItem = tile.Row;
            FileList.ScrollIntoView(tile.Row);
        }

        if (_current is not null && tile.Frame >= 0)
        {
            FrameSlider.Value = tile.Frame;
        }
    }

    private void BindCategories()
    {
        bool tiles = _kind is Kind.Floors or Kind.Walls;
        if (CategoryBox is not null)
        {
            CategoryBox.Visibility = tiles ? Visibility.Visible : Visibility.Collapsed;
        }

        if (CategoryLabel is not null)
        {
            CategoryLabel.Visibility = tiles ? Visibility.Visible : Visibility.Collapsed;
        }

        if (GalleryScroller is not null)
        {
            GalleryScroller.Visibility = tiles ? Visibility.Visible : Visibility.Collapsed;
        }

        if (GalleryPager is not null)
        {
            GalleryPager.Visibility = tiles ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!tiles)
        {
            return;
        }

        var items = new List<string> { "All" };
        if (_kind == Kind.Floors)
        {
            int count = _files.FirstOrDefault(r => r.FloorAtlas is not null)?.FrameCount
                ?? _files.Count;
            _floorFamilies = FloorFamilies.Cluster(Enumerable.Range(1, Math.Max(1, count)));
            items.AddRange(_floorFamilies.Select(f => f.Name));
        }
        else
        {
            _wallFamilies = WallFamilies.Cluster(_all.Select(r => WallFamilies.IdFromName(r.Name)));
            items.AddRange(_wallFamilies.Select(f => f.Name));
        }

        if (CategoryBox is not null)
        {
            CategoryBox.ItemsSource = items;
            CategoryBox.SelectedIndex = 0;
        }
    }

    private void LoadGallery()
    {
        if (TileGallery is null || _kind is not (Kind.Floors or Kind.Walls))
        {
            return;
        }

        Busy.Show(this, "Loading tile gallery…", determinate: true);
        try
        {
            LoadGalleryCore();
        }
        finally
        {
            Busy.Hide(this);
        }
    }

    private void LoadGalleryCore()
    {
        if (TileGallery is null || _kind is not (Kind.Floors or Kind.Walls))
        {
            return;
        }

        const int pageSize = 96;
        string cat = CategoryBox?.SelectedItem as string ?? "All";
        var pending = new List<(FileRow Row, int Frame, string Label)>();
        if (_kind == Kind.Floors)
        {
            FileRow? atlas = _current?.FloorAtlas is not null ? _current : _files.FirstOrDefault();
            if (atlas is not null)
            {
                try
                {
                    EnsureLoaded(atlas);
                }
                catch
                {
                    // skip
                }

                int count = Math.Max(1, atlas.FrameCount);
                _floorFamilies = FloorFamilies.Cluster(Enumerable.Range(1, count));
                for (int i = 0; i < count; i++)
                {
                    int id = i + 1;
                    if (!FloorInCategory(id, cat))
                    {
                        continue;
                    }

                    pending.Add((atlas, i, FloorFamilies.Label(id, _floorFamilies)));
                }
            }
            else
            {
                foreach (FileRow row in _files)
                {
                    int id = FloorIdFromName(row.Name);
                    if (id <= 0)
                    {
                        continue;
                    }

                    if (!FloorInCategory(id, cat))
                    {
                        continue;
                    }

                    pending.Add((row, 0, FloorFamilies.Label(id, _floorFamilies)));
                }
            }
        }
        else
        {
            foreach (FileRow row in _files)
            {
                int id = WallFamilies.IdFromName(row.Name);
                if (id <= 0)
                {
                    continue;
                }

                if (!WallInCategory(id, cat))
                {
                    continue;
                }

                pending.Add((row, 0, WallFamilies.Label(id, _wallFamilies)));
            }
        }

        int pages = Math.Max(1, (int)Math.Ceiling(pending.Count / (double)pageSize));
        _galleryPage = Math.Clamp(_galleryPage, 0, pages - 1);
        var page = pending.Skip(_galleryPage * pageSize).Take(pageSize).ToList();
        var tiles = new List<GalleryTile>();
        int n = 0;
        foreach ((FileRow row, int frame, string label) in page)
        {
            n++;
            Busy.Progress(this, n, page.Count, $"Loading tiles… {n}/{page.Count}");
            try
            {
                EnsureLoaded(row);
            }
            catch
            {
                continue;
            }

            PixelBuffer? pixels = FramePixels(row, frame);
            if (pixels is null)
            {
                continue;
            }

            tiles.Add(new GalleryTile(row, frame, label, WpfImages.ToBitmap(pixels)));
        }

        TileGallery.ItemsSource = tiles;
        if (GalleryPageLabel is not null)
        {
            GalleryPageLabel.Text = $"{pending.Count}  page {_galleryPage + 1} / {pages}";
        }
    }

    private bool FloorInCategory(int id, string cat) =>
        cat == "All" || (FloorFamilies.Find(id, _floorFamilies) is FloorFamily family && family.Name == cat);

    private bool WallInCategory(int id, string cat) =>
        cat == "All" || (WallFamilies.Find(id, _wallFamilies) is WallFamily family && family.Name == cat);

    private void UpdateEffectBar()
    {
        if (EffectBar is not null)
        {
            EffectBar.Visibility = _kind == Kind.Effects ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void SyncEffectFields(int index)
    {
        if (_current?.EfaFrames is null || BlendBox is null)
        {
            return;
        }

        _syncing = true;
        BlendBox.SelectedItem = _current.Blend;
        (short ox, short oy) = CurrentOrigin(index);
        OriginXBox.Text = ox.ToString();
        OriginYBox.Text = oy.ToString();
        _syncing = false;
    }

    private (short X, short Y) CurrentOrigin(int index)
    {
        if (_current?.EfaFrames is { Count: > 0 })
        {
            EfaFrame frame = _current.EfaFrames[Math.Clamp(index, 0, _current.EfaFrames.Count - 1)];
            if (frame.Header.OriginX != 0 || frame.Header.OriginY != 0)
            {
                return (frame.Header.OriginX, frame.Header.OriginY);
            }
        }

        return _current?.Centers is not null
            ? _current.Centers[index]
            : (EffectCenterTable.DefaultX, EffectCenterTable.DefaultY);
    }

    private static void SetOrigin(FileRow row, int index, short x, short y, bool markDirty = true)
    {
        if (row.EfaFrames is null || (uint)index >= (uint)row.EfaFrames.Count)
        {
            return;
        }

        EfaFrame frame = row.EfaFrames[index];
        row.EfaFrames[index] = new EfaFrame(
            new EfaFrameHeader
            {
                Size = frame.Header.Size,
                RawSize = frame.Header.RawSize,
                Width = frame.Header.Width,
                ByteCount = frame.Header.ByteCount,
                OriginX = x,
                OriginY = y,
                Raw = frame.Header.Raw
            },
            frame.Data);
        row.Centers ??= EffectCenterTable.ForFrames(row.EfaFrames.Count);
        row.Centers.Set(index, x, y);
        if (markDirty)
        {
            row.Dirty = true;
        }
    }

    private void ImportFloorPixels(CatalogEntry catalog, IArchive archive, PixelBuffer pixels)
    {
        string name = _current?.FloorAtlas is not null ? _current.Name : "TILEA.BMP";
        ArchiveEntry? entry = archive.GetEntry(name) ?? archive.GetEntry("TILEA.BMP");
        byte[] atlas = entry is not null ? FloorTiles.LoadAtlas(entry) : [];
        int index = FloorTiles.Count(atlas.Length);
        Palette pal = PaletteLocator.ForFloorTile(archive, index + 1) ?? PaletteLocator.Find(archive) ?? new Palette();
        atlas = FloorTiles.WriteTile(atlas, index, TileSplicer.ToFloorTile(pixels, pal));
        DatSave.Write(_cache, catalog, entry?.Name ?? "TILEA.BMP", atlas);
        LoadFiles();
        FileRow? row = _files.FirstOrDefault(f => f.Name.Equals(entry?.Name ?? "TILEA.BMP", StringComparison.OrdinalIgnoreCase));
        if (row is not null)
        {
            FileList.SelectedItem = row;
            EnsureLoaded(row);
            FrameSlider.Maximum = Math.Max(0, row.FrameCount - 1);
            FrameSlider.Value = index;
        }

        StatusText.Text = $"Imported floor tile {index + 1} into {entry?.Name ?? "TILEA.BMP"}.";
    }

    private Palette? PickPalette(IArchive archive)
    {
        var names = PaletteLocator.Palettes(archive).Select(e => e.Name).ToList();
        if (names.Count == 0)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Palettes|*.pal|All files|*.*" };
            if (dialog.ShowDialog(this) != true)
            {
                return null;
            }

            using Stream stream = File.OpenRead(dialog.FileName);
            return new Palette(stream);
        }

        var pick = new Window
        {
            Title = "Remap onto palette",
            Width = 380,
            Height = 460,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background
        };
        var list = new System.Windows.Controls.ListBox { ItemsSource = names, Margin = new Thickness(8) };
        if (names.Count > 0)
        {
            list.SelectedIndex = 0;
        }

        string? chosen = null;
        var ok = new System.Windows.Controls.Button { Content = "Use palette", Margin = new Thickness(8), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        ok.Click += (_, _) =>
        {
            chosen = list.SelectedItem as string;
            pick.DialogResult = true;
        };
        list.MouseDoubleClick += (_, _) =>
        {
            chosen = list.SelectedItem as string;
            pick.DialogResult = true;
        };
        var dock = new DockPanel();
        DockPanel.SetDock(ok, Dock.Bottom);
        dock.Children.Add(ok);
        dock.Children.Add(list);
        pick.Content = dock;
        return pick.ShowDialog() == true && chosen is not null ? PaletteLocator.Named(archive, chosen) : null;
    }

    private static void RemapRow(FileRow row, Palette dest)
    {
        Palette source = row.Palette ?? dest;
        if (row.FloorAtlas is not null)
        {
            int count = FloorTiles.Count(row.FloorAtlas.Length);
            byte[] atlas = row.FloorAtlas;
            for (int i = 0; i < count; i++)
            {
                atlas = FloorTiles.WriteTile(atlas, i, PaletteRemap.Remap(FloorTiles.ReadTile(atlas, i), source, dest));
            }

            row.FloorAtlas = atlas;
            row.Palette = dest;
            return;
        }

        if (row.Indexed is not null)
        {
            row.Indexed = PaletteRemap.Remap(row.Indexed, source, dest);
            row.Palette = dest;
            return;
        }

        if (row.EpfFrames is not null)
        {
            for (int i = 0; i < row.EpfFrames.Count; i++)
            {
                EpfFrame frame = row.EpfFrames[i];
                row.EpfFrames[i] = new EpfFrame(frame.Top, frame.Left, frame.Bottom, frame.Right, PaletteRemap.Remap(frame.Data, source, dest));
            }

            row.Palette = dest;
            return;
        }

        if (row.Spf is null || row.Spf.Format == SpfFormat.Colorized)
        {
            return;
        }

        List<SpfFrame> frames = row.SpfFrames ?? row.Spf.Frames.ToList();
        for (int i = 0; i < frames.Count; i++)
        {
            SpfFrame frame = frames[i];
            frames[i] = new SpfFrame(
                frame.Left,
                frame.Top,
                frame.Width,
                frame.Height,
                PaletteRemap.Remap(frame.Indexed, source, dest),
                null,
                frame.CenterX,
                frame.CenterY);
        }

        row.SpfFrames = frames;
        row.Palette = dest;
    }

    private static int FloorIdFromName(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        var digits = new string(stem.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out int n) ? n : 0;
    }

    private sealed class GalleryTile
    {
        public GalleryTile(FileRow row, int frame, string label, System.Windows.Media.Imaging.BitmapSource image)
        {
            Row = row;
            Frame = frame;
            Label = label;
            Image = image;
        }

        public FileRow Row { get; }
        public int Frame { get; }
        public string Label { get; }
        public System.Windows.Media.Imaging.BitmapSource Image { get; }
    }

    private sealed class FileRow
    {
        public string Name { get; set; } = "";
        public CatalogEntry Catalog { get; set; } = new();
        public ArchiveEntry? Entry { get; set; }
        public bool Loaded { get; set; }
        public bool Dirty { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int FrameCount { get; set; } = 1;
        public Palette? Palette { get; set; }
        public HpfFile? Hpf { get; set; }
        public byte[]? Indexed { get; set; }
        public SpfFile? Spf { get; set; }
        public List<SpfFrame>? SpfFrames { get; set; }
        public EfaFile? Efa { get; set; }
        public EfaBlend Blend { get; set; }
        public EffectCenterTable? Centers { get; set; }
        public List<EfaFrame>? EfaFrames { get; set; }
        public EpfFile? Epf { get; set; }
        public List<EpfFrame>? EpfFrames { get; set; }
        public List<PixelBuffer>? RgbaFrames { get; set; }
        public byte[]? FloorAtlas { get; set; }
        public bool SaveAsPcx { get; set; }
        public HeaFile? Hea { get; set; }
        public FntFile? Fnt { get; set; }
        public LftFile? Lft { get; set; }
        public UpfFile? Upf { get; set; }
        public string Label
        {
            get
            {
                int wall = WallFamilies.IdFromName(Name);
                if (wall > 0)
                {
                    return $"{WallFamilies.Label(wall)}{(Dirty ? " *" : "")}  ({Catalog.DisplayName})";
                }

                return $"{Name}{(Dirty ? " *" : "")}  ({Catalog.DisplayName})";
            }
        }

        public bool Matches(string q) =>
            string.IsNullOrWhiteSpace(q)
            || Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || Label.Contains(q, StringComparison.OrdinalIgnoreCase);
    }
}
