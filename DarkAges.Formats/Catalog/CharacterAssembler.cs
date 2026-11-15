using DarkAges.Formats.Data;
using DarkAges.Formats.Drawing;
using DarkAges.Formats.Imaging;

namespace DarkAges.Formats.Catalog;

public sealed class CharacterOutfit
{
    public char Gender { get; set; } = 'm';
    public string Motion { get; set; } = "01";
    public int Direction { get; set; } = KhanPieces.IdleDirection;
    public int BodyId { get; set; } = 1;
    public int FaceId { get; set; }
    public int HeadId { get; set; }
    public int ArmorId { get; set; }
    public int CoatId { get; set; }
    public int WeaponId { get; set; }
    public int ShieldId { get; set; }
    public int BootsId { get; set; }
    public int AccessoryId { get; set; }
}

public readonly record struct EquippedPiece(string Slot, int Id, EpfFile File, Palette Palette, int OffsetX);

public static class CharacterAssembler
{
    public static readonly (string Slot, string Label)[] Slots =
    [
        ("m", "Body"),
        ("o", "Face"),
        ("h", "Head"),
        ("i", "Armor"),
        ("u", "Overcoat"),
        ("w", "Weapon"),
        ("s", "Shield"),
        ("l", "Boots"),
        ("c", "Accessory")
    ];

    public static IReadOnlyList<int> Ids(IArchive archive, char gender, string slot)
    {
        var ids = new SortedSet<int>();
        foreach (ArchiveEntry entry in archive.Entries)
        {
            if (!KhanPieces.TryParse(entry.Name, out KhanPieceInfo info))
            {
                continue;
            }

            if (info.Gender == gender && info.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase) && !KhanPieces.IsEmptySlot(info))
            {
                ids.Add(info.Id);
            }
        }

