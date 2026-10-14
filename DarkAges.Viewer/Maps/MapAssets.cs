using System.IO;
using DarkAges.Formats.Catalog;
using DarkAges.Formats.Data;
using DarkAges.Formats.Drawing;
using DarkAges.Formats.Imaging;
using DarkAges.Formats.Maps;

namespace DarkAges.Viewer.Maps;

public sealed class MapAssets
{
    private readonly IArchive _seo;
    private readonly IArchive _ia;
    private readonly ArchiveEntry? _tilea;
    private readonly ArchiveEntry? _tileas;
    private readonly bool _rgbaFloors;
    private readonly Dictionary<int, PixelBuffer> _floors = new();
    private readonly Dictionary<int, PixelBuffer> _snow = new();
    private readonly Dictionary<int, PixelBuffer> _walls = new();
    private readonly Dictionary<(int Id, int Width), PixelBuffer> _wallScaled = new();
    private byte[]? _tileaBytes;
    private byte[]? _tileasBytes;
    private readonly object _cacheLock = new();

    public MapAssets(IArchive seo, IArchive ia, string? label = null)
    {
        _seo = seo;
        _ia = ia;
        Label = string.IsNullOrWhiteSpace(label) ? seo.DisplayName : label;
        _tilea = seo.GetEntry("TILEA.BMP");
        _tileas = seo.GetEntry("TILEAS.BMP");
        _rgbaFloors = _tilea is null;
        FloorCount = _tilea is null
            ? MaxRgbaId(seo, "tile_")
            : FloorTiles.Count(_tilea.Size);
        SnowCount = _tileas is null ? 0 : FloorTiles.Count(_tileas.Size);
        WallIds = ia.Entries
            .Select(e =>
            {
                string stem = Path.GetFileNameWithoutExtension(e.Name);
                if (!stem.StartsWith("stc", StringComparison.OrdinalIgnoreCase))
                {
                    return 0;
                }

                var digits = new string(stem.Skip(3).Where(char.IsDigit).ToArray());
                return int.TryParse(digits, out int n) ? n : 0;
            })
            .Where(n => n > 0)
            .Distinct()
            .OrderBy(n => n)
            .ToList();
        _wallSet = WallIds.ToHashSet();
        MaxWallId = WallIds.Count == 0 ? 0 : WallIds[^1];
        ArchiveEntry? sotpEntry = ia.GetEntry("sotp.dat");
        _sotp = sotpEntry is null ? [] : sotpEntry.ReadAllBytes();
    }

    public byte[] Sotp => _sotp;

    public void ReplaceSotp(byte[] data)
    {
        _sotp = data ?? [];
        lock (_cacheLock)
        {
            _walls.Clear();
            _wallScaled.Clear();
        }
    }

    public string Label { get; }
    public string SeoPath => _seo.SourcePath;
    public string IaPath => _ia.SourcePath;
    public IArchive Seo => _seo;
    public IArchive Ia => _ia;
    public IReadOnlyList<int> WallIds { get; }
    public int MaxWallId { get; }
    private readonly HashSet<int> _wallSet;
    private byte[] _sotp;

    public int FloorCount { get; }
    public int SnowCount { get; }
    public bool HasFloors => _tilea is not null || FloorCount > 0;
    public bool HasWalls => WallIds.Count > 0;

    private byte[]? AtlasBytes(bool snow)
    {
        lock (_cacheLock)
        {
            if (snow)
            {
                if (_tileasBytes is null && _tileas is not null)
                {
                    _tileasBytes = FloorTiles.LoadAtlas(_tileas);
                }

                return _tileasBytes ?? (_tileaBytes ??= _tilea is null ? null : FloorTiles.LoadAtlas(_tilea));
            }

            return _tileaBytes ??= _tilea is null ? null : FloorTiles.LoadAtlas(_tilea);
        }
    }

