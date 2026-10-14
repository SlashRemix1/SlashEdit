using System.IO;
using System.Windows;
using System.Windows.Controls;
using DarkAges.Formats.Catalog;
using DarkAges.Formats.Data;
using DarkAges.Formats.Drawing;
using DarkAges.Formats.Imaging;
using DarkAges.Formats.IO;
using DarkAges.Viewer.Imaging;
using DarkAges.Viewer.Services;
using Microsoft.Win32;

namespace DarkAges.Viewer;

public partial class CharacterAssemblerWindow : Window
{
    private static readonly string[] AislingFacings = ["N", "E", "S", "W"];

    private readonly ArchiveCache? _cache;
    private IArchive? _archive;
    private CatalogEntry? _catalog;
    private readonly bool _aisling;
    private bool _syncing = true;
    private bool _ignorePaint;
    private PixelBuffer? _aislingPaint;
    private string? _paintFileName;
    private EpfFile? _paintEpf;
    private Palette? _paintPalette;
    private int _paintFrame;

    public CharacterAssemblerWindow(ArchiveCache? cache, IReadOnlyList<CatalogEntry> catalogs)
    {
        InitializeComponent();
        _cache = cache;
        GenderBox.ItemsSource = new[] { "Male", "Female" };
        GenderBox.SelectedIndex = 0;
        MotionBox.ItemsSource = new[] { "01 (idle)" }
            .Concat(KhanMotions.Presets.Select(m => $"{KhanMotions.FileSuffix(m)} (motion {m.Number})"))
            .ToList();
        MotionBox.SelectedIndex = 0;
        Paint.PixelsChanged += PaintPixelsChanged;
        IArchive? archive = FindKhan(cache, catalogs);
        if (archive is null)
        {
            archive = FindAisling(cache, catalogs);
            _aisling = archive is not null;
        }

        _archive = archive;

        if (_archive is null)
        {
            StatusText.Text = "No khan DAT or AoC aisling_*.rgba sprites in this client.";
            _syncing = false;
            return;
        }

        if (_aisling)
        {
            GenderLabel.Visibility = Visibility.Collapsed;
            GenderBox.Visibility = Visibility.Collapsed;
            MotionLabel.Visibility = Visibility.Collapsed;
            MotionBox.Visibility = Visibility.Collapsed;
            KhanGearPanel.Visibility = Visibility.Collapsed;
            BodyLabel.Text = "Aisling";
            DirSlider.Maximum = 3;
            DirSlider.Value = 0;
            DirLabel.Text = "N";
            PaintSlotBox.ItemsSource = new[] { "Aisling" };
            PaintSlotBox.SelectedIndex = 0;
        }
        else
        {
            PaintSlotBox.ItemsSource = new[]
            {
                "Paper-doll (preview)", "Body", "Face", "Head", "Armor", "Overcoat", "Weapon", "Shield", "Boots", "Accessory"
            };
            PaintSlotBox.SelectedIndex = 1;
        }

        FillSlots();
        _syncing = false;
        RenderDoll();
    }

    private IArchive? FindKhan(ArchiveCache? cache, IReadOnlyList<CatalogEntry> catalogs)
    {
        if (cache is null)
        {
            return null;
        }

        foreach (CatalogEntry catalog in catalogs.Where(c => c.Role == ArchiveRole.PlayerSprites))
        {
            try
            {
                IArchive archive = cache.Get(catalog);
                if (KhanPieces.IsKhanArchive(archive) || archive.Entries.Any(e => KhanPieces.TryParse(e.Name, out _)))
                {
                    _catalog = catalog;
                    return archive;
                }
            }
            catch
            {
                // try next
            }
        }

        return null;
    }

    private IArchive? FindAisling(ArchiveCache? cache, IReadOnlyList<CatalogEntry> catalogs)
    {
        if (cache is null)
        {
            return null;
        }

        foreach (CatalogEntry catalog in catalogs.Where(c => c.Role is ArchiveRole.PlayerSprites or ArchiveRole.Misc or ArchiveRole.Unknown))
        {
            try
            {
                IArchive archive = cache.Get(catalog);
                if (archive.Entries.Any(IsAisling))
                {
                    _catalog = catalog;
                    return archive;
                }
            }
            catch
            {
                // try next
            }
        }

        return null;
    }

