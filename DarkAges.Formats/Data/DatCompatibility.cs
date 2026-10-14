using DarkAges.Formats.Catalog;
using DarkAges.Formats.Drawing;
using DarkAges.Formats.Imaging;
using DarkAges.Formats.Maps;

namespace DarkAges.Formats.Data;

public static class DatCompatibility
{
    public static string? OverlaySaveBlockReason(ClientFamily family)
    {
        if (family == ClientFamily.Yuki)
        {
            return null;
        }

        return family switch
        {
            ClientFamily.Fragments =>
                "Fragments light maps are packed HEA atlases (dest 640×480, huge tile sheet). This editor paints a map-sized tile grid and cannot rewrite that layout, so Save overlay is blocked to avoid replacing 000500.hea and the other numbered sheets.",
            ClientFamily.Classic3 or ClientFamily.Classic7 or ClientFamily.ClassicNa =>
                $"{ClientFamilies.Label(family)} has no map HEA light sheets. Lamp posts are wall HPFs. Writing a .hea overlay into seo.dat would not be loaded by the stock exe, so Save overlay is blocked.",
            ClientFamily.LegendOfDarkness =>
                "Legend of Darkness light maps are not a known drop-in of this editor's tile-grid HEA, so Save overlay is blocked.",
            ClientFamily.AoC =>
                "Legends AoC packs are read-only.",
            _ =>
                "Save overlay is blocked unless the client is Yuki (JP). 3.x, 7.x, and Fragments cannot use this editor's HEA overlay bytes as a drop-in."
        };
    }

    public static void ThrowIfUnsafe(
        ClientFamily family,
        int? nameLength,
        Func<string, byte[]?> existing,
        IReadOnlyList<(string Name, byte[] Data)> files)
    {
        string? reason = BlockReason(family, nameLength, existing, files);
        if (!string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidOperationException(reason);
        }
    }

    public static string? BlockReason(
        ClientFamily family,
        int? nameLength,
        Func<string, byte[]?> existing,
        IReadOnlyList<(string Name, byte[] Data)> files)
    {
        if (!ClientFamilies.IsGuarded(family))
        {
            return null;
        }

        foreach ((string name, byte[] data) in files)
        {
            string fileName = Path.GetFileName(name).Trim();
            if (nameLength is int width)
            {
                string encoded = DatPacker.DecodeName(DatPacker.EncodeName(fileName, width));
                if (!encoded.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return
                        $"Refusing '{fileName}': this DAT only stores {width - 1} characters, so it would be written as '{encoded}'. That can collide with another entry and break the pack.";
                }
            }

            byte[]? prior = existing(fileName);
            string? smash = EntrySmashReason(fileName, prior, data);
            if (!string.IsNullOrWhiteSpace(smash))
            {
                return smash;
            }
        }

        return null;
    }

    public static string? EntrySmashReason(string fileName, byte[]? existing, byte[] incoming)
    {
        string ext = Path.GetExtension(fileName);
        if (ext.Equals(".hea", StringComparison.OrdinalIgnoreCase) && existing is { Length: > 0 })
        {
            HeaLayout oldLayout = HeaLights.ClassifyLayout(existing);
            HeaLayout newLayout = HeaLights.ClassifyLayout(incoming);
            if (HeaLights.OverwriteWouldSmash(oldLayout, newLayout))
            {
                return
                    $"Refusing to overwrite {fileName}: it is a Fragments packed HEA ({oldLayout}), and the new bytes are {newLayout}. That would replace a client-readable sheet with a layout the exe will not parse.";
            }
        }

        if (ext.Equals(".map", StringComparison.OrdinalIgnoreCase) && existing is { Length: > 0 })
        {
            MapFile oldMap = MapFile.FromBytes(existing);
            MapFile newMap = MapFile.FromBytes(incoming);
            if (oldMap.SizeHeader == MapSizeHeader.None && newMap.SizeHeader != MapSizeHeader.None)
            {
                return
                    $"Refusing to save {fileName} with a {newMap.SizeHeader} size header. This DAT stores headerless 6-byte tiles; a header would make the client misread the map.";
            }
        }

        if (ext.Equals(".bik", StringComparison.OrdinalIgnoreCase)
            && BinkFile.TryParse(incoming, out BinkInfo info)
            && info.IsBink2)
        {
            return
                $"Refusing {fileName}: Dark Ages plays Bink 1, and this file is Bink 2. Replacing a cinematic with KB2 would leave an unplayable slot.";
        }

        return null;
    }
}
