using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DarkAges.Formats.Catalog;
using DarkAges.Formats.Data;
using DarkAges.Formats.Drawing;
using DarkAges.Formats.Imaging;
using DarkAges.Formats.IO;
using DarkAges.Viewer.Imaging;
using DarkAges.Viewer.Services;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace DarkAges.Viewer;

public partial class ItemEditorWindow : Window
{
    private readonly AssetWorkspace? _workspace;
    private readonly ArchiveCache? _cache;
    private readonly List<CatalogEntry> _catalogs;
    private readonly ObservableCollection<ItemDraft> _items = [];
    private readonly Dictionary<string, SheetCache> _sheets = new(StringComparer.OrdinalIgnoreCase);
    private ItemNameIndex _names = new();
    private bool _syncing;
    private ItemDraft? _current;

    public ItemEditorWindow(AssetWorkspace? workspace, ArchiveCache? cache, IReadOnlyList<CatalogEntry> catalogs)
    {
        InitializeComponent();
        _workspace = workspace;
        _cache = cache;
        _catalogs = catalogs.ToList();
        ItemList.ItemsSource = _items;
        Paint.PixelsChanged += PaintPixelsChanged;
        string client = _workspace?.ActiveClient?.Name ?? "no client";
        Title = $"Item Editor — {client}";
        LoadItems();
    }

    private void LoadItems()
    {
        _items.Clear();
        _sheets.Clear();
        if (_cache is null)
        {
            StatusText.Text = "Load a client first.";
            return;
        }

        LoadNames();
        foreach (CatalogEntry catalog in _catalogs.Where(c => c.Role is ArchiveRole.Items or ArchiveRole.Misc or ArchiveRole.Unknown))
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

            PaletteTable? table = ItemPaletteTable(archive);
            foreach (ArchiveEntry entry in archive.Entries.Where(LegendItems.IsRgbaItem).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                int id = LegendItems.RgbaItemId(entry);
                if (id <= 0)
                {
                    continue;
                }

                ApplyMeta(id, out string name, out string level, out string cls, out string weight, out string category, out string description);
                _items.Add(new ItemDraft
                {
                    Id = id,
                    Name = name,
                    Level = level,
                    Class = cls,
                    Weight = weight,
                    Category = category,
                    Description = description,
                    FileName = entry.Name,
                    FrameIndex = 0,
                    Catalog = catalog,
                    Entry = entry,
                    PaletteNumber = 0
                });
            }

            foreach (ArchiveEntry entry in archive.Entries.Where(LegendItems.IsItemSheet).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                int fileNumber = LegendItems.FileNumber(entry);
                IReadOnlyList<EpfFrameHeader> headers;
                try
                {
                    headers = EpfFile.PeekFrames(entry);
                }
                catch
                {
                    continue;
                }

                for (int i = 0; i < headers.Count; i++)
                {
                    if (headers[i].Width <= 0 || headers[i].Height <= 0)
                    {
                        continue;
                    }

                    int id = LegendItems.ItemId(fileNumber, i);
                    ApplyMeta(id, out string name, out string level, out string cls, out string weight, out string category, out string description);
                    _items.Add(new ItemDraft
                    {
                        Id = id,
                        Name = name,
                        Level = level,
                        Class = cls,
                        Weight = weight,
                        Category = category,
                        Description = description,
                        FileName = entry.Name,
                        FrameIndex = i,
                        Catalog = catalog,
                        Entry = entry,
                        PaletteNumber = table?.GetPaletteNumber(id) ?? 0
                    });
                }
            }
        }

