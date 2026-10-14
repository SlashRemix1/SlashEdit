using DarkAges.Formats.Data;

namespace DarkAges.Formats.Catalog;

public readonly record struct KhanPieceInfo(char Gender, string Slot, int Id, string Motion);

public static class KhanPieces
{
    /// <summary>DressMeUp default facing: CharacterForm._direction = 5.</summary>
    public const int IdleDirection = 5;

    public static bool IsKhanArchive(IArchive archive)
    {
        if (Path.GetFileName(archive.SourcePath).StartsWith("khan", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        int hits = 0;
        foreach (ArchiveEntry entry in archive.Entries.Take(80))
        {
            if (TryParse(entry.Name, out _))
            {
                hits++;
                if (hits >= 8)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool TryParse(string fileName, out KhanPieceInfo info)
    {
        info = default;
        if (!fileName.EndsWith(".epf", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length < 5)
        {
            return false;
        }

        char gender = char.ToLowerInvariant(stem[0]);
        if (gender is not 'm' and not 'w')
        {
            return false;
        }

        int i = 1;
        while (i < stem.Length && char.IsLetter(stem[i]))
        {
            i++;
        }

        if (i == 1)
        {
            return false;
        }

        string slot = stem[1..i].ToLowerInvariant();
        int digitStart = i;
        while (i < stem.Length && char.IsDigit(stem[i]))
        {
            i++;
        }

        if (i - digitStart < 3)
        {
            return false;
        }

        if (!int.TryParse(stem.AsSpan(digitStart, 3), out int id))
        {
            return false;
        }

        string motion = i < stem.Length ? stem[i..] : "01";
        if (string.IsNullOrEmpty(motion))
        {
            motion = "01";
        }

        info = new KhanPieceInfo(gender, slot, id, motion);
        return true;
    }

    public static bool IsIdleFile(KhanPieceInfo info) =>
        info.Motion.Equals("01", StringComparison.OrdinalIgnoreCase);

    public static bool IsEmptySlot(KhanPieceInfo info) => info.Id == 0;

    /// <summary>Slots that replace the whole figure (not gear layered on the nude body).</summary>
    public static bool IsFigureSlot(KhanPieceInfo info) =>
        info.Slot is "b" or "m" or "n" or "p";

    public static string BodyName(KhanPieceInfo info, string bodySlot = "m") =>
        $"{info.Gender}{bodySlot}001{info.Motion}.epf";

    public static string PalettePrefix(KhanPieceInfo info)
    {
        if (IsFigureSlot(info))
        {
            return info.Slot == "b" ? "palb" : "palm";
        }

        return info.Slot switch
        {
            "h" => "palh",
            "c" or "g" => "palc",
            "e" => "pale",
            "f" => "palf",
            "i" or "j" => "pali",
            "l" => "pall",
            "u" or "a" => "palu",
            "w" => "palw",
            "p" => "palp",
            "s" => "palc",
            "o" => "palm",
            _ => "palc"
        };
    }

    /// <summary>
    /// DressMeUp BrowserForm slot letters: b body, m body+, o face, c accessory,
    /// w weapon, u overcoat, i armor, h head, l boots, s shield.
    /// PaintPicture draws body/head/armor/overcoat/boots/shield at x=22 and
    /// weapons/accessories at x=-5 (−27 vs the body).
    /// </summary>
    public static int LayerOffsetX(KhanPieceInfo info) =>
        info.Slot is "w" or "p" or "c" or "g" ? -27 : 0;

    /// <summary>
    /// DressMeUp loads a second EPF with the slot letter swapped:
    /// accessory c→g (behind), overcoat u→a (arms), armor i→j, weapon w→p.
    /// </summary>
    public static string? PairSlot(KhanPieceInfo info) => info.Slot switch
    {
        "c" => "g",
        "u" => "a",
        "i" => "j",
        "w" => "p",
        _ => null
    };

    public static bool PairDrawsBehind(string pairSlot) => pairSlot is "g";

    public static bool IsAccessory(KhanPieceInfo info) => info.Slot is "c" or "g";

    public static bool DrawsBehind(KhanPieceInfo info) => info.Slot is "g";

    public static bool IsPairOnlySlot(KhanPieceInfo info) => info.Slot is "g" or "a" or "j" or "p";

    public static string PairFileName(KhanPieceInfo info, string pairSlot) =>
        $"{info.Gender}{pairSlot}{info.Id:D3}{info.Motion}.epf";

    public static int FrameIndex(int frameCount, int requested)
    {
        if (frameCount <= 0)
        {
            return 0;
        }

        return requested < frameCount ? requested : 0;
    }

    public static ArchiveEntry? BodyFor(IArchive archive, KhanPieceInfo info)
    {
        if (IsFigureSlot(info))
        {
            return null;
        }

        foreach (string slot in new[] { "m", "b" })
        {
            ArchiveEntry? match = archive.GetEntry(BodyName(info, slot))
                ?? archive.GetEntry($"{info.Gender}{slot}00101.epf");
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    public static IReadOnlyList<ArchiveEntry> CollapseForGallery(IEnumerable<ArchiveEntry> entries)
    {
        var parsed = new List<(ArchiveEntry Entry, KhanPieceInfo Info)>();
        var present = new HashSet<(char Gender, string Slot, int Id)>();
        foreach (ArchiveEntry entry in entries)
        {
            if (!TryParse(entry.Name, out KhanPieceInfo info))
            {
                continue;
            }

            parsed.Add((entry, info));
            present.Add((info.Gender, info.Slot, info.Id));
        }

        var unique = new Dictionary<(char Gender, string Slot, int Id), ArchiveEntry>();
        foreach ((ArchiveEntry entry, KhanPieceInfo info) in parsed)
        {
            if (!IsIdleFile(info) || IsEmptySlot(info))
            {
                continue;
            }

            if (IsPairOnlySlot(info) && HasPrimarySlot(present, info))
            {
                continue;
            }

            unique.TryAdd((info.Gender, info.Slot, info.Id), entry);
        }

        return unique.Values
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool HasPrimarySlot(HashSet<(char Gender, string Slot, int Id)> present, KhanPieceInfo info)
    {
        string? primary = info.Slot switch
        {
            "g" => "c",
            "a" => "u",
            "j" => "i",
            "p" => "w",
            _ => null
        };
        return primary is not null && present.Contains((info.Gender, primary, info.Id));
    }
}