    public PixelBuffer? Floor(int tileId, bool snow, int atlasOffset = 0, Palette? paletteOverride = null)
    {
        if (tileId <= 0)
        {
            return null;
        }

        int index = tileId - 1 + Math.Max(0, atlasOffset);
        Dictionary<int, PixelBuffer> cache = snow ? _snow : _floors;
        bool custom = paletteOverride is not null;
        if (!custom)
        {
            lock (_cacheLock)
            {
                if (cache.TryGetValue(index, out PixelBuffer? cached))
                {
                    return cached;
                }
            }
        }

        if (_rgbaFloors)
        {
            PixelBuffer? rgba = DecodeRgba(_seo.GetEntry($"tile_{tileId:D5}.rgba"));
            if (rgba is not null && !custom)
            {
                lock (_cacheLock)
                {
                    cache[index] = rgba;
                }
            }

            return rgba;
        }

        byte[]? atlas = AtlasBytes(snow);
        if (atlas is null)
        {
            return null;
        }

        if (index < 0 || index >= FloorTiles.Count(atlas.Length))
        {
            return null;
        }

        Palette pal = paletteOverride
            ?? (snow ? PaletteLocator.ForSnowFloor(_seo) : PaletteLocator.ForFloorTile(_seo, index + 1))
            ?? PaletteLocator.Find(_seo, "mpt0000.pal", "mpt000.pal", "mpspal.pal", "field001.pal", "legend.pal")
            ?? Fallback();
        PixelBuffer img = pal.Render(FloorTiles.ReadTile(atlas, index), FloorTiles.TileWidth, FloorTiles.TileHeight);
        if (!custom)
        {
            lock (_cacheLock)
            {
                cache[index] = img;
            }
        }

        return img;
    }

    public PixelBuffer? Wall(int wallId, Palette? paletteOverride = null)
    {
        if (wallId <= 0 || wallId % 10000 <= 1)
        {
            return null;
        }

        bool custom = paletteOverride is not null;
        if (!custom)
        {
            lock (_cacheLock)
            {
                if (_walls.TryGetValue(wallId, out PixelBuffer? cached))
                {
                    return cached;
                }
            }
        }

        ArchiveEntry? entry = _ia.GetEntry($"stc{wallId:D5}.rgba")
            ?? _ia.GetEntry($"stc{wallId:D5}.hpf")
            ?? _ia.GetEntry($"stc{wallId:D4}.hpf")
            ?? _ia.GetEntry($"stc{wallId}.hpf");
        if (entry is null)
        {
            return null;
        }

        PixelBuffer? rgba = DecodeRgba(entry);
        if (rgba is not null)
        {
            if (!custom)
            {
                lock (_cacheLock)
                {
                    _walls[wallId] = rgba;
                }
            }

            return rgba;
        }

        Palette pal = paletteOverride ?? PaletteLocator.ForWallTile(_ia, wallId) ?? Fallback();
        PixelBuffer img = new HpfFile(entry).Render(pal);
        if (!custom)
        {
            lock (_cacheLock)
            {
                _walls[wallId] = img;
            }
        }

        return img;
    }

