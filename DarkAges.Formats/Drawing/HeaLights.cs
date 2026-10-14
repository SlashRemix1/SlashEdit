using DarkAges.Formats.Data;
using DarkAges.Formats.Imaging;
using DarkAges.Formats.Maps;

namespace DarkAges.Formats.Drawing;

public enum OverlayKind
{
    Light,
    Effect
}

public enum HeaLayout
{
    Unknown,
    Rgb565,
    Indexed,
    PackedArtwork,
    PackedMask,
    TileMask
}

public enum OverlayBlendMode
{
    Shade,
    Additive
}

/// <summary>
/// Map-tied HEA light sheets. Fragments of Temuair 640×480 artwork in seo.dat is not a light map.
/// Overlay is offered when a .hea name matches a map. Packed atlas masks are resampled to the tile grid.
/// </summary>
public static class HeaLights
{
    public static bool LooksLikeArtwork(HeaFile hea) =>
        !hea.IsMask && hea.Width == 640 && hea.Height == 480;

    public static HeaLayout ClassifyLayout(HeaFile hea)
    {
        if (!hea.IsValid)
        {
            return HeaLayout.Unknown;
        }

        int destW = Math.Max(1, hea.DestWidth > 0 ? hea.DestWidth : hea.Width);
        int destH = Math.Max(1, hea.DestHeight > 0 ? hea.DestHeight : hea.Height);
        int atlasW = Math.Max(1, hea.AtlasWidth > 0 ? hea.AtlasWidth : hea.Width);
        int atlasH = Math.Max(1, hea.AtlasHeight > 0 ? hea.AtlasHeight : hea.Height);
        long destPixels = (long)destW * destH;
        long atlasPixels = (long)atlasW * atlasH;
        int payload = hea.Payload.Length;
        bool rgb = payload == destPixels * 2 || payload == atlasPixels * 2;
        bool indexed = payload == destPixels || payload == atlasPixels;
        if (hea.IsMask)
        {
            return atlasW > destW + 16 || atlasH > destH + 16
                ? HeaLayout.PackedMask
                : HeaLayout.TileMask;
        }

        if (rgb)
        {
            return HeaLayout.Rgb565;
        }

        if (indexed)
        {
            return HeaLayout.Indexed;
        }

        return LooksLikeArtwork(hea) ? HeaLayout.PackedArtwork : HeaLayout.Unknown;
    }

    public static HeaLayout ClassifyLayout(byte[] data)
    {
        try
        {
            return ClassifyLayout(new HeaFile(new MemoryStream(data)));
        }
        catch
        {
            return HeaLayout.Unknown;
        }
    }

    public static bool OverwriteWouldSmash(HeaLayout existing, HeaLayout incoming)
    {
        if (existing is not (HeaLayout.PackedArtwork or HeaLayout.PackedMask))
        {
            return false;
        }

        return incoming != existing;
    }