    private static bool IsAisling(ArchiveEntry entry) =>
        entry.Extension.Equals(".rgba", StringComparison.OrdinalIgnoreCase)
        && Path.GetFileNameWithoutExtension(entry.Name).StartsWith("aisling_", StringComparison.OrdinalIgnoreCase);

    private void FillSlots()
    {
        if (_aisling)
        {
            FillAisling();
            return;
        }

        char gender = GenderBox.SelectedIndex == 1 ? 'w' : 'm';
        Fill(BodyBox, gender, "m", 1);
        Fill(FaceBox, gender, "o", 0);
        Fill(HeadBox, gender, "h", 0);
        Fill(ArmorBox, gender, "i", 0);
        Fill(CoatBox, gender, "u", 0);
        Fill(WeaponBox, gender, "w", 0);
        Fill(ShieldBox, gender, "s", 0);
        Fill(BootsBox, gender, "l", 0);
        Fill(AccessoryBox, gender, "c", 0);
    }

    private void FillAisling()
    {
        var ids = new SortedSet<int>();
        if (_archive is not null)
        {
            foreach (ArchiveEntry entry in _archive.Entries.Where(IsAisling))
            {
                int id = NumericId(entry.Name);
                if (id > 0)
                {
                    ids.Add(id);
                }
            }
        }

        BodyBox.ItemsSource = ids.Select(id => id.ToString("D5")).ToList();
        if (BodyBox.Items.Count > 0)
        {
            BodyBox.SelectedIndex = 0;
        }
    }

    private void Fill(System.Windows.Controls.ComboBox box, char gender, string slot, int fallback)
    {
        var ids = new List<int> { 0 };
        if (_archive is not null)
        {
            ids.AddRange(CharacterAssembler.Ids(_archive, gender, slot));
            if (slot == "m" && ids.Count <= 1)
            {
                ids.AddRange(CharacterAssembler.Ids(_archive, gender, "b"));
            }
        }

        if (fallback > 0 && !ids.Contains(fallback))
        {
            ids.Insert(1, fallback);
        }

        box.ItemsSource = ids.Select(id => id == 0 ? "(none)" : id.ToString("D3")).ToList();
        box.SelectedIndex = fallback > 0 && ids.Contains(fallback) ? ids.IndexOf(fallback) : 0;
    }

    private void OutfitChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        if (sender == GenderBox && !_aisling)
        {
            _syncing = true;
            FillSlots();
            _syncing = false;
        }

        DirLabel.Text = _aisling ? Facing() : ((int)DirSlider.Value).ToString();
        if (sender == PaintSlotBox)
        {
            _aislingPaint = null;
        }