        ApplyFilter();
        int named = _items.Count(i => !i.Name.StartsWith("item ", StringComparison.Ordinal));
        StatusText.Text = _items.Count == 0
            ? "No item sheets (item###.epf) or AoC item_*.rgba in this client."
            : $"{_items.Count} graphics, {named} named. Names come from ItemDesc (graphic id), your saved labels, then item ####.";
        if (ItemList.Items.Count > 0)
        {
            ItemList.SelectedIndex = 0;
        }
    }

    private void LoadNames()
    {
        var metas = new List<IArchive>();
        foreach (CatalogEntry catalog in _catalogs.Where(c => c.Role == ArchiveRole.Metadata))
        {
            try
            {
                metas.Add(_cache!.Get(catalog));
            }
            catch
            {
                // skip
            }
        }

        _names = ItemNameIndex.Load(metas);
    }

    private void ApplyMeta(
        int id,
        out string name,
        out string level,
        out string cls,
        out string weight,
        out string category,
        out string description)
    {
        name = "";
        if (_workspace?.ActiveClient?.ItemNames is { Count: > 0 } saved
            && saved.TryGetValue(id, out string? custom)
            && !string.IsNullOrWhiteSpace(custom))
        {
            name = custom.Trim();
        }

        IReadOnlyList<string> aliases = _names.Names(id);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = aliases.Count == 0 ? $"item {id:D4}" : string.Join(" / ", aliases);
        }

        level = cls = weight = category = description = "";
        foreach (string candidate in aliases.Prepend(name).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_names.TryEncyclopedia(candidate, out EncyclopediaRow row))
            {
                continue;
            }

            level = row.Level;
            cls = row.Class;
            weight = row.Weight;
            category = row.Category;
            description = row.Description;
            break;
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            description = _names.Description(id);
        }
    }

    private void RememberName(ItemDraft item)
    {
        ClientProfile? client = _workspace?.ActiveClient;
        if (client is null || string.IsNullOrWhiteSpace(item.Name) || item.Name.StartsWith("item ", StringComparison.Ordinal))
        {
            return;
        }

        client.ItemNames[item.Id] = item.Name.Trim();
        _workspace?.Settings.Save();
    }

    private static PixelBuffer? DecodeRgba(ArchiveEntry entry)
    {
        try
        {
            IReadOnlyList<RgbaImage> frames = RgbaImage.DecodeAll(entry.ReadAllBytes(), 1);
            return frames.Count == 0 ? null : frames[0].Pixels;
        }
        catch
        {
            return null;
        }
    }

    private static PaletteTable? ItemPaletteTable(IArchive archive)
    {
        ArchiveEntry? table = archive.GetEntry("itempal.tbl");
        if (table is null)
        {
            return null;
        }

        var pal = new PaletteTable();
        pal.AddTable(table);
        return pal;
    }

    private void FilterChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string q = FilterBox.Text.Trim();
        ItemList.Items.Filter = q.Length == 0
            ? null
            : obj => obj is ItemDraft item && item.Matches(q);
    }

    private void ItemSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ItemList.SelectedItem is not ItemDraft item)
        {
            return;
        }

        ShowItem(item);
    }

    private void ShowItem(ItemDraft item)
    {
        _syncing = true;
        _current = item;
        IdBox.Text = item.Id.ToString();
        NameBox.Text = item.Name;
        LevelBox.Text = item.Level;
        ClassBox.Text = item.Class;
        WeightBox.Text = item.Weight;
        CategoryBox.Text = item.Category;
        DescriptionBox.Text = item.Description;
        FileBox.Text = item.FileName;
        FrameBox.Text = item.FrameIndex.ToString();
        PaletteBox.Text = item.PaletteNumber.ToString();
        SourceBox.Text = item.Catalog?.DisplayName ?? (item.IsNew ? "new" : "");
        LoadPaint(item);
        _syncing = false;
        StatusText.Text = item.IsNew
            ? $"New item {item.Id}. Import a sprite, fill the fields, then Export pack."
            : $"{item.ListLabel}";
    }

    private void LoadPaint(ItemDraft item)
    {
        try
        {
            PixelBuffer pixels = CopyRendered(item) ?? BlankSprite();
            Paint.Load(pixels, IsRgba(item) ? null : ItemPalette(item));
        }
        catch
        {
            Paint.Load(BlankSprite(), null);
        }
    }

    private void PaintPixelsChanged(object? sender, EventArgs e)
    {
        if (_syncing || _current is null || Paint.Pixels is null)
        {
            return;
        }

        ApplyPainted(Paint.Pixels.Clone());
        StatusText.Text = $"Painted item {_current.Id}. Save to DAT when ready.";
    }

    private void ApplyPainted(PixelBuffer pixels)
    {
        if (_current is null)
        {
            return;
        }

        if (IsRgba(_current))
        {
            _current.Replacement = pixels;
        }
        else
        {
            Palette pal = ItemPalette(_current);
            byte[] indexed = pal.Quantize(pixels);
            var frame = new EpfFrame(0, 0, pixels.Height, pixels.Width, indexed);
            _current.Replacement = pal.Render(indexed, pixels.Width, pixels.Height);
            GrowSheet(_current, _current.Replacement);
            PatchFrame(_current, frame);
        }

        _current.Dirty = true;
    }

    private Palette ItemPalette(ItemDraft item)
    {
        if (item.Catalog is not null && _cache is not null)
        {
            IArchive archive = _cache.Get(item.Catalog);
            return PaletteLocator.FindNumbered(archive, "item", item.PaletteNumber)
                ?? PaletteLocator.Find(archive, "item000.pal")
                ?? FallbackPalette();
        }

        return FallbackPalette();
    }

    private static Palette FallbackPalette()
    {
        var data = new byte[768];
        for (int i = 0; i < 256; i++)
        {
            data[i * 3] = (byte)i;
            data[i * 3 + 1] = (byte)i;
            data[i * 3 + 2] = (byte)i;
        }

        return new Palette(new MemoryStream(data));
    }

    private static bool IsRgba(ItemDraft item) =>
        item.FileName.EndsWith(".rgba", StringComparison.OrdinalIgnoreCase)
        || item.Entry?.Extension.Equals(".rgba", StringComparison.OrdinalIgnoreCase) == true;

    private void EnsureRgba(ItemDraft item)
    {
        if (item.Replacement is not null || item.Entry is null)
        {
            return;
        }

        item.Replacement = DecodeRgba(item.Entry);
    }

    private void EnsureSheet(ItemDraft item)
    {
        if (IsRgba(item) || item.Entry is null || _sheets.ContainsKey(SheetKey(item)))
        {
            return;
        }

        var epf = new EpfFile(item.Entry);
        _sheets[SheetKey(item)] = new SheetCache
        {
            Width = Math.Max(1, epf.Width),
            Height = Math.Max(1, epf.Height),
            Frames = epf.Frames.ToList()
        };
    }

    private EpfFrame? FrameOf(ItemDraft item)
    {
        if (!_sheets.TryGetValue(SheetKey(item), out SheetCache? sheet))
        {
            return null;
        }

        return (uint)item.FrameIndex < (uint)sheet.Frames.Count ? sheet.Frames[item.FrameIndex] : null;
    }

    private static string SheetKey(ItemDraft item) =>
        $"{item.Catalog?.SourcePath}\0{item.FileName}";

    private void FieldChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || _current is null)
        {
            return;
        }

        _current.Name = NameBox.Text;
        _current.Level = LevelBox.Text;
        _current.Class = ClassBox.Text;
        _current.Weight = WeightBox.Text;
        _current.Category = CategoryBox.Text;
        _current.Description = DescriptionBox.Text;
        if (int.TryParse(IdBox.Text.Trim(), out int id) && id > 0)
        {
            _current.Id = id;
        }

        _current.Dirty = true;
        ItemList.Items.Refresh();
    }

    private void NewClicked(object sender, RoutedEventArgs e)
    {
        int nextId = _items.Count == 0 ? 1 : _items.Max(i => i.Id) + 1;
        ItemDraft? source = _current;
        CatalogEntry? catalog = source?.Catalog ?? _catalogs.FirstOrDefault(c => c.Role == ArchiveRole.Items);
        bool rgba = source is not null && IsRgba(source)
            || (catalog is not null && LrarArchive.IsLrarPath(catalog.SourcePath));
        int fileNumber = (nextId - 1) / LegendItems.TilesPerFile + 1;
        int frame = rgba ? 0 : (nextId - 1) % LegendItems.TilesPerFile;
        string fileName = rgba ? $"item_{nextId:D5}.rgba" : $"item{fileNumber:D3}.epf";
        var item = new ItemDraft
        {
            Id = nextId,
            Name = "New item",
            FileName = fileName,
            FrameIndex = frame,
            Catalog = catalog,
            Entry = source?.Entry,
            PaletteNumber = source?.PaletteNumber ?? 0,
            IsNew = true,
            Dirty = true,
            Replacement = source?.Replacement
                ?? (source is not null ? CopyRendered(source) : BlankSprite())
        };
        GrowSheet(item, item.Replacement);
        _items.Add(item);
        ApplyFilter();
        ItemList.SelectedItem = item;
        ItemList.ScrollIntoView(item);
        StatusText.Text = $"Created item {nextId} ({fileName} frame {frame}). Import a sprite and Export pack.";
    }

    private PixelBuffer? CopyRendered(ItemDraft item)
    {
        try
        {
            if (IsRgba(item))
            {
                EnsureRgba(item);
                return item.Replacement;
            }

            EnsureSheet(item);
            EpfFrame? frame = FrameOf(item);
            return item.Replacement ?? frame?.Render(ItemPalette(item));
        }
        catch
        {
            return null;
        }
    }

    private static PixelBuffer BlankSprite()
    {
        var buf = new PixelBuffer(32, 32);
        buf.Clear();
        return buf;
    }

    private void ImportSpriteClicked(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            StatusText.Text = "Select or create an item first.";
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Images|*.png;*.bmp;*.jpg;*.jpeg;*.gif|All files|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        PixelBuffer? pixels = WpfImages.TryDecodeImage(File.ReadAllBytes(dialog.FileName));
        if (pixels is null)
        {
            System.Windows.MessageBox.Show(this, "Could not read that image.", "Import sprite");
            return;
        }

        pixels = pixels.CropVisible(pad: 0, out _, out _);
        if (IsRgba(_current))
        {
            _current.Replacement = pixels;
        }
        else
        {
            Palette pal = ItemPalette(_current);
            byte[] indexed = pal.Quantize(pixels);
            var frame = new EpfFrame(0, 0, pixels.Height, pixels.Width, indexed);
            _current.Replacement = pal.Render(indexed, pixels.Width, pixels.Height);
            GrowSheet(_current, _current.Replacement);
            PatchFrame(_current, frame);
        }
        _current.Dirty = true;
        LoadPaint(_current);
        StatusText.Text = $"Imported {Path.GetFileName(dialog.FileName)} onto item {_current.Id}.";
    }

    private void GrowSheet(ItemDraft item, PixelBuffer? sprite)
    {
        if (IsRgba(item))
        {
            return;
        }

        string key = SheetKey(item);
        if (!_sheets.TryGetValue(key, out SheetCache? sheet))
        {
            EnsureSheet(item);
            if (!_sheets.TryGetValue(key, out sheet))
            {
                sheet = new SheetCache { Width = 32, Height = 32, Frames = [] };
                _sheets[key] = sheet;
            }
        }

        while (sheet.Frames.Count <= item.FrameIndex)
        {
            sheet.Frames.Add(new EpfFrame(0, 0, 0, 0, []));
        }

        if (sprite is not null)
        {
            sheet.Width = Math.Max(sheet.Width, sprite.Width);
            sheet.Height = Math.Max(sheet.Height, sprite.Height);
        }
    }

    private void PatchFrame(ItemDraft item, EpfFrame frame)
    {
        GrowSheet(item, null);
        _sheets[SheetKey(item)].Frames[item.FrameIndex] = frame;
    }

    private void OpenButtonMenu(object sender, RoutedEventArgs e) => ButtonMenu.Open(sender);

    private void ExportDataClicked(object sender, RoutedEventArgs e)
    {
        IEnumerable<ItemDraft> items = CollectionViewSource.GetDefaultView(_items).Cast<ItemDraft>();
        TableDump dump = TableDump.Create(
            "Items",
            ["Id", "Name", "Level", "Class", "Weight", "Category", "Description", "FileName", "FrameIndex", "Palette"],
            items.Select(i => (IReadOnlyList<object?>)new object?[]
            {
                i.Id, i.Name, i.Level, i.Class, i.Weight, i.Category, i.Description, i.FileName, i.FrameIndex, i.PaletteNumber
            }));
        TableExport.Save(this, "items", dump, sender, StatusText);
    }

    private void ExportPackClicked(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        PullFields();
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Folder for the item pack (JSON + PNG + EPF)",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        try
        {
            string stem = SafeName($"item_{_current.Id:D4}_{_current.Name}");
            string folder = dialog.SelectedPath;
            string png = Path.Combine(folder, stem + ".png");
            string json = Path.Combine(folder, stem + ".json");
            string epf = Path.Combine(folder, _current.FileName);
            PixelBuffer? pixels = CopyRendered(_current);
            if (pixels is not null)
            {
                PngWriter.Write(pixels, png);
            }

            File.WriteAllText(json, JsonSerializer.Serialize(new ItemPack
            {
                Kind = "item",
                Id = _current.Id,
                Name = _current.Name,
                Level = _current.Level,
                Class = _current.Class,
                Weight = _current.Weight,
                Category = _current.Category,
                Description = _current.Description,
                SpriteFile = _current.FileName,
                FrameIndex = _current.FrameIndex,
                Palette = _current.PaletteNumber,
                Client = _workspace?.ActiveClient?.Name ?? "",
                SpritePng = Path.GetFileName(png),
                EpfFile = _current.FileName
            }, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllBytes(epf, BuildSprite(_current));
            _current.Dirty = false;
            StatusText.Text = $"Wrote {stem}.json, PNG, and {_current.FileName}.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Export pack");
        }
    }

    private void SaveEpfClicked(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        PullFields();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Sprite|*.epf;*.rgba|EPF sprite sheet|*.epf|RGBA sprite|*.rgba|All files|*.*",
            FileName = _current.FileName
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllBytes(dialog.FileName, BuildSprite(_current));
            StatusText.Text = $"Saved {dialog.FileName}";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Save EPF");
        }
    }

    private void SaveToDatClicked(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        PullFields();
        CatalogEntry? catalog = _current.Catalog ?? _catalogs.FirstOrDefault(c => c.Role == ArchiveRole.Items);
        if (catalog is null || !DatSave.IsWritable(catalog))
        {
            System.Windows.MessageBox.Show(this, "This item isn't tied to a writable DAT.", "Save to DAT");
            return;
        }

        try
        {
            byte[] bytes = BuildSprite(_current);
            DatSave.Write(_cache, catalog, _current.FileName, bytes);
            _current.Dirty = false;
            StatusText.Text = $"Saved {_current.FileName} into {catalog.DisplayName} (no unpack).";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Save to DAT");
        }
    }

    private byte[] BuildSprite(ItemDraft item) => IsRgba(item) ? BuildRgba(item) : BuildEpf(item);

    private byte[] BuildRgba(ItemDraft item)
    {
        EnsureRgba(item);
        return RgbaImage.Write(item.Replacement ?? new PixelBuffer(1, 1));
    }

    private byte[] BuildEpf(ItemDraft item)
    {
        GrowSheet(item, item.Replacement);
        if (item.Replacement is not null)
        {
            Palette pal = ItemPalette(item);
            byte[] indexed = pal.Quantize(item.Replacement);
            PatchFrame(item, new EpfFrame(0, 0, item.Replacement.Height, item.Replacement.Width, indexed));
        }

        SheetCache sheet = _sheets[SheetKey(item)];
        return EpfWriter.Write(sheet.Width, sheet.Height, sheet.Frames);
    }

    private void PullFields()
    {
        if (_current is null)
        {
            return;
        }

        _current.Name = NameBox.Text.Trim();
        _current.Level = LevelBox.Text.Trim();
        _current.Class = ClassBox.Text.Trim();
        _current.Weight = WeightBox.Text.Trim();
        _current.Category = CategoryBox.Text.Trim();
        _current.Description = DescriptionBox.Text.Trim();
        if (int.TryParse(IdBox.Text.Trim(), out int id) && id > 0)
        {
            _current.Id = id;
        }

        RememberName(_current);
    }

    private static string SafeName(string value)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(c, '_');
        }

        return string.IsNullOrWhiteSpace(value) ? "item" : value.Trim();
    }

    private sealed class ItemDraft
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Level { get; set; } = "";
        public string Class { get; set; } = "";
        public string Weight { get; set; } = "";
        public string Category { get; set; } = "";
        public string Description { get; set; } = "";
        public string FileName { get; set; } = "";
        public int FrameIndex { get; set; }
        public int PaletteNumber { get; set; }
        public CatalogEntry? Catalog { get; set; }
        public ArchiveEntry? Entry { get; set; }
        public PixelBuffer? Replacement { get; set; }
        public bool IsNew { get; set; }
        public bool Dirty { get; set; }
        public string ListLabel => $"{Id:D4}  {Name}{(Dirty ? " *" : "")}";

        public bool Matches(string q) =>
            Id.ToString().Contains(q, StringComparison.OrdinalIgnoreCase)
            || Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || FileName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || Category.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SheetCache
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public List<EpfFrame> Frames { get; set; } = [];
    }

    private sealed class ItemPack
    {
        public string Kind { get; set; } = "item";
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Level { get; set; } = "";
        public string Class { get; set; } = "";
        public string Weight { get; set; } = "";
        public string Category { get; set; } = "";
        public string Description { get; set; } = "";
        public string SpriteFile { get; set; } = "";
        public int FrameIndex { get; set; }
        public int Palette { get; set; }
        public string Client { get; set; } = "";
        public string SpritePng { get; set; } = "";
        public string EpfFile { get; set; } = "";
    }
}