    public readonly struct MapView
    {
        public int OriginX { get; init; }
        public int OriginY { get; init; }
        public int StepX { get; init; }
        public int StepY { get; init; }
        public int TileW { get; init; }
        public int TileH { get; init; }
        public int WallW { get; init; }
        public int WallFoot { get; init; }
        public int RightFoot { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
    }

    public static MapView Layout(MapFile map, int maxEdge = 0)
    {
        int nativeW = (map.Width + map.Height) * 28 + FloorTiles.TileWidth;
        int nativeH = (map.Width + map.Height) * 14 + 160 + FloorTiles.TileHeight;
        double scale = 1;
        if (maxEdge > 0)
        {
            scale = Math.Min(1, maxEdge / (double)Math.Max(nativeW, nativeH));
        }

        int stepX = Math.Max(2, (int)Math.Round(28 * scale));
        int stepY = Math.Max(1, (int)Math.Round(14 * scale));
        int tileW = stepX * 2;
        int tileH = Math.Max(1, (int)Math.Round(FloorTiles.TileHeight * stepX / 28.0));
        int originY = Math.Max(8, (int)Math.Round(160 * scale));
        int originX = map.Height * stepX;
        return new MapView
        {
            OriginX = originX,
            OriginY = originY,
            StepX = stepX,
            StepY = stepY,
            TileW = tileW,
            TileH = tileH,
            WallW = stepX,
            WallFoot = Math.Max(1, (int)Math.Round(WallFootNative * scale)),
            RightFoot = Math.Max(1, (int)Math.Round((WallFootNative + RightWallFootAdjust) * scale)),
            Width = Math.Max(1, (map.Width + map.Height) * stepX + tileW),
            Height = Math.Max(1, (map.Width + map.Height) * stepY + originY + tileH + 96)
        };
    }

    public PixelBuffer Render(
        MapFile map,
        bool snow,
        int maxEdge = 0,
        int floorOffset = 0,
        bool transparentBackdrop = false,
        bool tabMap = false,
        Palette? paletteOverride = null,
        IReadOnlyList<MapWarp>? warps = null,
        IReadOnlyList<MapWarp>? arrivals = null,
        IReadOnlyList<MapWarp>? serverWarps = null,
        IReadOnlyList<MapWarp>? serverArrivals = null,
        PixelBuffer? reuse = null)
    {
        MapView view = Layout(map, maxEdge);
        PixelBuffer canvas = reuse is not null && reuse.Width == view.Width && reuse.Height == view.Height
            ? reuse
            : new PixelBuffer(view.Width, view.Height);
        if (transparentBackdrop)
        {
            canvas.Clear(0, 0, 0, 0);
        }
        else
        {
            canvas.Clear(18, 20, 26, 255);
        }

        int maxSum = map.Width + map.Height - 1;
        for (int sum = 0; sum < maxSum; sum++)
        {
            BlitDiagonal(map, view, sum, snow, floorOffset, canvas, walls: false, paletteOverride);
            BlitDiagonal(map, view, sum, snow, floorOffset, canvas, walls: true, paletteOverride);
        }

        if (tabMap)
        {
            DrawTabMap(map, canvas, view);
        }

        if (warps is { Count: > 0 } || arrivals is { Count: > 0 })
        {
            DrawWarps(map, warps, arrivals, canvas, view);
        }

        if (serverWarps is { Count: > 0 } || serverArrivals is { Count: > 0 })
        {
            DrawWarps(map, serverWarps, serverArrivals, canvas, view, server: true);
        }

        return canvas;
    }

    public (int X, int Y, int W, int H) RepaintTiles(
        MapFile map,
        PixelBuffer canvas,
        MapView view,
        IEnumerable<(int X, int Y)> tiles,
        bool snow,
        int floorOffset,
        bool tabMap,
        IReadOnlyList<MapWarp>? warps,
        IReadOnlyList<MapWarp>? arrivals = null,
        IReadOnlyList<MapWarp>? serverWarps = null,
        IReadOnlyList<MapWarp>? serverArrivals = null)
    {
        int left = canvas.Width;
        int top = canvas.Height;
        int right = 0;
        int bottom = 0;
        foreach ((int x, int y) in tiles)
        {
            if ((uint)x >= (uint)map.Width || (uint)y >= (uint)map.Height)
            {
                continue;
            }

            (int sx, int sy) = TileToScreen(x, y, view.OriginX, view.OriginY, view.StepX, view.StepY);
            MapTile tile = map[x, y];
            int wallH = Math.Max(WallHeight(tile.LeftWall, view.WallW), WallHeight(tile.RightWall, view.WallW));
            int x0 = sx;
            int y0 = sy + view.WallFoot - Math.Max(view.TileH, wallH);
            int x1 = sx + view.TileW;
            int y1 = sy + view.TileH;
            left = Math.Min(left, x0);
            top = Math.Min(top, y0);
            right = Math.Max(right, x1);
            bottom = Math.Max(bottom, y1);
        }

        if (right <= left || bottom <= top)
        {
            return (0, 0, 0, 0);
        }

        left = Math.Max(0, left - view.TileW);
        top = Math.Max(0, top - 8);
        right = Math.Min(canvas.Width, right + view.TileW);
        bottom = Math.Min(canvas.Height, bottom + view.StepY);
        canvas.FillRect(left, top, right - left, bottom - top, 18, 20, 26, 255);

        var involved = new List<(int X, int Y, int Sum)>();
        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                (int sx, int sy) = TileToScreen(x, y, view.OriginX, view.OriginY, view.StepX, view.StepY);
                if (sx + view.TileW < left || sx > right || sy + 220 < top || sy > bottom)
                {
                    continue;
                }

                involved.Add((x, y, x + y));
            }
        }

        involved.Sort((a, b) => a.Sum.CompareTo(b.Sum));
        int i = 0;
        while (i < involved.Count)
        {
            int sum = involved[i].Sum;
            int start = i;
            while (i < involved.Count && involved[i].Sum == sum)
            {
                i++;
            }

            for (int j = start; j < i; j++)
            {
                BlitCell(map, involved[j].X, involved[j].Y, view, snow, floorOffset, canvas, walls: false, null);
            }

            for (int j = start; j < i; j++)
            {
                BlitCell(map, involved[j].X, involved[j].Y, view, snow, floorOffset, canvas, walls: true, null);
            }
        }