        RenderDoll();
    }

    private void RenderDoll()
    {
        if (_archive is null)
        {
            return;
        }

        try
        {
            if (_aisling)
            {
                PixelBuffer? pixels = _aislingPaint ?? RenderAisling();
                PreviewImage.Source = pixels is null ? null : WpfImages.ToBitmap(pixels);
                LoadPaint(pixels, null);
                StatusText.Text = pixels is null
                    ? "No aisling sprite for that id / facing."
                    : $"AoC aisling {BodyBox.SelectedItem}  {Facing()}  {pixels.Width}×{pixels.Height}. Paint here; LRAR is read-only so export PNG to keep it.";
                return;
            }

            var outfit = CurrentOutfit();
            PixelBuffer doll = CharacterAssembler.Render(_archive, outfit);
            PreviewImage.Source = WpfImages.ToBitmap(doll);
            StatusText.Text = $"{outfit.Gender}  motion {outfit.Motion}  dir {outfit.Direction}  {doll.Width}×{doll.Height}";
            LoadSelectedSlot(doll);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void LoadPaint(PixelBuffer? pixels, Palette? palette)
    {
        _ignorePaint = true;
        if (pixels is null)
        {
            Paint.Clear();
        }
        else
        {
            Paint.Load(pixels, palette);
        }

        _ignorePaint = false;
    }

    private void LoadSelectedSlot(PixelBuffer doll)
    {
        string? slot = PaintSlotCode();
        if (slot is null)
        {
            _paintFileName = null;
            _paintEpf = null;
            LoadPaint(doll, null);
            return;
        }

        char gender = GenderBox.SelectedIndex == 1 ? 'w' : 'm';
        int id = SlotId(slot);
        if (id <= 0 || _archive is null)
        {
            _paintFileName = null;
            _paintEpf = null;
            LoadPaint(doll, null);
            StatusText.Text = $"{PaintSlotBox.SelectedItem} is (none). Showing the paper-doll. Pick an id for that slot to paint its EPF.";
            return;
        }

        string motion = MotionSuffix();
        ArchiveEntry? entry = FindSlotEntry(gender, slot, id, motion);
        if (entry is null)
        {
            _paintFileName = null;
            _paintEpf = null;
            LoadPaint(doll, null);
            StatusText.Text = $"No {gender}{slot}{id:D3} EPF in this client. Showing the paper-doll.";
            return;
        }

        var epf = new EpfFile(entry);
        int index = KhanPieces.FrameIndex(epf.Frames.Count, (int)DirSlider.Value);
        KhanPieces.TryParse(entry.Name, out KhanPieceInfo info);
        Palette pal = PaletteLocator.Named(_archive, $"{KhanPieces.PalettePrefix(info)}{id:D3}.pal")
            ?? PaletteLocator.ForEntry(_archive, entry)
            ?? PaletteLocator.Find(_archive)
            ?? new Palette();
        EpfFrame frame = epf.Frames[index];
        _paintFileName = entry.Name;
        _paintEpf = epf;
        _paintPalette = pal;
        _paintFrame = index;
        LoadPaint(frame.Render(pal), pal);
    }

    private string? PaintSlotCode()
    {
        if (_aisling || PaintSlotBox.SelectedItem is not string text)
        {
            return _aisling ? "aisling" : null;
        }

        return text switch
        {
            "Body" => "m",
            "Face" => "o",
            "Head" => "h",
            "Armor" => "i",
            "Overcoat" => "u",
            "Weapon" => "w",
            "Shield" => "s",
            "Boots" => "l",
            "Accessory" => "c",
            _ => null
        };
    }

    private ArchiveEntry? FindSlotEntry(char gender, string slot, int id, string motion)
    {
        if (_archive is null)
        {
            return null;
        }

        foreach (string letter in SlotLetters(slot))
        {
            ArchiveEntry? entry = _archive.GetEntry($"{gender}{letter}{id:D3}{motion}.epf")
                ?? _archive.GetEntry($"{gender}{letter}{id:D3}01.epf");
            if (entry is not null)
            {
                return entry;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> SlotLetters(string slot) => slot switch
    {
        "m" => ["m", "b"],
        "i" => ["i", "j"],
        "u" => ["u", "a"],
        "w" => ["w", "p"],
        "c" => ["c", "g"],
        _ => [slot]
    };

    private int SlotId(string slot) => slot switch
    {
        "m" => IdOf(BodyBox, 1),
        "o" => IdOf(FaceBox),
        "h" => IdOf(HeadBox),
        "i" => IdOf(ArmorBox),
        "u" => IdOf(CoatBox),
        "w" => IdOf(WeaponBox),
        "s" => IdOf(ShieldBox),
        "l" => IdOf(BootsBox),
        "c" => IdOf(AccessoryBox),
        _ => 0
    };

    private void PaintPixelsChanged(object? sender, EventArgs e)
    {
        if (_ignorePaint || Paint.Pixels is null)
        {
            return;
        }

        if (_aisling)
        {
            _aislingPaint = Paint.Pixels.Clone();
            PreviewImage.Source = WpfImages.ToBitmap(_aislingPaint);
            return;
        }

        if (PaintSlotCode() is null || _paintEpf is null)
        {
            return;
        }

        StatusText.Text = $"Painted {_paintFileName ?? "slot"}. Save painted slot to DAT when ready.";
    }

    private void SaveSlotClicked(object sender, RoutedEventArgs e)
    {
        if (Paint.Pixels is null)
        {
            return;
        }

        if (_aisling)
        {
            System.Windows.MessageBox.Show(this, "Legends AoC LRAR packs are read-only. Export PNG instead.", "Save slot");
            return;
        }

        if (_paintEpf is null || _paintFileName is null || _catalog is null || _cache is null)
        {
            System.Windows.MessageBox.Show(this, "Pick Armor (or another slot) with an id, then paint before saving.", "Save slot");
            return;
        }

        try
        {
            PixelBuffer pixels = Paint.Pixels.Clone();
            Palette pal = _paintPalette ?? new Palette();
            byte[] indexed = pal.Quantize(pixels);
            var frames = _paintEpf.Frames.ToList();
            EpfFrame old = frames[_paintFrame];
            frames[_paintFrame] = new EpfFrame(old.Top, old.Left, old.Top + pixels.Height, old.Left + pixels.Width, indexed);
            int width = Math.Max(_paintEpf.Width, pixels.Width + old.Left);
            int height = Math.Max(_paintEpf.Height, pixels.Height + old.Top);
            DatSave.Write(_cache, _catalog, _paintFileName, EpfWriter.Write(width, height, frames));
            _archive = _cache.Get(_catalog);
            RenderDoll();
            StatusText.Text = $"Saved {_paintFileName} into {_catalog.DisplayName}.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Save slot");
        }
    }

    private PixelBuffer? RenderAisling()
    {
        if (_archive is null || BodyBox.SelectedItem is not string idText)
        {
            return null;
        }

        string facing = Facing();
        ArchiveEntry? entry = _archive.GetEntry($"aisling_{idText}_{facing}.rgba")
            ?? _archive.GetEntry($"aisling_{idText}.rgba")
            ?? _archive.Entries.FirstOrDefault(e =>
                e.Name.StartsWith($"aisling_{idText}", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }

        IReadOnlyList<RgbaImage> frames = RgbaImage.DecodeAll(entry.ReadAllBytes(), 1);
        return frames.Count == 0 ? null : frames[0].Pixels;
    }

    private string Facing()
    {
        int i = Math.Clamp((int)DirSlider.Value, 0, AislingFacings.Length - 1);
        return AislingFacings[i];
    }

    private CharacterOutfit CurrentOutfit() =>
        new()
        {
            Gender = GenderBox.SelectedIndex == 1 ? 'w' : 'm',
            Motion = MotionSuffix(),
            Direction = (int)DirSlider.Value,
            BodyId = IdOf(BodyBox, 1),
            FaceId = IdOf(FaceBox),
            HeadId = IdOf(HeadBox),
            ArmorId = IdOf(ArmorBox),
            CoatId = IdOf(CoatBox),
            WeaponId = IdOf(WeaponBox),
            ShieldId = IdOf(ShieldBox),
            BootsId = IdOf(BootsBox),
            AccessoryId = IdOf(AccessoryBox)
        };

    private string MotionSuffix()
    {
        if (MotionBox.SelectedItem is not string text)
        {
            return "01";
        }

        int space = text.IndexOf(' ');
        string token = space > 0 ? text[..space] : text;
        return string.IsNullOrWhiteSpace(token) ? "01" : token;
    }

    private static int IdOf(System.Windows.Controls.ComboBox box, int fallback = 0)
    {
        if (box.SelectedItem is not string text || text.StartsWith('('))
        {
            return fallback;
        }

        return int.TryParse(text, out int id) ? id : fallback;
    }

    private static int NumericId(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        var digits = new string(stem.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out int id) ? id : 0;
    }

    private void OpenButtonMenu(object sender, RoutedEventArgs e) => ButtonMenu.Open(sender);

    private void ExportDataClicked(object sender, RoutedEventArgs e)
    {
        CharacterOutfit outfit = CurrentOutfit();
        TableDump dump = TableDump.Create(
            "Outfit",
            ["Gender", "Motion", "Direction", "BodyId", "FaceId", "HeadId", "ArmorId", "CoatId", "WeaponId", "ShieldId", "BootsId", "AccessoryId"],
            [[
                outfit.Gender.ToString(), outfit.Motion, outfit.Direction, outfit.BodyId, outfit.FaceId, outfit.HeadId,
                outfit.ArmorId, outfit.CoatId, outfit.WeaponId, outfit.ShieldId, outfit.BootsId, outfit.AccessoryId
            ]]);
        TableExport.Save(this, "outfit", dump, sender, StatusText);
    }

    private void ExportClicked(object sender, RoutedEventArgs e)
    {
        if (PreviewImage.Source is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG image|*.png",
            FileName = "character.png"
        };
        if (dialog.ShowDialog(this) != true || _archive is null)
        {
            return;
        }

        try
        {
            PixelBuffer? pixels = _aisling
                ? (_aislingPaint ?? RenderAisling())
                : CharacterAssembler.Render(_archive, CurrentOutfit());
            if (pixels is null)
            {
                return;
            }

            DarkAges.Formats.Imaging.PngWriter.Write(pixels, dialog.FileName);
            StatusText.Text = $"Exported {dialog.FileName}";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Export PNG");
        }
    }
}
