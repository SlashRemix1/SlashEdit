using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DarkAges.Formats.Catalog;
using DarkAges.Formats.Data;
using DarkAges.Formats.Drawing;
using DarkAges.Formats.Imaging;
using DarkAges.Formats.IO;
using DarkAges.Viewer.Imaging;
using DarkAges.Viewer.Preview;
using DarkAges.Viewer.Services;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace DarkAges.Viewer;

public partial class MonsterEditorWindow : Window
{
    private readonly AssetWorkspace? _workspace;
    private readonly ArchiveCache? _cache;
    private readonly List<CatalogEntry> _catalogs;
    private readonly ObservableCollection<MonsterDraft> _monsters = [];
    private bool _syncing;
    private MonsterDraft? _current;

    public MonsterEditorWindow(AssetWorkspace? workspace, ArchiveCache? cache, IReadOnlyList<CatalogEntry> catalogs)
    {
        InitializeComponent();
        _workspace = workspace;
        _cache = cache;
        _catalogs = catalogs.ToList();
        MonsterList.ItemsSource = _monsters;
        Paint.PixelsChanged += PaintPixelsChanged;
        string client = _workspace?.ActiveClient?.Name ?? "no client";
        Title = $"Monster Editor — {client}";
        LoadMonsters();
    }

    private void LoadMonsters()
    {
        _monsters.Clear();
        if (_cache is null)
        {
            StatusText.Text = "Load a client first.";
            return;
        }

        foreach (CatalogEntry catalog in _catalogs.Where(c => c.Role is ArchiveRole.Mobs or ArchiveRole.Misc or ArchiveRole.Unknown))
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

            foreach (ArchiveEntry entry in archive.Entries.Where(IsMonster).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                _monsters.Add(new MonsterDraft
                {
                    Name = Path.GetFileNameWithoutExtension(entry.Name),
                    FileName = entry.Name,
                    Catalog = catalog,
                    Entry = entry
                });
            }
        }