    public static bool LooksLikeMapLight(HeaFile hea, string name)
    {
        if (!hea.IsValid)
        {
            return false;
        }

        if (hea.IsMask)
        {
            return true;
        }

        string stem = Path.GetFileNameWithoutExtension(name);
        bool mapName = stem.StartsWith("lod", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("map", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("lgt", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("light", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("fx", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("efx", StringComparison.OrdinalIgnoreCase);
        bool tileGrid = hea.DestWidth is >= 4 and <= 512 && hea.DestHeight is >= 4 and <= 512
            && hea.DestWidth * hea.DestHeight <= 512 * 512;
        return mapName && tileGrid && !LooksLikeArtwork(hea);
    }

    public static IReadOnlyList<string> NamesForMap(string mapName, OverlayKind kind = OverlayKind.Light)
    {
        if (!TryMapStem(mapName, out string stem, out int id))
        {
            return [];
        }

        var names = new List<string>();
        Add(names, stem);
        string[] pads =
        [
            id.ToString(),
            id.ToString("D4"),
            id.ToString("D5"),
            id.ToString("D6")
        ];
        string[] prefixes = kind == OverlayKind.Effect
            ? ["fx", "efx", "ef", "ov"]
            : ["", "lod", "map", "lgt", "light"];
        foreach (string prefix in prefixes)
        {
            foreach (string pad in pads)
            {
                Add(names, prefix + pad);
            }
        }

        return names;
    }

    public static bool NameMatches(string entryName, string mapName, OverlayKind kind = OverlayKind.Light)
    {
        string other = Path.GetFileNameWithoutExtension(entryName);
        if (string.IsNullOrWhiteSpace(other))
        {
            return false;
        }

        foreach (string name in NamesForMap(mapName, kind))
        {
            if (other.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string PreferredFileName(string mapName, OverlayKind kind = OverlayKind.Light)
    {
        if (!TryMapStem(mapName, out _, out int id))
        {
            string stem = Path.GetFileNameWithoutExtension(mapName);
            string raw = kind == OverlayKind.Effect ? "fx" + stem : stem;
            return TruncateName(raw) + ".hea";
        }

        string body = kind == OverlayKind.Effect ? "fx" + id.ToString("D6") : id.ToString("D6");
        if (body.Length > 9)
        {
            body = kind == OverlayKind.Effect ? "fx" + id.ToString("D5") : id.ToString("D5");
        }

        return body + ".hea";
    }

    public static ArchiveEntry? FindForMap(
        IEnumerable<IArchive> archives,
        string mapName,
        OverlayKind kind = OverlayKind.Light)
    {
        if (!TryMapStem(mapName, out _, out _))
        {
            return null;
        }

        foreach (IArchive archive in archives)
        {
            foreach (ArchiveEntry entry in archive.Entries)
            {
                if (!entry.Extension.Equals(".hea", StringComparison.OrdinalIgnoreCase)
                    || !NameMatches(entry.Name, mapName, kind))
                {
                    continue;
                }

                try
                {
                    var hea = new HeaFile(entry);
                    if (LooksLikeMapLight(hea, entry.Name))
                    {
                        return entry;
                    }
                }
                catch
                {
                    // skip
                }
            }
        }

        return null;
    }

    public static bool ClientHasMapLights(IEnumerable<IArchive> archives)
    {
        foreach (IArchive archive in archives)
        {
            foreach (ArchiveEntry entry in archive.Entries)
            {
                if (!entry.Extension.Equals(".hea", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    if (LooksLikeMapLight(new HeaFile(entry), entry.Name))
                    {
                        return true;
                    }
                }
                catch
                {
                    // skip
                }
            }
        }

        return false;
    }

    public static PixelBuffer ToTileOverlay(PixelBuffer source, int mapWidth, int mapHeight)
    {
        mapWidth = Math.Max(1, mapWidth);
        mapHeight = Math.Max(1, mapHeight);
        if (source.Width == mapWidth && source.Height == mapHeight)
        {
            return source;
        }

        return source.Resize(mapWidth, mapHeight);
    }

    public static void Overlay(PixelBuffer canvas, MapFile map, MapViewLike view, HeaFile hea, PixelBuffer? decoded)
    {
        if (decoded is null || !hea.IsValid)
        {
            return;
        }

        Overlay(
            canvas,
            map,
            view,
            ToTileOverlay(decoded, map.Width, map.Height),
            OverlayBlendMode.Shade);
    }

    public static void Overlay(
        PixelBuffer canvas,
        MapFile map,
        MapViewLike view,
        PixelBuffer tiles,
        OverlayBlendMode mode,
        IEnumerable<(int X, int Y)>? only = null)
    {
        if (tiles.Width != map.Width || tiles.Height != map.Height)
        {
            tiles = ToTileOverlay(tiles, map.Width, map.Height);
        }

        if (only is not null)
        {
            foreach ((int x, int y) in only)
            {
                if ((uint)x >= (uint)map.Width || (uint)y >= (uint)map.Height)
                {
                    continue;
                }

                ApplyTile(canvas, view, tiles, x, y, mode);
            }

            return;
        }

        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                ApplyTile(canvas, view, tiles, x, y, mode);
            }
        }
    }

    public readonly record struct MapViewLike(int OriginX, int OriginY, int StepX, int StepY, int TileW, int TileH);

    private static void ApplyTile(
        PixelBuffer canvas,
        MapViewLike view,
        PixelBuffer tiles,
        int tileX,
        int tileY,
        OverlayBlendMode mode)
    {
        var (b, g, r, a) = tiles.GetPixel(tileX, tileY);
        if (a == 0)
        {
            return;
        }

        if (mode == OverlayBlendMode.Additive)
        {
            AddDiamond(canvas, view, tileX, tileY, b, g, r, a);
            return;
        }

        int lum = Math.Clamp((r + g + b) / 3, 0, 255);
        ShadeDiamond(canvas, view, tileX, tileY, lum);
    }

    private static void ShadeDiamond(PixelBuffer canvas, MapViewLike view, int tileX, int tileY, int lum)
    {
        (int sx, int sy) = IsoCoords.TileToScreen(tileX, tileY, view.OriginX, view.OriginY, view.StepX, view.StepY);
        int shade = 255 - lum;
        if (shade <= 8)
        {
            return;
        }

        byte a = (byte)Math.Clamp(shade, 0, 180);
        BlendDiamond(canvas, view, sx, sy, 0, 0, 0, a, additive: false);
    }

    private static void AddDiamond(
        PixelBuffer canvas,
        MapViewLike view,
        int tileX,
        int tileY,
        byte b,
        byte g,
        byte r,
        byte a)
    {
        (int sx, int sy) = IsoCoords.TileToScreen(tileX, tileY, view.OriginX, view.OriginY, view.StepX, view.StepY);
        byte glow = (byte)Math.Clamp(a * 180 / 255, 8, 180);
        BlendDiamond(canvas, view, sx, sy, b, g, r, glow, additive: true);
    }

    private static void BlendDiamond(
        PixelBuffer canvas,
        MapViewLike view,
        int sx,
        int sy,
        byte b,
        byte g,
        byte r,
        byte a,
        bool additive)
    {
        for (int y = 0; y < view.TileH; y++)
        {
            for (int x = 0; x < view.TileW; x++)
            {
                if (!IsoCoords.DiamondContains(sx + x, sy + y, sx, sy, view.TileW, view.TileH))
                {
                    continue;
                }

                var (db, dg, dr, da) = canvas.GetPixel(sx + x, sy + y);
                if (additive)
                {
                    canvas.SetPixel(
                        sx + x,
                        sy + y,
                        (byte)Math.Min(255, db + b * a / 255),
                        (byte)Math.Min(255, dg + g * a / 255),
                        (byte)Math.Min(255, dr + r * a / 255),
                        da);
                    continue;
                }

                int inv = 255 - a;
                canvas.SetPixel(
                    sx + x,
                    sy + y,
                    (byte)((db * inv) / 255),
                    (byte)((dg * inv) / 255),
                    (byte)((dr * inv) / 255),
                    da);
            }
        }
    }

    private static void Add(List<string> names, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (string existing in names)
        {
            if (existing.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        names.Add(value);
    }

    private static string TruncateName(string stem)
    {
        stem = new string(stem.Where(c => char.IsAsciiLetterOrDigit(c)).ToArray());
        if (stem.Length <= 9)
        {
            return string.IsNullOrEmpty(stem) ? "overlay" : stem;
        }

        return stem[..9];
    }

    private static bool TryMapStem(string mapName, out string stem, out int id)
    {
        stem = Path.GetFileNameWithoutExtension(mapName);
        id = 0;
        if (string.IsNullOrWhiteSpace(stem))
        {
            return false;
        }

        var digits = new string(stem.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out id);
    }
}