        return ids.ToList();
    }

    public static PixelBuffer Render(IArchive archive, CharacterOutfit outfit)
    {
        char gender = char.ToLowerInvariant(outfit.Gender);
        if (gender is not 'm' and not 'w')
        {
            gender = 'm';
        }

        string motion = string.IsNullOrWhiteSpace(outfit.Motion) ? "01" : outfit.Motion;
        var layers = new List<EquippedPiece>();
        Add(layers, archive, gender, "g", outfit.AccessoryId, motion);
        Add(layers, archive, gender, "m", Math.Max(1, outfit.BodyId), motion);
        if (layers.TrueForAll(p => p.Slot != "m"))
        {
            Add(layers, archive, gender, "b", Math.Max(1, outfit.BodyId), motion);
        }

        Add(layers, archive, gender, "o", outfit.FaceId, motion);
        Add(layers, archive, gender, "j", outfit.ArmorId, motion);
        Add(layers, archive, gender, "i", outfit.ArmorId, motion);
        Add(layers, archive, gender, "h", outfit.HeadId, motion);
        Add(layers, archive, gender, "l", outfit.BootsId, motion);
        Add(layers, archive, gender, "a", outfit.CoatId, motion);
        Add(layers, archive, gender, "u", outfit.CoatId, motion);
        Add(layers, archive, gender, "s", outfit.ShieldId, motion);
        Add(layers, archive, gender, "p", outfit.WeaponId, motion);
        Add(layers, archive, gender, "w", outfit.WeaponId, motion);
        Add(layers, archive, gender, "c", outfit.AccessoryId, motion);

        int originX = 0;
        int originY = 0;
        int width = 1;
        int height = 1;
        var frames = new List<(EquippedPiece Piece, EpfFrame Frame)>();
        foreach (EquippedPiece piece in layers)
        {
            if (piece.File.Frames.Count == 0)
            {
                continue;
            }

            int index = KhanPieces.FrameIndex(piece.File.Frames.Count, outfit.Direction);
            EpfFrame frame = piece.File.Frames[index];
            int x = frame.Left + piece.OffsetX;
            int y = frame.Top;
            originX = Math.Min(originX, x);
            originY = Math.Min(originY, y);
            width = Math.Max(width, x + frame.Width);
            height = Math.Max(height, y + frame.Height);
            frames.Add((piece, frame));
        }

        originX = Math.Max(0, -originX);
        originY = Math.Max(0, -originY);
        var canvas = new PixelBuffer(Math.Max(1, width + originX), Math.Max(1, height + originY));
        foreach ((EquippedPiece piece, EpfFrame frame) in frames)
        {
            canvas.Blit(
                frame.Render(piece.Palette),
                frame.Left + piece.OffsetX + originX,
                frame.Top + originY);
        }

        return canvas;
    }

    private static void Add(
        List<EquippedPiece> layers,
        IArchive archive,
        char gender,
        string slot,
        int id,
        string motion)
    {
        if (id <= 0)
        {
            return;
        }

        ArchiveEntry? entry = archive.GetEntry($"{gender}{slot}{id:D3}{motion}.epf")
            ?? archive.GetEntry($"{gender}{slot}{id:D3}01.epf");
        if (entry is null)
        {
            return;
        }

        EpfFile file;
        try
        {
            file = new EpfFile(entry);
        }
        catch
        {
            return;
        }

        KhanPieces.TryParse(entry.Name, out KhanPieceInfo info);
        Palette pal = PaletteLocator.Named(archive, PaletteFile(info, id))
            ?? PaletteLocator.ForEntry(archive, entry)
            ?? PaletteLocator.Find(archive)
            ?? new Palette();
        layers.Add(new EquippedPiece(slot, id, file, pal, KhanPieces.LayerOffsetX(info)));
    }

    private static string PaletteFile(KhanPieceInfo info, int id)
    {
        string prefix = KhanPieces.PalettePrefix(info);
        return $"{prefix}{id:D3}.pal";
    }

    /// <summary>
    /// Idle paper-dolls for reverse-image search: nude body + each idle gear piece
    /// (and bare bodies). Finds full costumes / weapons as they appear on a character,
    /// not as isolated layer EPFs.
    /// </summary>
    /// <param name="allFacings">
    /// When true, render each outfit at directions 0–7 (needed because screenshots
    /// rarely match the default DressMeUp facing).
    /// </param>
    public static IEnumerable<CompositeCandidate> IdleComposites(IArchive archive, bool allFacings = true)
    {
        IReadOnlyList<ArchiveEntry> idle = KhanPieces.CollapseForGallery(archive.Entries);
        int[] directions = allFacings
            ? [0, 1, 2, 3, 4, 5, 6, 7]
            : [KhanPieces.IdleDirection];

        foreach (ArchiveEntry entry in idle)
        {
            if (!KhanPieces.TryParse(entry.Name, out KhanPieceInfo info)
                || KhanPieces.IsPairOnlySlot(info)
                || KhanPieces.IsEmptySlot(info))
            {
                continue;
            }

            CharacterOutfit? baseOutfit = OutfitFocusing(info);
            if (baseOutfit is null)
            {
                continue;
            }

            string slot = SlotLabel(info.Slot);
            foreach (int dir in directions)
            {
                var outfit = CloneOutfit(baseOutfit, dir);
                PixelBuffer pixels;
                try
                {
                    pixels = Render(archive, outfit);
                }
                catch
                {
                    continue;
                }

                if (!pixels.HasVisiblePixels())
                {
                    continue;
                }

                yield return new CompositeCandidate(
                    entry,
                    pixels,
                    $"{info.Gender} {slot} #{info.Id:D3} · dir {dir}",
                    outfit);
            }
        }
    }

    private static CharacterOutfit CloneOutfit(CharacterOutfit src, int direction) => new()
    {
        Gender = src.Gender,
        Motion = src.Motion,
        Direction = direction,
        BodyId = src.BodyId,
        FaceId = src.FaceId,
        HeadId = src.HeadId,
        ArmorId = src.ArmorId,
        CoatId = src.CoatId,
        WeaponId = src.WeaponId,
        ShieldId = src.ShieldId,
        BootsId = src.BootsId,
        AccessoryId = src.AccessoryId
    };

    private static CharacterOutfit? OutfitFocusing(KhanPieceInfo info)
    {
        var outfit = new CharacterOutfit
        {
            Gender = info.Gender,
            Motion = "01",
            Direction = KhanPieces.IdleDirection,
            BodyId = 1
        };

        switch (info.Slot.ToLowerInvariant())
        {
            case "m":
            case "b":
            case "n":
                outfit.BodyId = info.Id;
                break;
            case "o":
                outfit.FaceId = info.Id;
                break;
            case "h":
                outfit.HeadId = info.Id;
                break;
            case "i":
            case "j":
                outfit.ArmorId = info.Id;
                break;
            case "u":
            case "a":
                outfit.CoatId = info.Id;
                break;
            case "w":
            case "p":
                outfit.WeaponId = info.Id;
                break;
            case "s":
                outfit.ShieldId = info.Id;
                break;
            case "l":
                outfit.BootsId = info.Id;
                break;
            case "c":
            case "g":
                outfit.AccessoryId = info.Id;
                break;
            default:
                return null;
        }

        return outfit;
    }

    private static string SlotLabel(string slot) => slot.ToLowerInvariant() switch
    {
        "m" or "b" or "n" => "body",
        "o" => "face",
        "h" => "head",
        "i" or "j" => "armor",
        "u" or "a" => "coat",
        "w" or "p" => "weapon",
        "s" => "shield",
        "l" => "boots",
        "c" or "g" => "accessory",
        _ => slot
    };
}

public readonly record struct CompositeCandidate(
    ArchiveEntry FocusEntry,
    PixelBuffer Pixels,
    string Label,
    CharacterOutfit Outfit);