        ApplyFilter();
        StatusText.Text = _monsters.Count == 0
            ? "No mns*.mpf or AoC body_*.rgba sprites in this client."
            : $"{_monsters.Count} monsters. Select one, or New monster.";
        if (MonsterList.Items.Count > 0)
        {
            MonsterList.SelectedIndex = 0;
        }
    }

    private static bool IsMonster(ArchiveEntry entry)
    {
        string stem = Path.GetFileNameWithoutExtension(entry.Name);
        if (entry.Extension.Equals(".rgba", StringComparison.OrdinalIgnoreCase))
        {
            return stem.StartsWith("body_", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("banim_", StringComparison.OrdinalIgnoreCase);
        }

        return entry.Extension.Equals(".mpf", StringComparison.OrdinalIgnoreCase)
            && stem.StartsWith("mns", StringComparison.OrdinalIgnoreCase);
    }

    private void FilterChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string q = FilterBox.Text.Trim();
        MonsterList.Items.Filter = q.Length == 0
            ? null
            : obj => obj is MonsterDraft m && m.Matches(q);
    }

    private void MonsterSelected(object sender, SelectionChangedEventArgs e)
    {
        if (MonsterList.SelectedItem is MonsterDraft monster)
        {
            ShowMonster(monster);
        }
    }

    private void ShowMonster(MonsterDraft monster)
    {
        EnsureLoaded(monster);
        _syncing = true;
        _current = monster;
        NameBox.Text = monster.Name;
        FileBox.Text = monster.FileName;
        PaletteBox.Text = monster.PaletteNumber.ToString();
        SizeBox.Text = $"{monster.Width}×{monster.Height}  {monster.Frames.Count} frames";
        IdleIndexBox.Text = monster.IdleIndex.ToString();
        IdleCountBox.Text = monster.IdleCount.ToString();
        WalkIndexBox.Text = monster.WalkIndex.ToString();
        WalkCountBox.Text = monster.WalkCount.ToString();
        AttackIndexBox.Text = monster.AttackIndex.ToString();
        AttackCountBox.Text = monster.AttackCount.ToString();
        Attack2IndexBox.Text = monster.Attack2Index.ToString();
        Attack2CountBox.Text = monster.Attack2Count.ToString();
        Attack3IndexBox.Text = monster.Attack3Index.ToString();
        Attack3CountBox.Text = monster.Attack3Count.ToString();
        StopMotionBox.Text = monster.StopMotion.ToString();
        StopMotionPctBox.Text = monster.StopMotionPct.ToString();
        NotesBox.Text = monster.Notes;
        FrameSlider.Maximum = Math.Max(0, monster.Frames.Count - 1);
        FrameSlider.Value = Math.Min(FrameSlider.Value, FrameSlider.Maximum);
        FillPalettePick(monster);
        _syncing = false;
        RefreshFrame();
        BindColorVariants(monster);
        StatusText.Text = monster.IsNew
            ? $"New monster {monster.FileName}. Import frames, then Export pack."
            : monster.ListLabel;
    }

    private void FillPalettePick(MonsterDraft monster)
    {
        if (PalettePickBox is null || _cache is null || monster.Catalog is null)
        {
            return;
        }

        IArchive archive = _cache.Get(monster.Catalog);
        var names = PaletteLocator.Palettes(archive)
            .Where(e => e.Name.StartsWith("mns", StringComparison.OrdinalIgnoreCase)
                        && e.Extension.Equals(".pal", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Name)
            .ToList();
        PalettePickBox.ItemsSource = names;
        string baked = $"mns{monster.PaletteNumber:D3}.pal";
        PalettePickBox.SelectedItem = names.FirstOrDefault(n => n.Equals(baked, StringComparison.OrdinalIgnoreCase))
            ?? names.FirstOrDefault(n =>
            {
                RelatedFiles.TryParse(n, out _, out int number);
                return number == monster.PaletteNumber;
            });
    }

    private void BindColorVariants(MonsterDraft monster)
    {
        if (ColorVariantHost is null)
        {
            return;
        }

        if (monster.Catalog is null || monster.Entry is null || _cache is null)
        {
            ColorVariantHost.ItemsSource = null;
            return;
        }

        try
        {
            IArchive archive = _cache.Get(monster.Catalog);
            ColorVariantHost.ItemsSource = MonsterColors.For(archive, monster.Entry);
        }
        catch
        {
            ColorVariantHost.ItemsSource = null;
        }
    }

    private void PalettePicked(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || PalettePickBox?.SelectedItem is not string name)
        {
            return;
        }

        RelatedFiles.TryParse(name, out _, out int number);
        PaletteBox.Text = number.ToString();
        RefreshFrame();
    }

    private void ColorVariantClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not MonsterColorVariant variant)
        {
            return;
        }

        if (variant.SiblingFile is not null)
        {
            MonsterDraft? sibling = _monsters.FirstOrDefault(m =>
                m.FileName.Equals(variant.SiblingFile, StringComparison.OrdinalIgnoreCase));
            if (sibling is not null)
            {
                MonsterList.SelectedItem = sibling;
                MonsterList.ScrollIntoView(sibling);
            }

            return;
        }

        if (variant.PaletteName is not null)
        {
            PalettePickBox.SelectedItem = variant.PaletteName;
            PaletteBox.Text = variant.PaletteNumber.ToString();
            RefreshFrame();
        }
    }

    private void FrameChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_syncing)
        {
            RefreshFrame();
        }
    }

    private void RefreshFrame()
    {
        if (_current is null)
        {
            return;
        }

        int index = (int)FrameSlider.Value;
        FrameLabel.Text = _current.Frames.Count == 0 ? "0/0" : $"{index + 1}/{_current.Frames.Count}";
        LoadPaint(_current, index);
    }

    private void LoadPaint(MonsterDraft monster, int index)
    {
        if ((uint)index >= (uint)monster.Frames.Count)
        {
            Paint.Clear();
            return;
        }

        try
        {
            Palette pal = MonsterPalette(monster);
            MpfFrame frame = monster.Frames[index];
            PixelBuffer img = monster.Replacements.TryGetValue(index, out PixelBuffer? replacement)
                ? replacement
                : frame.Render(pal);
            Paint.Load(img, IsRgba(monster) ? null : pal);
        }
        catch
        {
            Paint.Clear();
        }
    }

    private void PaintPixelsChanged(object? sender, EventArgs e)
    {
        if (_syncing || _current is null || Paint.Pixels is null)
        {
            return;
        }

        PixelBuffer pixels = Paint.Pixels.Clone();
        int index = Math.Clamp((int)FrameSlider.Value, 0, Math.Max(0, _current.Frames.Count - 1));
        if (_current.Frames.Count == 0)
        {
            index = 0;
            _current.Frames.Add(new MpfFrame(0, 0, pixels.Height, pixels.Width, 0, 0, []));
        }

        byte[] indexed = [];
        PixelBuffer stored = pixels;
        if (!IsRgba(_current))
        {
            Palette pal = MonsterPalette(_current);
            indexed = pal.Quantize(pixels);
            stored = pal.Render(indexed, pixels.Width, pixels.Height);
        }

        MpfFrame old = _current.Frames[index];
        _current.Frames[index] = new MpfFrame(old.Top, old.Left, old.Top + pixels.Height, old.Left + pixels.Width, old.XOffset, old.YOffset, indexed);
        _current.Replacements[index] = stored;
        _current.Width = Math.Max(_current.Width, pixels.Width);
        _current.Height = Math.Max(_current.Height, pixels.Height);
        _current.Dirty = true;
        StatusText.Text = $"Painted frame {index + 1}. Save to DAT when ready.";
    }

    private System.Windows.Media.Imaging.BitmapSource? RenderFrame(MonsterDraft monster, int index)
    {
        if ((uint)index >= (uint)monster.Frames.Count)
        {
            return null;
        }

        try
        {
            Palette pal = MonsterPalette(monster);
            MpfFrame frame = monster.Frames[index];
            PixelBuffer img = monster.Replacements.TryGetValue(index, out PixelBuffer? replacement)
                ? replacement
                : frame.Render(pal);
            var canvas = new PixelBuffer(Math.Max(monster.Width, frame.Left + img.Width), Math.Max(monster.Height, frame.Top + img.Height));
            canvas.Blit(img, frame.Left, frame.Top);
            return WpfImages.Checkerboard(canvas.Width, canvas.Height, canvas);
        }
        catch
        {
            return null;
        }
    }

    private static void EnsureLoaded(MonsterDraft monster)
    {
        if (monster.Loaded || monster.Entry is null)
        {
            return;
        }

        if (monster.Entry.Extension.Equals(".rgba", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<RgbaImage> images = RgbaImage.DecodeAll(monster.Entry.ReadAllBytes());
            monster.Frames = [];
            monster.Replacements.Clear();
            for (int i = 0; i < images.Count; i++)
            {
                PixelBuffer pixels = images[i].Pixels;
                monster.Frames.Add(new MpfFrame(0, 0, pixels.Height, pixels.Width, 0, 0, []));
                monster.Replacements[i] = pixels;
                monster.Width = Math.Max(monster.Width, pixels.Width);
                monster.Height = Math.Max(monster.Height, pixels.Height);
            }

            if (monster.Frames.Count == 0)
            {
                monster.Frames.Add(new MpfFrame(0, 0, 1, 1, 0, 0, []));
                monster.Replacements[0] = new PixelBuffer(1, 1);
                monster.Width = 1;
                monster.Height = 1;
            }

            monster.IdleCount = Math.Max(1, monster.Frames.Count);
            monster.Loaded = true;
            return;
        }

        var mpf = new MpfFile(monster.Entry);
        monster.PaletteNumber = mpf.PaletteNumber;
        monster.Width = Math.Max(1, mpf.Width);
        monster.Height = Math.Max(1, mpf.Height);
        monster.IdleIndex = mpf.StopFrameIndex;
        monster.IdleCount = mpf.StopFrameCount;
        monster.WalkIndex = mpf.WalkFrameIndex;
        monster.WalkCount = mpf.WalkFrameCount;
        monster.AttackIndex = mpf.AttackFrameIndex;
        monster.AttackCount = mpf.AttackFrameCount;
        monster.Attack2Index = mpf.Attack2FrameIndex;
        monster.Attack2Count = mpf.Attack2FrameCount;
        monster.Attack3Index = mpf.Attack3FrameIndex;
        monster.Attack3Count = mpf.Attack3FrameCount;
        monster.StopMotion = mpf.StopMotionFrameCount;
        monster.StopMotionPct = mpf.StopMotionProbability;
        monster.Frames = mpf.Frames.ToList();
        monster.Loaded = true;
    }

    private Palette MonsterPalette(MonsterDraft monster)
    {
        if (monster.Catalog is not null && _cache is not null)
        {
            IArchive archive = _cache.Get(monster.Catalog);
            return PaletteLocator.FindNumbered(archive, "mns", monster.PaletteNumber)
                ?? (monster.Entry is not null ? PaletteLocator.ForEntry(archive, monster.Entry) : null)
                ?? PaletteLocator.Find(archive, $"mns{monster.PaletteNumber:D3}.pal")
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

    private void FieldChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || _current is null)
        {
            return;
        }

        _current.Name = NameBox.Text;
        _current.FileName = FileBox.Text.Trim();
        _current.Notes = NotesBox.Text;
        _current.PaletteNumber = ParseInt(PaletteBox.Text, _current.PaletteNumber);
        _current.IdleIndex = ParseInt(IdleIndexBox.Text, _current.IdleIndex);
        _current.IdleCount = ParseInt(IdleCountBox.Text, _current.IdleCount);
        _current.WalkIndex = ParseInt(WalkIndexBox.Text, _current.WalkIndex);
        _current.WalkCount = ParseInt(WalkCountBox.Text, _current.WalkCount);
        _current.AttackIndex = ParseInt(AttackIndexBox.Text, _current.AttackIndex);
        _current.AttackCount = ParseInt(AttackCountBox.Text, _current.AttackCount);
        _current.Attack2Index = ParseInt(Attack2IndexBox.Text, _current.Attack2Index);
        _current.Attack2Count = ParseInt(Attack2CountBox.Text, _current.Attack2Count);
        _current.Attack3Index = ParseInt(Attack3IndexBox.Text, _current.Attack3Index);
        _current.Attack3Count = ParseInt(Attack3CountBox.Text, _current.Attack3Count);
        _current.StopMotion = ParseInt(StopMotionBox.Text, _current.StopMotion);
        _current.StopMotionPct = ParseInt(StopMotionPctBox.Text, _current.StopMotionPct);
        _current.Dirty = true;
        MonsterList.Items.Refresh();
        if (ReferenceEquals(sender, PaletteBox))
        {
            RefreshFrame();
        }
    }

    private static int ParseInt(string text, int fallback) =>
        int.TryParse(text.Trim(), out int n) ? n : fallback;

    private void NewClicked(object sender, RoutedEventArgs e)
    {
        CloneFilterBox.Text = "";
        CloneList.ItemsSource = _monsters.ToList();
        CloneList.Items.Filter = null;
        if (_current is not null)
        {
            CloneList.SelectedItem = _current;
            CloneList.ScrollIntoView(_current);
        }
        else if (CloneList.Items.Count > 0)
        {
            CloneList.SelectedIndex = 0;
        }

        UpdateClonePreview();
        NewMonsterOverlay.Visibility = Visibility.Visible;
        CloneFilterBox.Focus();
        CloneFilterBox.SelectAll();
    }

    private void NewMonsterOverlayClicked(object sender, MouseButtonEventArgs e)
    {
        HideNewMonsterOverlay();
        e.Handled = true;
    }

    private void NewMonsterDialogClicked(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void NewCancelClicked(object sender, RoutedEventArgs e) => HideNewMonsterOverlay();

    private void NewBlankClicked(object sender, RoutedEventArgs e) => CreateNewMonster(null);

    private void NewCopyClicked(object sender, RoutedEventArgs e)
    {
        if (CloneList.SelectedItem is MonsterDraft source)
        {
            CreateNewMonster(source);
            return;
        }

        StatusText.Text = "Select a monster to copy, or choose Blank.";
    }

    private void CloneListDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        if (CloneList.SelectedItem is MonsterDraft source)
        {
            CreateNewMonster(source);
        }
    }

    private void CloneListSelected(object sender, SelectionChangedEventArgs e) => UpdateClonePreview();

    private void CloneFilterChanged(object sender, TextChangedEventArgs e)
    {
        string q = CloneFilterBox.Text.Trim();
        CloneList.Items.Filter = q.Length == 0
            ? null
            : obj => obj is MonsterDraft m && m.Matches(q);
        if (CloneList.SelectedItem is null && CloneList.Items.Count > 0)
        {
            CloneList.SelectedIndex = 0;
        }

        UpdateClonePreview();
    }

    private void UpdateClonePreview()
    {
        if (ClonePreview is null)
        {
            return;
        }

        if (CloneList.SelectedItem is not MonsterDraft source)
        {
            ClonePreview.Source = null;
            return;
        }

        EnsureLoaded(source);
        int idle = source.IdleCount > 0
            ? Math.Clamp(source.IdleIndex, 0, Math.Max(0, source.Frames.Count - 1))
            : 0;
        ClonePreview.Source = RenderFrame(source, idle);
    }

    private void HideNewMonsterOverlay()
    {
        NewMonsterOverlay.Visibility = Visibility.Collapsed;
        ClonePreview.Source = null;
    }

    private void WindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (NewMonsterOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            HideNewMonsterOverlay();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && CloneList.SelectedItem is MonsterDraft)
        {
            CreateNewMonster((MonsterDraft)CloneList.SelectedItem);
            e.Handled = true;
        }
    }

    private void CreateNewMonster(MonsterDraft? source)
    {
        HideNewMonsterOverlay();
        int next = NextMonsterNumber();
        CatalogEntry? catalog = source?.Catalog ?? _current?.Catalog ?? _catalogs.FirstOrDefault(c => c.Role == ArchiveRole.Mobs);
        bool rgba = source is not null && IsRgba(source)
            || (catalog is not null && LrarArchive.IsLrarPath(catalog.SourcePath));
        string file = rgba ? $"body_{next:D5}.rgba" : $"mns{next:D4}.mpf";
        if (source is not null)
        {
            EnsureLoaded(source);
        }

        var monster = new MonsterDraft
        {
            Name = source is null ? "New monster" : CopyName(source.Name),
            FileName = file,
            Catalog = catalog,
            PaletteNumber = source?.PaletteNumber ?? 0,
            Width = source?.Width ?? 48,
            Height = source?.Height ?? 48,
            IdleIndex = source?.IdleIndex ?? 0,
            IdleCount = source?.IdleCount > 0 ? source.IdleCount : 1,
            WalkIndex = source?.WalkIndex ?? 0,
            WalkCount = source?.WalkCount ?? 0,
            AttackIndex = source?.AttackIndex ?? 0,
            AttackCount = source?.AttackCount ?? 0,
            Attack2Index = source?.Attack2Index ?? 0,
            Attack2Count = source?.Attack2Count ?? 0,
            Attack3Index = source?.Attack3Index ?? 0,
            Attack3Count = source?.Attack3Count ?? 0,
            StopMotion = source?.StopMotion ?? 0,
            StopMotionPct = source?.StopMotionPct ?? 0,
            Notes = source?.Notes ?? "",
            IsNew = true,
            Loaded = true,
            Dirty = true,
            Frames = source?.Frames.Count > 0
                ? source.Frames.Select(CloneFrame).ToList()
                : [new MpfFrame(0, 0, 48, 48, 0, 0, new byte[48 * 48])]
        };
        if (source is not null)
        {
            foreach (KeyValuePair<int, PixelBuffer> pair in source.Replacements)
            {
                monster.Replacements[pair.Key] = ClonePixels(pair.Value);
            }
        }

        _monsters.Add(monster);
        ApplyFilter();
        MonsterList.SelectedItem = monster;
        MonsterList.ScrollIntoView(monster);
        StatusText.Text = source is null
            ? $"Created {file}. Import frames and Export pack."
            : $"Created {file} from {source.FileName}. Edit freely; the original is unchanged.";
    }

    private static string CopyName(string name)
    {
        name = name.Trim();
        if (string.IsNullOrEmpty(name) || name.Equals("New monster", StringComparison.OrdinalIgnoreCase))
        {
            return "New monster";
        }

        return name.StartsWith("Copy of ", StringComparison.OrdinalIgnoreCase) ? name : "Copy of " + name;
    }

    private static PixelBuffer ClonePixels(PixelBuffer source)
    {
        var copy = new PixelBuffer(source.Width, source.Height);
        Buffer.BlockCopy(source.Bgra, 0, copy.Bgra, 0, source.Bgra.Length);
        return copy;
    }

    private int NextMonsterNumber()
    {
        int max = 0;
        foreach (MonsterDraft monster in _monsters)
        {
            string digits = new(Path.GetFileNameWithoutExtension(monster.FileName)
                .SkipWhile(c => !char.IsDigit(c))
                .TakeWhile(char.IsDigit)
                .ToArray());
            if (int.TryParse(digits, out int n))
            {
                max = Math.Max(max, n);
            }
        }

        return max + 1;
    }

    private static MpfFrame CloneFrame(MpfFrame frame) =>
        new(frame.Top, frame.Left, frame.Bottom, frame.Right, frame.XOffset, frame.YOffset, frame.Data.ToArray());

    private void ImportFrameClicked(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            StatusText.Text = "Select or create a monster first.";
            return;
        }

        PixelBuffer? pixels = PickImage();
        if (pixels is null)
        {
            return;
        }

        ReplaceCurrentFrame(pixels);
    }

    private void AddFramesClicked(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            StatusText.Text = "Select or create a monster first.";
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Images|*.png;*.bmp;*.jpg;*.jpeg;*.gif|All files|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        int added = 0;
        foreach (string path in dialog.FileNames)
        {
            PixelBuffer? pixels = WpfImages.TryDecodeImage(File.ReadAllBytes(path));
            if (pixels is null)
            {
                continue;
            }

            AppendFrame(pixels);
            added++;
        }

        ShowMonster(_current);
        StatusText.Text = $"Added {added} frame(s).";
    }

    private PixelBuffer? PickImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.png;*.bmp;*.jpg;*.jpeg;*.gif|All files|*.*" };
        if (dialog.ShowDialog(this) != true)
        {
            return null;
        }

        PixelBuffer? pixels = WpfImages.TryDecodeImage(File.ReadAllBytes(dialog.FileName));
        if (pixels is null)
        {
            System.Windows.MessageBox.Show(this, "Could not read that image.", "Import frame");
        }

        return pixels;
    }

    private void ReplaceCurrentFrame(PixelBuffer pixels)
    {
        if (_current is null)
        {
            return;
        }

        pixels = pixels.CropVisible(pad: 0, out _, out _);
        byte[] indexed;
        PixelBuffer stored;
        if (IsRgba(_current))
        {
            indexed = [];
            stored = pixels;
        }
        else
        {
            Palette pal = MonsterPalette(_current);
            indexed = pal.Quantize(pixels);
            stored = pal.Render(indexed, pixels.Width, pixels.Height);
        }

        int index = Math.Clamp((int)FrameSlider.Value, 0, Math.Max(0, _current.Frames.Count - 1));
        if (_current.Frames.Count == 0)
        {
            index = 0;
            _current.Frames.Add(new MpfFrame(0, 0, pixels.Height, pixels.Width, 0, 0, indexed));
        }
        else
        {
            MpfFrame old = _current.Frames[index];
            _current.Frames[index] = new MpfFrame(old.Top, old.Left, old.Top + pixels.Height, old.Left + pixels.Width, old.XOffset, old.YOffset, indexed);
        }

        _current.Replacements[index] = stored;
        _current.Width = Math.Max(_current.Width, pixels.Width);
        _current.Height = Math.Max(_current.Height, pixels.Height);
        _current.Dirty = true;
        ShowMonster(_current);
        FrameSlider.Value = index;
        StatusText.Text = $"Replaced frame {index + 1}.";
    }

    private void AppendFrame(PixelBuffer pixels)
    {
        if (_current is null)
        {
            return;
        }

        pixels = pixels.CropVisible(pad: 0, out _, out _);
        byte[] indexed;
        PixelBuffer stored;
        if (IsRgba(_current))
        {
            indexed = [];
            stored = pixels;
        }
        else
        {
            Palette pal = MonsterPalette(_current);
            indexed = pal.Quantize(pixels);
            stored = pal.Render(indexed, pixels.Width, pixels.Height);
        }

        int index = _current.Frames.Count;
        _current.Frames.Add(new MpfFrame(0, 0, pixels.Height, pixels.Width, 0, 0, indexed));
        _current.Replacements[index] = stored;
        _current.Width = Math.Max(_current.Width, pixels.Width);
        _current.Height = Math.Max(_current.Height, pixels.Height);
        _current.Dirty = true;
        if (_current.IdleCount <= 0)
        {
            _current.IdleCount = 1;
        }
    }

    private void OpenButtonMenu(object sender, RoutedEventArgs e) => ButtonMenu.Open(sender);

    private void ExportDataClicked(object sender, RoutedEventArgs e)
    {
        IEnumerable<MonsterDraft> monsters = CollectionViewSource.GetDefaultView(_monsters).Cast<MonsterDraft>();
        TableDump dump = TableDump.Create(
            "Monsters",
            ["Name", "FileName", "Palette", "Width", "Height", "IdleIndex", "IdleCount", "WalkIndex", "WalkCount",
                "AttackIndex", "AttackCount", "Notes"],
            monsters.Select(m => (IReadOnlyList<object?>)new object?[]
            {
                m.Name, m.FileName, m.PaletteNumber, m.Width, m.Height, m.IdleIndex, m.IdleCount, m.WalkIndex, m.WalkCount,
                m.AttackIndex, m.AttackCount, m.Notes
            }));
        TableExport.Save(this, "monsters", dump, sender, StatusText);
    }

    private void ExportPackClicked(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Folder for the monster pack (JSON + PNGs + MPF)",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        try
        {
            string stem = SafeName($"{Path.GetFileNameWithoutExtension(_current.FileName)}_{_current.Name}");
            string folder = Path.Combine(dialog.SelectedPath, stem);
            Directory.CreateDirectory(folder);
            Palette pal = MonsterPalette(_current);
            var pngs = new List<string>();
            for (int i = 0; i < _current.Frames.Count; i++)
            {
                PixelBuffer img = _current.Replacements.TryGetValue(i, out PixelBuffer? replacement)
                    ? replacement
                    : _current.Frames[i].Render(pal);
                string name = $"frame_{i:D3}.png";
                PngWriter.Write(img, Path.Combine(folder, name));
                pngs.Add(name);
            }

            File.WriteAllText(Path.Combine(folder, stem + ".json"), JsonSerializer.Serialize(new MonsterPack
            {
                Kind = "monster",
                Name = _current.Name,
                FileName = _current.FileName,
                Palette = _current.PaletteNumber,
                Width = _current.Width,
                Height = _current.Height,
                IdleIndex = _current.IdleIndex,
                IdleCount = _current.IdleCount,
                WalkIndex = _current.WalkIndex,
                WalkCount = _current.WalkCount,
                AttackIndex = _current.AttackIndex,
                AttackCount = _current.AttackCount,
                Attack2Index = _current.Attack2Index,
                Attack2Count = _current.Attack2Count,
                Attack3Index = _current.Attack3Index,
                Attack3Count = _current.Attack3Count,
                StopMotion = _current.StopMotion,
                StopMotionPct = _current.StopMotionPct,
                Notes = _current.Notes,
                Client = _workspace?.ActiveClient?.Name ?? "",
                Frames = pngs,
                MpfFile = _current.FileName
            }, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllBytes(Path.Combine(folder, _current.FileName), BuildSprite(_current));
            _current.Dirty = false;
            StatusText.Text = $"Wrote pack to {folder}";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Export pack");
        }
    }

    private void SaveMpfClicked(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "MPF monster|*.mpf|All files|*.*",
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
            System.Windows.MessageBox.Show(this, ex.Message, "Save MPF");
        }
    }

    private void SaveToDatClicked(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        EnsureLoaded(_current);
        CatalogEntry? catalog = _current.Catalog ?? _catalogs.FirstOrDefault(c => c.Role == ArchiveRole.Mobs);
        if (catalog is null || !DatSave.IsWritable(catalog))
        {
            System.Windows.MessageBox.Show(this, "This monster isn't tied to a writable DAT.", "Save to DAT");
            return;
        }

        try
        {
            DatSave.Write(_cache, catalog, _current.FileName, BuildSprite(_current));
            _current.Dirty = false;
            StatusText.Text = $"Saved {_current.FileName} into {catalog.DisplayName} (no unpack).";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Save to DAT");
        }
    }

    private static bool IsRgba(MonsterDraft monster) =>
        monster.FileName.EndsWith(".rgba", StringComparison.OrdinalIgnoreCase)
        || monster.Entry?.Extension.Equals(".rgba", StringComparison.OrdinalIgnoreCase) == true;

    private static byte[] BuildSprite(MonsterDraft monster) => IsRgba(monster) ? BuildRgba(monster) : BuildMpf(monster);

    private static byte[] BuildRgba(MonsterDraft monster)
    {
        var frames = new List<byte[]>();
        for (int i = 0; i < monster.Frames.Count; i++)
        {
            PixelBuffer pixels = monster.Replacements.TryGetValue(i, out PixelBuffer? replacement)
                ? replacement
                : new PixelBuffer(Math.Max(1, monster.Width), Math.Max(1, monster.Height));
            frames.Add(RgbaImage.Write(pixels));
        }

        return frames.Count == 0 ? RgbaImage.Write(new PixelBuffer(1, 1)) : RgbaImage.Pack(frames);
    }

    private static byte[] BuildMpf(MonsterDraft monster) =>
        MpfWriter.Write(
            monster.Width,
            monster.Height,
            monster.PaletteNumber,
            monster.Frames,
            monster.WalkIndex,
            monster.WalkCount,
            monster.IdleIndex,
            monster.IdleCount,
            monster.StopMotion,
            monster.StopMotionPct,
            monster.AttackIndex,
            monster.AttackCount,
            monster.Attack2Index,
            monster.Attack2Count,
            monster.Attack3Index,
            monster.Attack3Count);

    private static string SafeName(string value)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(c, '_');
        }

        return string.IsNullOrWhiteSpace(value) ? "monster" : value.Trim();
    }

    private sealed class MonsterDraft
    {
        public string Name { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Notes { get; set; } = "";
        public int PaletteNumber { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int IdleIndex { get; set; }
        public int IdleCount { get; set; }
        public int WalkIndex { get; set; }
        public int WalkCount { get; set; }
        public int AttackIndex { get; set; }
        public int AttackCount { get; set; }
        public int Attack2Index { get; set; }
        public int Attack2Count { get; set; }
        public int Attack3Index { get; set; }
        public int Attack3Count { get; set; }
        public int StopMotion { get; set; }
        public int StopMotionPct { get; set; }
        public CatalogEntry? Catalog { get; set; }
        public ArchiveEntry? Entry { get; set; }
        public List<MpfFrame> Frames { get; set; } = [];
        public Dictionary<int, PixelBuffer> Replacements { get; } = [];
        public bool IsNew { get; set; }
        public bool Loaded { get; set; }
        public bool Dirty { get; set; }
        public string ListLabel => $"{FileName}  {Name}{(Dirty ? " *" : "")}";

        public bool Matches(string q) =>
            Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || FileName.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class MonsterPack
    {
        public string Kind { get; set; } = "monster";
        public string Name { get; set; } = "";
        public string FileName { get; set; } = "";
        public int Palette { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int IdleIndex { get; set; }
        public int IdleCount { get; set; }
        public int WalkIndex { get; set; }
        public int WalkCount { get; set; }
        public int AttackIndex { get; set; }
        public int AttackCount { get; set; }
        public int Attack2Index { get; set; }
        public int Attack2Count { get; set; }
        public int Attack3Index { get; set; }
        public int Attack3Count { get; set; }
        public int StopMotion { get; set; }
        public int StopMotionPct { get; set; }
        public string Notes { get; set; } = "";
        public string Client { get; set; } = "";
        public List<string> Frames { get; set; } = [];
        public string MpfFile { get; set; } = "";
    }
}