        if (tabMap || warps is { Count: > 0 } || arrivals is { Count: > 0 }
            || serverWarps is { Count: > 0 } || serverArrivals is { Count: > 0 })
        {
            foreach ((int x, int y, _) in involved)
            {
                MapTile tile = map[x, y];
                (int sx, int sy) = TileToScreen(x, y, view.OriginX, view.OriginY, view.StepX, view.StepY);
                if (tabMap && (IsTabBlocked(tile.LeftWall) || IsTabBlocked(tile.RightWall)))
                {
                    canvas.FillDiamond(sx, sy, view.TileW, view.TileH, 255, 255, 255, 128);
                }

                PaintWarpTile(canvas, view, sx, sy, WarpCovers(warps, x, y), WarpLanding(arrivals, x, y));
                PaintWarpTile(
                    canvas,
                    view,
                    sx,
                    sy,
                    WarpCovers(serverWarps, x, y),
                    WarpLanding(serverArrivals, x, y),
                    server: true);
            }
        }

        return (left, top, right - left, bottom - top);
    }

    private static bool WarpCovers(IReadOnlyList<MapWarp>? warps, int x, int y)
    {
        if (warps is null)
        {
            return false;
        }

        foreach (MapWarp warp in warps)
        {
            if (warp.Covers(x, y))
            {
                return true;
            }
        }

        return false;
    }

    private static bool WarpLanding(IReadOnlyList<MapWarp>? arrivals, int x, int y)
    {
        if (arrivals is null)
        {
            return false;
        }

        foreach (MapWarp warp in arrivals)
        {
            if (warp.HasLanding && warp.DestX == x && warp.DestY == y)
            {
                return true;
            }
        }

        return false;
    }

    // FillDiamond is B,G,R,A. Exit tiles (from) are light blue; landings (to) are yellow.
    // Server overlay uses magenta exits and orange landings so they stay distinct.
    private const byte ExitB = 255, ExitG = 210, ExitR = 70, ExitA = 160;
    private const byte LandB = 40, LandG = 210, LandR = 255, LandA = 160;
    private const byte ServerExitB = 210, ServerExitG = 70, ServerExitR = 220, ServerExitA = 170;
    private const byte ServerLandB = 40, ServerLandG = 120, ServerLandR = 255, ServerLandA = 160;

    private static void PaintWarpTile(
        PixelBuffer canvas,
        MapView view,
        int sx,
        int sy,
        bool exit,
        bool landing,
        bool server = false)
    {
        if (landing)
        {
            canvas.FillDiamond(
                sx,
                sy,
                view.TileW,
                view.TileH,
                server ? ServerLandB : LandB,
                server ? ServerLandG : LandG,
                server ? ServerLandR : LandR,
                server ? ServerLandA : LandA);
        }

        if (exit)
        {
            canvas.FillDiamond(
                sx,
                sy,
                view.TileW,
                view.TileH,
                server ? ServerExitB : ExitB,
                server ? ServerExitG : ExitG,
                server ? ServerExitR : ExitR,
                server ? ServerExitA : ExitA);
        }
    }

    private int WallHeight(int wallId, int wallW)
    {
        PixelBuffer? wall = WallScaled(wallId, wallW, null);
        return wall?.Height ?? 0;
    }

    public PixelBuffer RenderTabSquare(MapFile map, int cell = 8)
    {
        cell = Math.Clamp(cell, 4, 32);
        var canvas = new PixelBuffer(Math.Max(1, map.Width * cell), Math.Max(1, map.Height * cell));
        canvas.Clear(18, 20, 26, 255);
        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                MapTile tile = map[x, y];
                byte r, g, b;
                if (tile.Floor == 0)
                {
                    b = 12;
                    g = 12;
                    r = 12;
                }
                else if (IsTabBlocked(tile.LeftWall) || IsTabBlocked(tile.RightWall))
                {
                    b = 210;
                    g = 210;
                    r = 210;
                }
                else if (IsBlockingWall(tile.LeftWall) || IsBlockingWall(tile.RightWall))
                {
                    b = 96;
                    g = 108;
                    r = 128;
                }
                else
                {
                    b = 46;
                    g = 92;
                    r = 58;
                }

                canvas.FillRect(x * cell, y * cell, cell - 1, cell - 1, b, g, r, 255);
            }
        }

        return canvas;
    }

    /// <summary>
    /// Capricorn "Show tab map": a floor diamond is blocked when sotp[wallId - 1] is exactly 15.
    /// </summary>
    public bool IsTabBlocked(int wallId) => wallId > 0 && SotpFlags(wallId) == 15;

    private void DrawTabMap(MapFile map, PixelBuffer canvas, MapView view)
    {
        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                MapTile tile = map[x, y];
                if (!IsTabBlocked(tile.LeftWall) && !IsTabBlocked(tile.RightWall))
                {
                    continue;
                }

                (int sx, int sy) = TileToScreen(x, y, view.OriginX, view.OriginY, view.StepX, view.StepY);
                canvas.FillDiamond(sx, sy, view.TileW, view.TileH, 255, 255, 255, 128);
            }
        }
    }

    private static void DrawWarps(
        MapFile map,
        IReadOnlyList<MapWarp>? warps,
        IReadOnlyList<MapWarp>? arrivals,
        PixelBuffer canvas,
        MapView view,
        bool server = false)
    {
        if (arrivals is { Count: > 0 })
        {
            foreach (MapWarp warp in arrivals)
            {
                if (!warp.HasLanding
                    || (uint)warp.DestX >= (uint)map.Width
                    || (uint)warp.DestY >= (uint)map.Height)
                {
                    continue;
                }

                (int sx, int sy) = TileToScreen(warp.DestX, warp.DestY, view.OriginX, view.OriginY, view.StepX, view.StepY);
                PaintWarpTile(canvas, view, sx, sy, exit: false, landing: true, server);
            }
        }

        if (warps is { Count: > 0 })
        {
            foreach (MapWarp warp in warps)
            {
                foreach ((int x, int y) in warp.Tiles())
                {
                    if ((uint)x >= (uint)map.Width || (uint)y >= (uint)map.Height)
                    {
                        continue;
                    }

                    (int sx, int sy) = TileToScreen(x, y, view.OriginX, view.OriginY, view.StepX, view.StepY);
                    PaintWarpTile(canvas, view, sx, sy, exit: true, landing: false, server);
                }
            }
        }
    }

    private void BlitDiagonal(
        MapFile map,
        MapView view,
        int sum,
        bool snow,
        int floorOffset,
        PixelBuffer canvas,
        bool walls,
        Palette? paletteOverride)
    {
        for (int x = 0; x < map.Width; x++)
        {
            int y = sum - x;
            if ((uint)y >= (uint)map.Height)
            {
                continue;
            }

            BlitCell(map, x, y, view, snow, floorOffset, canvas, walls, paletteOverride);
        }
    }

    private void BlitCell(
        MapFile map,
        int x,
        int y,
        MapView view,
        bool snow,
        int floorOffset,
        PixelBuffer canvas,
        bool walls,
        Palette? paletteOverride)
    {
        MapTile tile = map[x, y];
        (int sx, int sy) = TileToScreen(x, y, view.OriginX, view.OriginY, view.StepX, view.StepY);
        if (!walls)
        {
            PixelBuffer? floor = Scaled(Floor(tile.Floor, snow, floorOffset, paletteOverride), view.TileW, view.TileH);
            if (floor is not null)
            {
                canvas.Blit(floor, sx, sy);
            }

            return;
        }

        PixelBuffer? left = WallScaled(tile.LeftWall, view.WallW, paletteOverride);
        if (left is not null)
        {
            BlitWall(canvas, left, sx, sy + view.WallFoot - left.Height, tile.LeftWall);
        }

        PixelBuffer? right = WallScaled(tile.RightWall, view.WallW, paletteOverride);
        if (right is not null)
        {
            BlitWall(canvas, right, sx + view.StepX, sy + view.RightFoot - right.Height, tile.RightWall);
        }
    }

    private PixelBuffer? WallScaled(int wallId, int wallW, Palette? paletteOverride)
    {
        PixelBuffer? source = Wall(wallId, paletteOverride);
        if (source is null)
        {
            return null;
        }

        if (paletteOverride is not null)
        {
            return Scaled(source, wallW, 0);
        }

        (int Id, int Width) key = (wallId, wallW);
        lock (_cacheLock)
        {
            if (_wallScaled.TryGetValue(key, out PixelBuffer? cached))
            {
                return cached;
            }
        }

        PixelBuffer scaled = Scaled(source, wallW, 0)!;
        lock (_cacheLock)
        {
            _wallScaled[key] = scaled;
        }

        return scaled;
    }

    private void BlitWall(PixelBuffer canvas, PixelBuffer wall, int x, int y, int wallId)
    {
        if (IsAdditiveWall(wallId))
        {
            canvas.BlitAdd(wall, x, y);
        }
        else
        {
            canvas.Blit(wall, x, y);
        }
    }

    /// <summary>
    /// Classic servers index sotp as sotp[wallId - 1]. Capricorn uses bit 0x80 as additive glass.
    /// Any non-zero byte is a collision block for pathing (not drawing).
    /// </summary>
    public byte SotpFlags(int wallId)
    {
        int i = wallId - 1;
        if (i < 0 || i >= _sotp.Length)
        {
            return 0;
        }

        return _sotp[i];
    }

    public bool IsAdditiveWall(int wallId) => (SotpFlags(wallId) & 0x80) == 0x80;

    public bool IsBlockingWall(int wallId) => SotpFlags(wallId) != 0;

    private static PixelBuffer? Scaled(PixelBuffer? source, int width, int height)
    {
        if (source is null)
        {
            return null;
        }

        if (height <= 0)
        {
            height = Math.Max(1, source.Height * width / Math.Max(1, source.Width));
        }

        return source.Resize(width, height);
    }

    public int Coverage(MapFile map)
    {
        int hits = 0;
        foreach (MapTile tile in map.Tiles)
        {
            if (tile.Floor > 0 && tile.Floor <= FloorCount)
            {
                hits++;
            }

            if (tile.LeftWall > 0 && _wallSet.Contains(tile.LeftWall))
            {
                hits++;
            }

            if (tile.RightWall > 0 && _wallSet.Contains(tile.RightWall))
            {
                hits++;
            }
        }

        return hits;
    }

    public static MapAssets Pick(IReadOnlyList<MapAssets> sets, MapFile map, string? hint)
    {
        if (sets.Count == 0)
        {
            throw new ArgumentException("No map tilesets are loaded.");
        }

        bool lodFolder = IsLodMapsHint(hint);
        return sets
            .OrderByDescending(s => s.Coverage(map))
            .ThenByDescending(s => lodFolder == IsStoned(s))
            .ThenBy(s => lodFolder ? -s.FloorCount : s.FloorCount)
            .ThenByDescending(s => s.MaxWallId)
            .First();
    }

    private static PixelBuffer? DecodeRgba(ArchiveEntry? entry)
    {
        if (entry is null || !entry.Extension.Equals(".rgba", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return RgbaImage.TryDecode(entry.ReadAllBytes())?.Pixels;
    }

    private static int MaxRgbaId(IArchive archive, string prefix)
    {
        int max = 0;
        foreach (ArchiveEntry entry in archive.Entries)
        {
            string stem = Path.GetFileNameWithoutExtension(entry.Name);
            if (!stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var digits = new string(stem.Skip(prefix.Length).Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out int n))
            {
                max = Math.Max(max, n);
            }
        }

        return max;
    }

    public static bool IsLodMapsHint(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
        {
            return false;
        }

        foreach (string part in hint.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/'))
        {
            if (part.Equals("LOD Maps", StringComparison.OrdinalIgnoreCase)
                || part.Equals("Lod Maps", StringComparison.OrdinalIgnoreCase)
                || part.Equals("STONEDDAT", StringComparison.OrdinalIgnoreCase)
                || part.Equals("STONED", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsStoned(MapAssets set) =>
        set.SeoPath.Contains("STONED", StringComparison.OrdinalIgnoreCase)
        || set.IaPath.Contains("STONED", StringComparison.OrdinalIgnoreCase)
        || set.Label.Contains("STONED", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Capricorn MapEditor wall blit: Y = floorY + (x + 1) * 14 + 14 - height,
    /// which is floor AABB Y + 28 - height. Left and right walls share this foot.
    /// </summary>
    public const int WallFootNative = 28;
    /// <summary>Extra pixels on right-wall Y only. Positive lowers the right wall. Reference editor uses 0.</summary>
    public const int RightWallFootAdjust = 0;

    public static (int X, int Y) TileToScreen(int x, int y, int originX, int originY, int stepX = 28, int stepY = 14) =>
        IsoCoords.TileToScreen(x, y, originX, originY, stepX, stepY);

    public static bool TryHit(int px, int py, MapFile map, out int tileX, out int tileY) =>
        TryHit(px, py, map, Layout(map), out tileX, out tileY);

    public static bool TryHit(int px, int py, MapFile map, MapView view, out int tileX, out int tileY) =>
        IsoCoords.TryHit(
            px,
            py,
            map.Width,
            map.Height,
            out tileX,
            out tileY,
            view.OriginX,
            view.OriginY,
            view.StepX,
            view.StepY,
            view.TileW,
            view.TileH);

    private static Palette Fallback()
    {
        using var ms = new MemoryStream(new byte[768]);
        return new Palette(ms);
    }
}
