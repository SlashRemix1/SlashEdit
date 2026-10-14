using System.IO;
using System.Text;
using DarkAges.Formats.Catalog;
using DarkAges.Formats.Data;
using DarkAges.Formats.Drawing;
using DarkAges.Formats.Imaging;
using DarkAges.Viewer.Imaging;
using DarkAges.Viewer.Maps;
using DarkAges.Formats.Maps;

namespace DarkAges.Viewer.Preview;

public static class PreviewBuilder
{
    public static PreviewResult Build(
        IArchive archive,
        ArchiveEntry entry,
        int frameIndex,
        string? paletteOverride,
        string animationGroup,
        bool additive,
        IReadOnlyList<ArchiveEntry>? overlayEntries,
        bool contactSheet,
        IReadOnlyList<MapAssets>? mapTilesets = null,
        string? tilesetName = null,
        string? mapHint = null,
        MapTilesetOption? tileset = null,
        int floorOffset = 0,
        WarpTable? warps = null)
    {
        try
        {
            string ext = entry.Extension.ToLowerInvariant();
            if (ext == ".map")
            {
                return FromMap(entry, mapTilesets, tilesetName, mapHint, tileset, floorOffset, paletteOverride, warps);
            }

            if (MetaFile.LooksLike(entry))
            {
                return FromMeta(entry, archive.SourcePath);
            }

            Palette? palette = ResolvePalette(archive, entry, paletteOverride);
            if (ext == ".bik")
            {
                return FromBink(entry, frameIndex);
            }

            if (contactSheet)
            {
                return ext switch
                {
                    ".epf" => KhanPieces.IsKhanArchive(archive) && KhanPieces.TryParse(entry.Name, out _)
                        ? KhanSheet(archive, entry, palette, additive)
                        : UiOrItemSheet(archive, entry, palette),
                    ".mpf" => MpfSheet(archive, entry, palette, animationGroup),
                    ".spf" => SpfContact(archive, entry, palette),
                    ".hpf" => HpfRelated(archive, entry, palette),
                    ".efa" => EfaSheet(entry),
                    ".fnt" => FromFnt(entry, frameIndex, contactSheet: true),
                    ".lft" => FromLft(entry, frameIndex, contactSheet: true),
                    ".upf" => FromUpf(archive, entry, palette, frameIndex, contactSheet: true),
                    ".pcx" => FromPcx(entry),
                    ".hea" => FromHea(entry),
                    ".bmp" => FromFloorAtlas(archive, entry, palette),
                    ".rgba" => FromRgba(entry, frameIndex),
                    _ => BuildSingle(archive, entry, ext, palette, frameIndex, animationGroup, additive, overlayEntries)
                };
            }

            return BuildSingle(archive, entry, ext, palette, frameIndex, animationGroup, additive, overlayEntries);
        }
        catch (Exception ex)
        {
            return new PreviewResult { Error = ex.Message, Metadata = "Failed to decode this entry." };
        }
    }

    private static PreviewResult BuildSingle(
        IArchive archive,
        ArchiveEntry entry,
        string ext,
        Palette? palette,
        int frameIndex,
        string animationGroup,
        bool additive,
        IReadOnlyList<ArchiveEntry>? overlayEntries) => ext switch
    {
        ".epf" => FromEpf(archive, entry, palette, frameIndex, additive, overlayEntries),
        ".mpf" => FromMpf(archive, entry, palette, frameIndex, animationGroup),
        ".hpf" => FromHpf(archive, entry, palette),
        ".spf" => FromSpf(archive, entry, palette, frameIndex),
        ".efa" => FromEfa(entry, frameIndex),
        ".pal" => FromPal(entry),
        ".pcx" => FromPcx(entry),
        ".bmp" => FromFloorAtlas(archive, entry, palette),
        ".tbl" => FromTbl(entry),
        ".mp3" or ".wav" => FromAudio(entry),
        ".jpf" or ".jpg" or ".jpeg" or ".png" => FromEncodedImage(entry),
        ".rgba" => FromRgba(entry, frameIndex),
        ".hea" => FromHea(entry),
        ".fnt" => FromFnt(entry, frameIndex, contactSheet: false),
        ".lft" => FromLft(entry, frameIndex, contactSheet: false),
        ".upf" => FromUpf(archive, entry, palette, frameIndex, contactSheet: false),
        ".bik" => FromBink(entry, frameIndex),
        ".map" => FromMap(entry, null, null, null, null, 0),
        _ => MetaFile.LooksLike(entry) ? FromMeta(entry, archive.SourcePath) : FromUnknown(entry)
    };

    public static PreviewResult FromMeta(ArchiveEntry entry, string? encodingHint = null)
    {
        MetaFile meta = MetaFile.Load(entry, encodingHint);
        IReadOnlyList<string> headers = meta.Headers();
        IReadOnlyList<string[]> rows = meta.Rows();
        return new PreviewResult
        {
            TableHeaders = headers,
            TableRows = rows,
            Metadata = $"{entry.DisplayName}\n{meta.Nodes.Count} records  zlib metafile\nUse the entry filter or search box to find names.",
            FrameCount = 1
        };
    }

    public static PreviewResult FromMap(
        ArchiveEntry entry,
        IReadOnlyList<MapAssets>? tilesets,
        string? tilesetName,
        string? mapHint,
        MapTilesetOption? tileset = null,
        int floorOffset = 0,
        string? paletteOverride = null,
        WarpTable? warps = null)
    {
        if (tilesets is null || tilesets.Count == 0)
        {
            return new PreviewResult
            {
                Error = "Map tilesets are not loaded. Open a folder that contains seo.dat (TILEA.BMP) and ia.dat.",
                Metadata = entry.Name
            };
        }

        MapFile map = MapFile.FromBytes(entry.ReadAllBytes());
        string hint = MapHint(mapHint, entry);
        bool snow = tileset?.Snow == true;
        MapAssets assets;
        if (tileset?.Assets is not null)
        {
            assets = tileset.Assets;
        }
        else if (!string.IsNullOrWhiteSpace(tilesetName)
            && !tilesetName.Equals("(auto)", StringComparison.OrdinalIgnoreCase))
        {
            assets = tilesets.FirstOrDefault(t => t.Label.Equals(tilesetName, StringComparison.OrdinalIgnoreCase))
                ?? MapAssets.Pick(tilesets, map, hint);
        }
        else
        {
            assets = MapAssets.Pick(tilesets, map, hint);
        }

        int bank = snow ? assets.SnowCount : assets.FloorCount;
        int offset = Math.Max(0, floorOffset);
        int nativeEdge = Math.Max(
            (map.Width + map.Height) * 28 + FloorTiles.TileWidth,
            (map.Width + map.Height) * 14 + 160 + FloorTiles.TileHeight);
        Palette? pal = PaletteLocator.Named(assets.Seo, paletteOverride)
            ?? PaletteLocator.Named(assets.Ia, paletteOverride);
        IReadOnlyList<MapWarp> mapWarps = warps?.For(entry.Name) ?? [];
        IReadOnlyList<MapWarp> mapArrivals = warps?.Arrivals(entry.Name) ?? [];
        if (mapWarps.Count == 0 && !string.Equals(hint, entry.Name, StringComparison.OrdinalIgnoreCase))
        {
            mapWarps = warps?.For(hint) ?? [];
        }

        if (mapArrivals.Count == 0 && !string.Equals(hint, entry.Name, StringComparison.OrdinalIgnoreCase))
        {
            mapArrivals = warps?.Arrivals(hint) ?? [];
        }

        int maxEdge = nativeEdge > 3600 ? 3600 : 0;
        MapAssets.MapView view = MapAssets.Layout(map, maxEdge);
        PixelBuffer pixels = assets.Render(
            map, snow, maxEdge: maxEdge, floorOffset: offset, paletteOverride: pal, warps: mapWarps, arrivals: mapArrivals);
        string layout = map.SizeHeader == MapSizeHeader.None
            ? "raw 6-byte tiles (size guessed)"
            : $"size header ({map.SizeHeader})";
        int floorHits = map.Tiles.Count(t => t.Floor > 0 && t.Floor - 1 + offset < bank);
        int floorUsed = map.Tiles.Count(t => t.Floor > 0);
        string bankName = snow ? "TILEAS.BMP (snow)" : "TILEA.BMP";
        string offsetNote = offset == 0
            ? ""
            : $"\nFloor atlas offset {offset}  (map floor N draws TILEA[N-1+{offset}]).";
        string warpNote = mapWarps.Count == 0
            ? ""
            : $"\n{mapWarps.Count} exit{(mapWarps.Count == 1 ? "" : "s")} (light blue)  {mapArrivals.Count} landing{(mapArrivals.Count == 1 ? "" : "s")} (yellow). Click a light-blue tile to open the destination.";
        return new PreviewResult
        {
            Pixels = pixels,
            Image = WpfImages.ToBitmap(pixels),
            Metadata = $"{entry.DisplayName}\n{map.Width} × {map.Height}  ({map.Tiles.Length} tiles, {layout})\nTileset: {assets.Label}{(snow ? "  snow floors" : "")}\nFloors {floorHits}/{floorUsed} in {bankName} ({bank} tiles).{offsetNote}{warpNote}",
            FrameCount = 1,
            Map = map,
            MapView = view,
            MapExits = mapWarps,
            MapKey = hint
        };
    }

    public static string MapHint(string? folderOrPath, ArchiveEntry entry)
    {
        if (string.IsNullOrWhiteSpace(folderOrPath))
        {
            return entry.Name;
        }

        if (Path.HasExtension(folderOrPath) &&
            folderOrPath.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
        {
            return folderOrPath;
        }

        return Path.Combine(folderOrPath, entry.Name);
    }

    public static PreviewResult TileGrid(IArchive archive, IEnumerable<ArchiveEntry> tiles, Palette? palette, int columns = 16)
    {
        try
        {
            var list = tiles.Take(256).ToList();
            if (list.Count == 0)
            {
                return new PreviewResult { Error = "No tiles in this archive." };
            }

            palette ??= PaletteLocator.Find(archive) ?? FallbackPalette();
            var rendered = new List<PixelBuffer>(list.Count);
            int cellW = 28;
            int cellH = 28;
            foreach (ArchiveEntry tileEntry in list)
            {
                try
                {
                    PixelBuffer tile = RenderTile(tileEntry, palette);
                    cellW = Math.Max(cellW, tile.Width);
                    cellH = Math.Max(cellH, tile.Height);
                    rendered.Add(tile);
                }
                catch
                {
                    rendered.Add(new PixelBuffer(1, 1));
                }
            }

            cellW = Math.Min(Math.Max(cellW, 1), 512);
            cellH = Math.Min(Math.Max(cellH, 1), 512);
            int rows = (int)Math.Ceiling(rendered.Count / (double)columns);
            var canvas = new PixelBuffer(columns * cellW, rows * cellH);
            canvas.Clear(32, 32, 36, 255);
            for (int i = 0; i < rendered.Count; i++)
            {
                int col = i % columns;
                int row = i / columns;
                PixelBuffer tile = rendered[i];
                canvas.Blit(tile, col * cellW + Math.Max(0, (cellW - tile.Width) / 2), row * cellH + Math.Max(0, (cellH - tile.Height) / 2));
            }

            return new PreviewResult
            {
                Pixels = canvas,
                Image = WpfImages.ToBitmap(canvas),
                Metadata = $"{list.Count} tiles\nPalette: {(paletteOverrideName(archive, palette))}",
                FrameCount = 1
            };
        }
        catch (Exception ex)
        {
            return new PreviewResult { Error = ex.Message };
        }
    }

    public static PreviewResult UiOrItemSheet(IArchive archive, ArchiveEntry entry, Palette? palette, int tileWidth = 32, int tileHeight = 32)
    {
        try
        {
            bool isItem = entry.Name.StartsWith("item", StringComparison.OrdinalIgnoreCase);
            if (entry.FrameIndex is not null)
            {
                return FromEpf(archive, entry, palette, 0, additive: false, overlays: null);
            }

            palette ??= PaletteLocator.Find(archive, isItem ? "item000.pal" : "gui06.pal", "gui000.pal", "item000.pal") ?? FallbackPalette();
            var epf = new EpfFile(entry);
            bool iconSheet = isItem
                || entry.Name.StartsWith("skill", StringComparison.OrdinalIgnoreCase)
                || entry.Name.StartsWith("spell", StringComparison.OrdinalIgnoreCase);
            if (!iconSheet && LooksLikeSlices(epf.Frames.Select(f => (f.Left, f.Top, f.Width, f.Height)).ToList()))
            {
                PixelBuffer assembled = AssembleEpf(epf, palette!);
                return new PreviewResult
                {
                    Pixels = assembled,
                    Image = WpfImages.Checkerboard(assembled.Width, assembled.Height, assembled),
                    Metadata = $"{entry.Name}\nAssembled {epf.Frames.Count} slices  {assembled.Width}x{assembled.Height}",
                    FrameCount = epf.Frames.Count
                };
            }
            int cellW = iconSheet ? tileWidth : Math.Max(epf.Width, 1);
            int cellH = iconSheet ? tileHeight : Math.Max(epf.Height, 1);
            if (!iconSheet)
            {
                foreach (EpfFrame frame in epf.Frames)
                {
                    cellW = Math.Max(cellW, Math.Max(frame.Right, frame.Left + frame.Width));
                    cellH = Math.Max(cellH, Math.Max(frame.Bottom, frame.Top + frame.Height));
                }
            }

            cellW = Math.Max(1, cellW);
            cellH = Math.Max(1, cellH);
            int columns = iconSheet ? 19 : Math.Min(8, Math.Max(1, epf.Frames.Count));
            int rows = Math.Max(1, (int)Math.Ceiling(epf.Frames.Count / (double)columns));
            var canvas = new PixelBuffer(columns * cellW, rows * cellH);
            canvas.Clear(28, 28, 32, 255);
            PaletteTable? table = LoadTable(archive, "itempal.tbl");
            int fileNumber = ParseDigits(entry.Name);
            for (int i = 0; i < epf.Frames.Count; i++)
            {
                int col = i % columns;
                int row = i / columns;
                Palette framePal = palette;
                if (isItem && table is not null && fileNumber > 0)
                {
                    int tileNumber = (fileNumber - 1) * 266 + i + 1;
                    int palNum = table.GetPaletteNumber(tileNumber);
                    framePal = PaletteLocator.FindNumbered(archive, "item", palNum) ?? palette;
                }

                PixelBuffer img = epf.Frames[i].Render(framePal);
                canvas.Blit(img, col * cellW + epf.Frames[i].Left, row * cellH + epf.Frames[i].Top);
            }

            return new PreviewResult
            {
                Pixels = canvas,
                Image = WpfImages.ToBitmap(canvas),
                Metadata = $"{entry.Name}\n{epf.Frames.Count} frames in a {columns}x{rows} sheet",
                FrameCount = epf.Frames.Count,
                CanAnimate = false
            };
        }
        catch (Exception ex)
        {
            return new PreviewResult { Error = ex.Message };
        }
    }

    public static PreviewResult MpfSheet(IArchive archive, ArchiveEntry entry, Palette? palette, string group)
    {
        var mpf = new MpfFile(entry);
        Palette pal = palette ?? PaletteLocator.FindNumbered(archive, "mns", mpf.PaletteNumber) ?? PaletteLocator.Find(archive) ?? FallbackPalette();
        if (mpf.Frames.Count == 0)
        {
            return new PreviewResult { Error = "MPF has no frames." };
        }

        IReadOnlyList<int> range = MotionRange(mpf, group);
        if (range.Count == 0)
        {
            range = Enumerable.Range(0, mpf.Frames.Count).ToList();
        }

        int cellW = Math.Max(1, mpf.Width);
        int cellH = Math.Max(1, mpf.Height);
        foreach (int idx in range)
        {
            MpfFrame frame = mpf.Frames[idx];
            cellW = Math.Max(cellW, frame.Left + frame.Width);
            cellH = Math.Max(cellH, frame.Top + frame.Height);
        }

        int columns = Math.Min(8, Math.Max(1, range.Count));
        int rows = Math.Max(1, (int)Math.Ceiling(range.Count / (double)columns));
        var canvas = new PixelBuffer(columns * cellW, rows * cellH);
        canvas.Clear(28, 28, 32, 255);
        for (int i = 0; i < range.Count; i++)
        {
            MpfFrame frame = mpf.Frames[range[i]];
            PixelBuffer img = frame.Render(pal);
            int col = i % columns;
            int row = i / columns;
            canvas.Blit(img, col * cellW + frame.Left, row * cellH + frame.Top);
        }

        return new PreviewResult
        {
            Pixels = canvas,
            Image = WpfImages.ToBitmap(canvas),
            Metadata = $"{entry.Name}\n{range.Count} frames ({group})\n{MpfPaletteNote(mpf)}\nIdle {mpf.StopFrameIndex}+{mpf.StopFrameCount}  Walk {mpf.WalkFrameIndex}+{mpf.WalkFrameCount}\nAttack {mpf.AttackFrameIndex}+{mpf.AttackFrameCount}",
            FrameCount = range.Count,
            CanAnimate = false
        };
    }

    public static PreviewResult SpfContact(IArchive archive, ArchiveEntry entry, Palette? palette)
    {
        IReadOnlyList<ArchiveEntry> related = RelatedFiles.Find(archive, entry);
        if (related.Count > 1)
        {
            return RelatedSliceSheet(archive, related, palette);
        }

        return SpfSheet(archive, entry, palette);
    }

    public static PreviewResult HpfRelated(IArchive archive, ArchiveEntry entry, Palette? palette)
    {
        IReadOnlyList<ArchiveEntry> related = RelatedFiles.Find(archive, entry);
        if (related.Count > 1)
        {
            return RelatedSliceSheet(archive, related, palette);
        }

        return FromHpf(archive, entry, palette);
    }

    public static PreviewResult RelatedSliceSheet(IArchive archive, IReadOnlyList<ArchiveEntry> related, Palette? palette)
    {
        palette ??= PaletteLocator.Find(archive) ?? FallbackPalette();
        var images = new List<PixelBuffer>();
        foreach (ArchiveEntry piece in related)
        {
            try
            {
                string ext = piece.Extension.ToLowerInvariant();
                PixelBuffer image = ext switch
                {
                    ".spf" => AssembleSpf(new SpfFile(piece), palette),
                    ".hpf" => new HpfFile(piece).Render(palette),
                    ".pcx" => new PcxFile(piece).Image,
                    ".epf" => AssembleEpf(new EpfFile(piece), PaletteForItemFrame(archive, piece, 0, palette)),
                    _ => RenderTile(piece, palette)
                };
                images.Add(image);
            }
            catch
            {
                // skip
            }
        }

        if (images.Count == 0)
        {
            return new PreviewResult { Error = "Could not assemble related slices." };
        }

        int cellW = images.Max(img => img.Width);
        int cellH = images.Max(img => img.Height);
        int columns = images.Count <= 8 ? images.Count : Math.Min(8, images.Count);
        int rows = Math.Max(1, (int)Math.Ceiling(images.Count / (double)columns));
        var canvas = new PixelBuffer(columns * cellW, rows * cellH);
        canvas.Clear(28, 28, 32, 255);
        for (int i = 0; i < images.Count; i++)
        {
            int col = i % columns;
            int row = i / columns;
            canvas.Blit(images[i], col * cellW, row * cellH);
        }

        return new PreviewResult
        {
            Pixels = canvas,
            Image = WpfImages.ToBitmap(canvas),
            Metadata = $"{related.Count} related slices assembled\n{string.Join(", ", related.Select(r => r.Name).Take(12))}",
            FrameCount = related.Count
        };
    }

    public static PreviewResult SpfSheet(IArchive archive, ArchiveEntry entry, Palette? palette)
    {
        var spf = new SpfFile(entry);
        if (spf.Frames.Count == 0)
        {
            return new PreviewResult { Error = "SPF has no frames." };
        }

        if (LooksLikeSlices(spf.Frames.Select(f => (f.Left, f.Top, f.Width, f.Height)).ToList()))
        {
            PixelBuffer assembled = AssembleSpf(spf, palette);
            return new PreviewResult
            {
                Pixels = assembled,
                Image = WpfImages.Checkerboard(assembled.Width, assembled.Height, assembled),
                Metadata = $"{entry.Name}\nAssembled {spf.Frames.Count} SPF slices  {assembled.Width}x{assembled.Height}",
                FrameCount = spf.Frames.Count
            };
        }

        int cellW = Math.Max(32, spf.Frames.Max(f => f.Width + f.Left));
        int cellH = Math.Max(32, spf.Frames.Max(f => f.Height + f.Top));
        int columns = Math.Min(8, spf.Frames.Count);
        int rows = Math.Max(1, (int)Math.Ceiling(spf.Frames.Count / (double)columns));
        var canvas = new PixelBuffer(columns * cellW, rows * cellH);
        canvas.Clear(28, 28, 32, 255);
        for (int i = 0; i < spf.Frames.Count; i++)
        {
            PixelBuffer img = spf.Frames[i].Render(spf, palette);
            int col = i % columns;
            int row = i / columns;
            canvas.Blit(img, col * cellW + spf.Frames[i].Left, row * cellH + spf.Frames[i].Top);
        }

        return new PreviewResult
        {
            Pixels = canvas,
            Image = WpfImages.ToBitmap(canvas),
            Metadata = $"{entry.Name}\n{spf.Frames.Count} SPF frames ({spf.Format})",
            FrameCount = spf.Frames.Count
        };
    }

    public static PreviewResult EfaSheet(ArchiveEntry entry)
    {
        var efa = new EfaFile(entry);
        if (efa.Frames.Count == 0)
        {
            return new PreviewResult { Error = "EFA has no frames." };
        }

        int cellW = Math.Max(32, efa.Frames.Max(f => f.Render().Width));
        int cellH = Math.Max(32, efa.Frames.Max(f => f.Render().Height));
        int columns = Math.Min(8, efa.Frames.Count);
        int rows = Math.Max(1, (int)Math.Ceiling(efa.Frames.Count / (double)columns));
        var canvas = new PixelBuffer(columns * cellW, rows * cellH);
        canvas.Clear(28, 28, 32, 255);
        for (int i = 0; i < efa.Frames.Count; i++)
        {
            PixelBuffer img = efa.Frames[i].Render();
            int col = i % columns;
            int row = i / columns;
            canvas.Blit(img, col * cellW, row * cellH);
        }

        return new PreviewResult
        {
            Pixels = canvas,
            Image = WpfImages.ToBitmap(canvas),
            Metadata = $"{entry.Name}\n{efa.Frames.Count} EFA frames",
            FrameCount = efa.Frames.Count
        };
    }

    public static PreviewResult EntryGrid(IArchive archive, IReadOnlyList<ArchiveEntry> entries, string? paletteOverride)
    {
        Palette? palette = ResolvePalette(archive, entries[0], paletteOverride);
        const int cell = 48;
        int columns = Math.Min(12, Math.Max(1, entries.Count));
        int rows = Math.Max(1, (int)Math.Ceiling(entries.Count / (double)columns));
        var canvas = new PixelBuffer(columns * cell, rows * cell);
        canvas.Clear(28, 28, 32, 255);
        int shown = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            try
            {
                PreviewResult one = Build(archive, entries[i], 0, paletteOverride, "All", false, null, contactSheet: false);
                if (one.Pixels is null)
                {
                    continue;
                }

                int col = i % columns;
                int row = i / columns;
                int x = col * cell + Math.Max(0, (cell - one.Pixels.Width) / 2);
                int y = row * cell + Math.Max(0, (cell - one.Pixels.Height) / 2);
                canvas.Blit(one.Pixels, x, y);
                shown++;
            }
            catch
            {
                // skip
            }
        }

        return new PreviewResult
        {
            Pixels = canvas,
            Image = WpfImages.ToBitmap(canvas),
            Metadata = $"{shown} files in a contact sheet",
            FrameCount = 1
        };
    }

    private static PreviewResult FromEpf(IArchive archive, ArchiveEntry entry, Palette? palette, int frameIndex, bool additive, IReadOnlyList<ArchiveEntry>? overlays)
    {
        if (KhanPieces.IsKhanArchive(archive) && KhanPieces.TryParse(entry.Name, out KhanPieceInfo khanInfo))
        {
            List<PixelBuffer> frames = ExtractKhan(archive, entry, palette ?? FallbackPalette(), int.MaxValue, additive, overlays);
            if (frames.Count == 0)
            {
                return new PreviewResult { Error = "EPF has no frames." };
            }

            int index = Math.Clamp(frameIndex, 0, frames.Count - 1);
            PixelBuffer canvas = frames[index];
            string layer = KhanPieces.IsFigureSlot(khanInfo) ? "body" : $"body + {khanInfo.Gender}{khanInfo.Slot}{khanInfo.Id:D3}";
            return new PreviewResult
            {
                Pixels = canvas,
                Image = WpfImages.Checkerboard(canvas.Width, canvas.Height, canvas),
                Metadata = $"{entry.Name}\n{layer}  motion {khanInfo.Motion}\nFrame {index + 1}/{frames.Count}",
                FrameCount = frames.Count,
                CanAnimate = frames.Count > 1
            };
        }

        var epf = new EpfFile(entry);
        if (epf.Frames.Count == 0)
        {
            return new PreviewResult { Error = "EPF has no frames." };
        }

        bool itemTile = entry.FrameIndex is not null;
        int rawIndex = Math.Clamp(entry.FrameIndex ?? frameIndex, 0, epf.Frames.Count - 1);
        EpfFrame frame = epf.Frames[rawIndex];
        Palette framePal = PaletteForItemFrame(archive, entry, rawIndex, palette ?? FallbackPalette());
        PaletteBlit blit = additive ? PaletteBlit.Additive : PaletteLocator.BlitFor(archive, entry);
        PixelBuffer frameImage = frame.Render(framePal, blit);
        int w = Math.Max(Math.Max(1, epf.Width), frameImage.Width + frame.Left);
        int h = Math.Max(Math.Max(1, epf.Height), frameImage.Height + frame.Top);
        if (overlays is not { Count: > 0 } || itemTile)
        {
            w = Math.Max(1, frameImage.Width);
            h = Math.Max(1, frameImage.Height);
        }

        var rawCanvas = new PixelBuffer(w, h);
        rawCanvas.Blit(frameImage, overlays is { Count: > 0 } && !itemTile ? frame.Left : 0, overlays is { Count: > 0 } && !itemTile ? frame.Top : 0);

        if (overlays is { Count: > 0 })
        {
            foreach (ArchiveEntry overlay in overlays)
            {
                try
                {
                    var o = new EpfFile(overlay);
                    if (o.Frames.Count == 0)
                    {
                        continue;
                    }

                    EpfFrame of = o.Frames[Math.Min(rawIndex, o.Frames.Count - 1)];
                    rawCanvas.Blit(of.Render(framePal, blit), of.Left, of.Top);
                }
                catch
                {
                    // skip bad overlay
                }
            }
        }

        return new PreviewResult
        {
            Pixels = rawCanvas,
            Image = WpfImages.Checkerboard(w, h, rawCanvas),
            Metadata = itemTile
                ? $"{entry.DisplayName}\n{entry.Name}  tile {rawIndex + 1}\n{frame.Width}x{frame.Height}"
                : $"{entry.Name}\nFrame {rawIndex + 1}/{epf.Frames.Count}\nCanvas {epf.Width}x{epf.Height}\nFrame {frame.Width}x{frame.Height} at ({frame.Left},{frame.Top})",
            FrameCount = itemTile ? 1 : epf.Frames.Count,
            CanAnimate = !itemTile && epf.Frames.Count > 1
        };
    }

    private static PreviewResult FromMpf(IArchive archive, ArchiveEntry entry, Palette? palette, int frameIndex, string group)
    {
        var mpf = new MpfFile(entry);
        Palette? pal = palette ?? PaletteLocator.FindNumbered(archive, "mns", mpf.PaletteNumber) ?? PaletteLocator.Find(archive) ?? FallbackPalette();
        if (mpf.Frames.Count == 0)
        {
            return new PreviewResult { Error = "MPF has no frames." };
        }

        IReadOnlyList<int> range = MotionRange(mpf, group);
        int local = range.Count == 0 ? 0 : range[Math.Clamp(frameIndex, 0, range.Count - 1)];
        local = Math.Clamp(local, 0, mpf.Frames.Count - 1);
        MpfFrame frame = mpf.Frames[local];
        int w = Math.Max(1, mpf.Width);
        int h = Math.Max(1, mpf.Height);
        var canvas = new PixelBuffer(w, h);
        canvas.Blit(frame.Render(pal), frame.Left, frame.Top);
        return new PreviewResult
        {
            Pixels = canvas,
            Image = WpfImages.Checkerboard(w, h, canvas),
            Metadata =
                $"{entry.Name}\n{MpfPaletteNote(mpf)}\n{MonsterSpriteNote(entry)}\nFrame {local + 1}/{mpf.Frames.Count} ({group})\nIdle {mpf.StopFrameIndex}+{mpf.StopFrameCount}\nWalk {mpf.WalkFrameIndex}+{mpf.WalkFrameCount}\nAttack {mpf.AttackFrameIndex}+{mpf.AttackFrameCount}\nAtk2 {mpf.Attack2FrameIndex}+{mpf.Attack2FrameCount}\nAtk3 {mpf.Attack3FrameIndex}+{mpf.Attack3FrameCount}",
            FrameCount = Math.Max(1, range.Count),
            CanAnimate = range.Count > 1
        };
    }

    private static PreviewResult FromSpf(IArchive archive, ArchiveEntry entry, Palette? palette, int frameIndex)
    {
        var spf = new SpfFile(entry);
        if (spf.Frames.Count == 0)
        {
            return new PreviewResult { Error = "SPF has no frames." };
        }

        int index = Math.Clamp(frameIndex, 0, spf.Frames.Count - 1);
        SpfFrame frame = spf.Frames[index];
        PixelBuffer img = frame.Render(spf, palette);
        int w = Math.Max(img.Width + frame.Left, img.Width);
        int h = Math.Max(img.Height + frame.Top, img.Height);
        var canvas = new PixelBuffer(Math.Max(1, w), Math.Max(1, h));
        canvas.Blit(img, frame.Left, frame.Top);
        return new PreviewResult
        {
            Pixels = canvas,
            Image = WpfImages.Checkerboard(canvas.Width, canvas.Height, canvas),
            Metadata = $"{entry.Name}\nSPF {spf.Format}  frame {index + 1}/{spf.Frames.Count}\n{frame.Width}x{frame.Height} at ({frame.Left},{frame.Top})",
            FrameCount = spf.Frames.Count,
            CanAnimate = spf.Frames.Count > 1
        };
    }

    private static PreviewResult FromFloorAtlas(IArchive archive, ArchiveEntry entry, Palette? palette)
    {
        string name = Path.GetFileName(entry.Name);
        if (!name.Equals("TILEA.BMP", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("TILEAS.BMP", StringComparison.OrdinalIgnoreCase))
        {
            return FromUnknown(entry);
        }

        int count = FloorTiles.Count(entry.Size);
        if (count <= 0)
        {
            return new PreviewResult { Error = "TILEA.BMP is empty." };
        }

        if (entry.FrameIndex is int frame)
        {
            int index = Math.Clamp(frame, 0, count - 1);
            int tileId = index + 1;
            bool snowTile = name.Equals("TILEAS.BMP", StringComparison.OrdinalIgnoreCase);
            Palette pal = palette
                ?? (snowTile ? PaletteLocator.ForSnowFloor(archive) : PaletteLocator.ForFloorTile(archive, tileId))
                ?? FallbackPalette();
            byte[] data = FloorTiles.ReadTile(entry, index);
            PixelBuffer img = pal.Render(data, FloorTiles.TileWidth, FloorTiles.TileHeight);
            return new PreviewResult
            {
                Pixels = img,
                Image = WpfImages.Checkerboard(img.Width, img.Height, img),
                Metadata = $"{entry.DisplayName}\n{name} tile {tileId}/{count}\n{FloorTiles.TileWidth}x{FloorTiles.TileHeight}  palette {paletteOverrideName(archive, pal)}",
                FrameCount = 1
            };
        }

        int shown = Math.Min(count, 266);
        int columns = 19;
        int rows = (int)Math.Ceiling(shown / (double)columns);
        var canvas = new PixelBuffer(columns * FloorTiles.TileWidth, rows * FloorTiles.TileHeight);
        canvas.Clear(28, 28, 32, 255);
        bool snowSheet = name.Equals("TILEAS.BMP", StringComparison.OrdinalIgnoreCase);
        Palette? snowPal = snowSheet ? PaletteLocator.ForSnowFloor(archive) : null;
        for (int i = 0; i < shown; i++)
        {
            Palette pal = snowPal ?? PaletteLocator.ForFloorTile(archive, i + 1) ?? palette ?? FallbackPalette();
            PixelBuffer img = pal.Render(FloorTiles.ReadTile(entry, i), FloorTiles.TileWidth, FloorTiles.TileHeight);
            canvas.Blit(img, (i % columns) * FloorTiles.TileWidth, (i / columns) * FloorTiles.TileHeight);
        }

        return new PreviewResult
        {
            Pixels = canvas,
            Image = WpfImages.ToBitmap(canvas),
            Metadata = $"{name}\n{count} packed 56×27 floor tiles (seo.dat). Showing first {shown}.\nMap layouts (.map) are not stored in DAT files.",
            FrameCount = 1
        };
    }

    private static PreviewResult FromHpf(IArchive archive, ArchiveEntry entry, Palette? palette)
    {
        palette ??= PaletteLocator.Find(archive) ?? FallbackPalette();
        var hpf = new HpfFile(entry);
        PixelBuffer img = hpf.Render(palette);
        return new PreviewResult
        {
            Pixels = img,
            Image = WpfImages.Checkerboard(img.Width, img.Height, img),
            Metadata = $"{entry.Name}\nHPF {hpf.Width}x{hpf.Height}",
            FrameCount = 1
        };
    }

    private static PreviewResult FromEfa(ArchiveEntry entry, int frameIndex)
    {
        var efa = new EfaFile(entry);
        if (efa.Frames.Count == 0)
        {
            return new PreviewResult { Error = "EFA has no frames." };
        }

        int index = Math.Clamp(frameIndex, 0, efa.Frames.Count - 1);
        EfaFrame frame = efa.Frames[index];
        PixelBuffer img = frame.Render(efa.Blend);
        short ox = frame.Header.OriginX != 0 ? frame.Header.OriginX : EffectCenterTable.DefaultX;
        short oy = frame.Header.OriginY != 0 ? frame.Header.OriginY : EffectCenterTable.DefaultY;
        int canvasW = Math.Max(img.Width + Math.Abs((int)ox), img.Width);
        int canvasH = Math.Max(img.Height + Math.Abs((int)oy), img.Height);
        var canvas = new PixelBuffer(canvasW, canvasH);
        int dx = Math.Max(0, (int)ox);
        int dy = Math.Max(0, (int)oy);
        if (EfaBlendRules.AdditiveBlit(efa.Blend))
        {
            canvas.BlitAdd(img, dx, dy);
        }
        else
        {
            canvas.Blit(img, dx, dy);
        }

        return new PreviewResult
        {
            Pixels = canvas,
            Image = WpfImages.Checkerboard(canvas.Width, canvas.Height, canvas),
            Metadata =
                $"{entry.Name}\nEFA frame {index + 1}/{efa.Frames.Count}  {img.Width}x{img.Height}\n" +
                $"blend {efa.Blend}  origin ({ox},{oy})",
            FrameCount = efa.Frames.Count,
            CanAnimate = efa.Frames.Count > 1
        };
    }

    private static PreviewResult FromPal(ArchiveEntry entry)
    {
        var pal = new Palette(entry);
        PixelBuffer img = pal.RenderSwatch();
        return new PreviewResult
        {
            Pixels = img,
            Image = WpfImages.ToBitmap(img),
            Metadata = $"{entry.Name}\n256-color palette",
            FrameCount = 1
        };
    }

    private static PreviewResult FromRgba(ArchiveEntry entry, int frameIndex = 0)
    {
        IReadOnlyList<RgbaImage> frames = RgbaImage.DecodeAll(entry.ReadAllBytes());
        if (frames.Count == 0)
        {
            return FromUnknown(entry);
        }

        int index = Math.Clamp(frameIndex, 0, frames.Count - 1);
        RgbaImage image = frames[index];
        string anchor = image.AnchorX != 0 || image.AnchorY != 0
            ? $"  anchor ({image.AnchorX},{image.AnchorY})"
            : "";
        string frameMeta = frames.Count > 1 ? $"  {frames.Count} frames" : "";
        return new PreviewResult
        {
            Pixels = image.Pixels,
            Image = WpfImages.ToBitmap(image.Pixels),
            Metadata = $"{entry.DisplayName}\n{image.Width}×{image.Height}  Legends AoC RGBA{anchor}{frameMeta}",
            FrameCount = frames.Count,
            CanAnimate = frames.Count > 1
        };
    }

    private static IReadOnlyList<PixelBuffer> RgbaFrames(ArchiveEntry entry, int maxFrames = int.MaxValue)
    {
        return RgbaImage.DecodeAll(entry.ReadAllBytes(), maxFrames).Select(f => f.Pixels).ToList();
    }

    private static PreviewResult FromEncodedImage(ArchiveEntry entry)
    {
        byte[] data = entry.ReadAllBytes();
        PixelBuffer? pixels = WpfImages.TryDecodeImage(data);
        if (pixels is null)
        {
            return FromUnknown(entry);
        }

        return new PreviewResult
        {
            Pixels = pixels,
            Image = WpfImages.ToBitmap(pixels),
            Metadata = $"{entry.Name}\n{pixels.Width}×{pixels.Height}  {(JpfFile.LooksLike(data) ? "JPEG" : "image")}",
            FrameCount = 1
        };
    }

    private static PreviewResult FromHea(ArchiveEntry entry)
    {
        var hea = new HeaFile(entry);
        if (!hea.IsValid)
        {
            return FromUnknown(entry);
        }

        bool mapLight = HeaLights.LooksLikeMapLight(hea, entry.Name);
        PixelBuffer? pixels = hea.TryRender();
        string kind = mapLight
            ? "map light sheet"
            : HeaLights.LooksLikeArtwork(hea)
                ? "Fragments of Temuair packed artwork"
                : "HEA container";
        string meta =
            $"{entry.Name}\nHEA {hea.Width}×{hea.Height}  dest {hea.DestWidth}×{hea.DestHeight}\n" +
            $"Tiles {hea.TileWidth}×{hea.TileHeight}  atlas {hea.AtlasWidth}×{hea.AtlasHeight}  layers {hea.LayerCount}\n" +
            $"{hea.ByteLength} bytes ({kind})";
        if (pixels is null)
        {
            return new PreviewResult
            {
                Metadata = meta,
                Hex = $"{entry.Name}  {hea.ByteLength} bytes HEA container",
                FrameCount = 1
            };
        }

        return new PreviewResult
        {
            Pixels = pixels,
            Image = WpfImages.ToBitmap(pixels),
            Metadata = meta,
            FrameCount = 1
        };
    }

    private static PreviewResult FromBink(ArchiveEntry entry, int frameIndex)
    {
        byte[] data = entry.ReadAllBytes();
        if (!BinkFile.TryParse(data, out BinkInfo info))
        {
            return FromUnknown(entry);
        }

        string meta = $"{entry.Name}\n{info.Describe()}\n{data.Length} bytes";
        if (info.IsBink2)
        {
            return new PreviewResult
            {
                Metadata = meta,
                Error = "Bink 2 (KB2) video is not decoded by FFmpeg. Dark Ages cinematics are Bink 1.",
                FrameCount = Math.Max(1, info.FrameCount)
            };
        }

        if (FFmpegLocator.Find() is null)
        {
            return new PreviewResult
            {
                Metadata = meta,
                Error = BinkDecoder.InstallHint,
                FrameCount = Math.Max(1, info.FrameCount)
            };
        }

        try
        {
            BinkClip clip = BinkDecoder.Decode(entry, info, data);
            int index = Math.Clamp(frameIndex, 0, clip.FrameCount - 1);
            PixelBuffer pixels = clip.ReadFrame(index);
            return new PreviewResult
            {
                Pixels = pixels,
                Image = WpfImages.ToBitmap(pixels),
                Metadata = $"{meta}\nDecoded with FFmpeg",
                FrameCount = clip.FrameCount,
                CanAnimate = clip.FrameCount > 1,
                FrameDelayMs = clip.FrameDelayMs,
                FramesPerSecond = clip.FramesPerSecond,
                Audio = clip.Wav,
                AudioName = clip.Wav is null ? null : "cinematic.wav",
                Cinematic = clip
            };
        }
        catch (Exception ex)
        {
            return new PreviewResult
            {
                Metadata = meta,
                Error = ex.Message,
                FrameCount = Math.Max(1, info.FrameCount)
            };
        }
    }

    private static List<PixelBuffer> ExtractBink(ArchiveEntry entry, int maxFrames)
    {
        byte[] data = entry.ReadAllBytes();
        if (!BinkFile.TryParse(data, out BinkInfo info) || info.IsBink2)
        {
            return [];
        }

        try
        {
            BinkClip clip = BinkDecoder.Decode(entry, info, data);
            int count = maxFrames == int.MaxValue ? 1 : Math.Clamp(maxFrames, 1, clip.FrameCount);
            var frames = new List<PixelBuffer>(count);
            for (int i = 0; i < count; i++)
            {
                frames.Add(clip.ReadFrame(i));
            }

            return frames;
        }
        catch
        {
            return [];
        }
    }

    private static PreviewResult FromFnt(ArchiveEntry entry, int frameIndex, bool contactSheet)
    {
        var font = FntFile.Load(entry);
        if (contactSheet)
        {
            PixelBuffer sheet = font.Sheet();
            return new PreviewResult
            {
                Pixels = sheet,
                Image = WpfImages.ToBitmap(sheet),
                Metadata = $"{entry.Name}\n{font.Kind} FNT  {font.Count} glyphs  {font.GlyphWidth}×{font.GlyphHeight}  1-bit",
                FrameCount = font.Count,
                CanAnimate = false
            };
        }

        int index = Math.Clamp(frameIndex, 0, Math.Max(0, font.Count - 1));
        PixelBuffer glyph = font.Render(index);
        string label = font.Kind == FntKind.English && index < FntFile.EnglishGlyphCount
            ? $"U+{(FntFile.EnglishFirstChar + index):X2} '{(char)(FntFile.EnglishFirstChar + index)}'"
            : $"glyph {index}";
        return new PreviewResult
        {
            Pixels = glyph,
            Image = WpfImages.Checkerboard(glyph.Width, glyph.Height, glyph),
            Metadata = $"{entry.Name}\n{font.Kind} FNT  {label}\n{font.GlyphWidth}×{font.GlyphHeight}  1-bit",
            FrameCount = font.Count,
            CanAnimate = font.Count > 1
        };
    }

    private static PreviewResult FromLft(ArchiveEntry entry, int frameIndex, bool contactSheet)
    {
        var font = LftFile.Load(entry);
        if (contactSheet)
        {
            PixelBuffer sample = font.RenderText("Dark Ages 0123456789");
            return new PreviewResult
            {
                Pixels = sample,
                Image = WpfImages.ToBitmap(sample),
                Metadata = $"{entry.Name}\nLFT {font.NominalWidth}×{font.NominalHeight}  {LftFile.GlyphKeys} keys\nDecoded ANSI sample. Graphic editor paints keys 0–255.",
                FrameCount = 256,
                CanAnimate = false
            };
        }

        int key = Math.Clamp(frameIndex, 0, 255);
        PixelBuffer glyph = font.Render(key);
        return new PreviewResult
        {
            Pixels = glyph,
            Image = WpfImages.Checkerboard(glyph.Width, glyph.Height, glyph),
            Metadata = $"{entry.Name}\nLFT glyph 0x{key:X2}  advance {font.Advance(key)}\ncell {font.NominalWidth}×{font.NominalHeight}",
            FrameCount = 256,
            CanAnimate = true
        };
    }

    private static PreviewResult FromUpf(IArchive archive, ArchiveEntry entry, Palette? palette, int frameIndex, bool contactSheet)
    {
        var upf = UpfFile.Load(entry);
        palette ??= PaletteLocator.Find(archive, "gui000.pal", "gui06.pal", "legend.pal") ?? FallbackPalette();
        if (contactSheet)
        {
            PixelBuffer sheet = upf.Sheet(palette);
            return new PreviewResult
            {
                Pixels = sheet,
                Image = WpfImages.ToBitmap(sheet),
                Metadata = $"{entry.Name}\nUPF dialog chrome  {upf.Count} tiles  {upf.Width}×{upf.Height}",
                FrameCount = Math.Max(1, upf.Count),
                CanAnimate = false
            };
        }

        int index = Math.Clamp(frameIndex, 0, Math.Max(0, upf.Count - 1));
        PixelBuffer tile = upf.Render(index, palette);
        return new PreviewResult
        {
            Pixels = tile,
            Image = WpfImages.Checkerboard(tile.Width, tile.Height, tile),
            Metadata = $"{entry.Name}\nUPF frame {index + 1}/{upf.Count}  {upf.Width}×{upf.Height}",
            FrameCount = Math.Max(1, upf.Count),
            CanAnimate = upf.Count > 1
        };
    }

    private static List<PixelBuffer> ExtractFnt(ArchiveEntry entry, int maxFrames)
    {
        var font = FntFile.Load(entry);
        int n = Math.Min(font.Count, Math.Max(1, maxFrames == int.MaxValue ? font.Count : maxFrames));
        var frames = new List<PixelBuffer>(n);
        for (int i = 0; i < n; i++)
        {
            frames.Add(font.Render(i));
        }

        return frames;
    }

    private static List<PixelBuffer> ExtractLft(ArchiveEntry entry, int maxFrames)
    {
        var font = LftFile.Load(entry);
        int n = Math.Min(256, Math.Max(1, maxFrames == int.MaxValue ? 96 : maxFrames));
        var frames = new List<PixelBuffer>(n);
        for (int i = 0; i < n; i++)
        {
            frames.Add(font.Render(i));
        }

        return frames;
    }

    private static List<PixelBuffer> ExtractUpf(IArchive archive, ArchiveEntry entry, int maxFrames, string? paletteOverride)
    {
        var upf = UpfFile.Load(entry);
        Palette pal = ResolvePalette(archive, entry, paletteOverride) ?? FallbackPalette();
        int n = Math.Min(upf.Count, Math.Max(1, maxFrames == int.MaxValue ? upf.Count : maxFrames));
        var frames = new List<PixelBuffer>(n);
        for (int i = 0; i < n; i++)
        {
            frames.Add(upf.Render(i, pal));
        }

        return frames;
    }

    private static List<PixelBuffer> ExtractHea(ArchiveEntry entry)
    {
        PixelBuffer? pixels = new HeaFile(entry).TryRender();
        return pixels is null ? [] : [pixels];
    }

    private static PreviewResult FromPcx(ArchiveEntry entry)
    {
        var pcx = new PcxFile(entry);
        return new PreviewResult
        {
            Pixels = pcx.Image,
            Image = WpfImages.ToBitmap(pcx.Image),
            Metadata = $"{entry.Name}\nPCX {pcx.Width}x{pcx.Height}",
            FrameCount = 1
        };
    }

    private static PreviewResult FromTbl(ArchiveEntry entry)
    {
        var table = new PaletteTable();
        table.AddTable(entry);
        var sb = new StringBuilder();
        sb.AppendLine(entry.Name);
        sb.AppendLine($"{table.Entries.Count} ranges");
        foreach (PaletteTableEntry row in table.Entries.Take(40))
        {
            sb.AppendLine($"{row.Minimum}-{row.Maximum} -> pal {row.PaletteNumber} misc {row.Misc}");
        }

        return new PreviewResult { Metadata = sb.ToString(), Hex = sb.ToString(), FrameCount = 1 };
    }

    private static PreviewResult FromAudio(ArchiveEntry entry)
    {
        byte[] data = entry.ReadAllBytes();
        return new PreviewResult
        {
            Audio = data,
            AudioName = entry.Name,
            Metadata = $"{entry.Name}\n{data.Length} bytes audio",
            FrameCount = 1
        };
    }

    private static PreviewResult FromUnknown(ArchiveEntry entry)
    {
        byte[] data = entry.ReadAllBytes();
        int n = Math.Min(512, data.Length);
        var sb = new StringBuilder();
        sb.AppendLine($"{entry.Name}  {data.Length} bytes");
        for (int i = 0; i < n; i += 16)
        {
            sb.Append($"{i:X4}  ");
            int len = Math.Min(16, n - i);
            for (int j = 0; j < len; j++)
            {
                sb.Append($"{data[i + j]:X2} ");
            }

            sb.AppendLine();
        }

        return new PreviewResult { Hex = sb.ToString(), Metadata = $"{entry.Name}\nUnknown type — hex dump", FrameCount = 1 };
    }

    private static Palette? ResolvePalette(IArchive archive, ArchiveEntry entry, string? paletteOverride)
    {
        Palette? named = PaletteLocator.Named(archive, paletteOverride);
        if (named is not null)
        {
            return named;
        }

        return PaletteLocator.ForEntry(archive, entry);
    }

    public static IReadOnlyList<PixelBuffer> ExtractFrames(
        IArchive archive,
        ArchiveEntry entry,
        int maxFrames = int.MaxValue,
        string? paletteOverride = null,
        bool padToSheet = true)
    {
        Palette palette = ResolvePalette(archive, entry, paletteOverride) ?? FallbackPalette();
        string ext = entry.Extension.ToLowerInvariant();
        try
        {
            return ext switch
            {
                ".epf" => KhanPieces.IsKhanArchive(archive) && KhanPieces.TryParse(entry.Name, out _)
                    ? ExtractKhan(archive, entry, palette, maxFrames, additive: false, paletteOverride: paletteOverride)
                    : ExtractEpf(archive, entry, palette, maxFrames, padToSheet),
                ".mpf" => ExtractMpf(archive, entry, palette, maxFrames, padToSheet),
                ".spf" => ExtractSpf(entry, palette, maxFrames),
                ".hpf" => [new HpfFile(entry).Render(palette)],
                ".efa" => new EfaFile(entry, maxFrames).Frames.Take(Math.Max(1, maxFrames)).Select(f => f.Render()).ToList(),
                ".pcx" => [new PcxFile(entry).Image],
                ".fnt" => ExtractFnt(entry, maxFrames),
                ".lft" => ExtractLft(entry, maxFrames),
                ".upf" => ExtractUpf(archive, entry, maxFrames, paletteOverride),
                ".hea" => ExtractHea(entry),
                ".bmp" => ExtractFloorTile(archive, entry, paletteOverride),
                ".jpf" or ".jpg" or ".jpeg" or ".png" => EncodedImageFrames(entry),
                ".rgba" => RgbaFrames(entry, maxFrames),
                ".bik" => ExtractBink(entry, maxFrames),
                _ => []
            };
        }
        catch
        {
            return [];
        }
    }

    private static PreviewResult KhanSheet(IArchive archive, ArchiveEntry entry, Palette? palette, bool additive)
    {
        List<PixelBuffer> frames = ExtractKhan(archive, entry, palette ?? FallbackPalette(), int.MaxValue, additive);
        if (frames.Count == 0)
        {
            return new PreviewResult { Error = "EPF has no frames." };
        }

        int cellW = Math.Max(1, frames.Max(f => f.Width));
        int cellH = Math.Max(1, frames.Max(f => f.Height));
        int columns = Math.Min(8, Math.Max(1, frames.Count));
        int rows = Math.Max(1, (int)Math.Ceiling(frames.Count / (double)columns));
        var canvas = new PixelBuffer(columns * cellW, rows * cellH);
        canvas.Clear(28, 28, 32, 255);
        for (int i = 0; i < frames.Count; i++)
        {
            int col = i % columns;
            int row = i / columns;
            canvas.Blit(
                frames[i],
                col * cellW + Math.Max(0, (cellW - frames[i].Width) / 2),
                row * cellH + Math.Max(0, (cellH - frames[i].Height) / 2));
        }

        KhanPieces.TryParse(entry.Name, out KhanPieceInfo info);
        return new PreviewResult
        {
            Pixels = canvas,
            Image = WpfImages.ToBitmap(canvas),
            Metadata = $"{entry.Name}\nComposited on body  {info.Gender}{info.Slot}{info.Id:D3}  {frames.Count} directions",
            FrameCount = frames.Count,
            CanAnimate = false
        };
    }

    private static List<PixelBuffer> ExtractKhan(
        IArchive archive,
        ArchiveEntry entry,
        Palette palette,
        int maxFrames,
        bool additive,
        IReadOnlyList<ArchiveEntry>? extraOverlays = null,
        string? paletteOverride = null)
    {
        if (!KhanPieces.TryParse(entry.Name, out KhanPieceInfo info))
        {
            return ExtractEpf(archive, entry, palette, maxFrames);
        }

        var piece = new EpfFile(entry);
        EpfFile? body = null;
        ArchiveEntry? bodyEntry = null;
        if (!KhanPieces.IsFigureSlot(info))
        {
            bodyEntry = KhanPieces.BodyFor(archive, info);
            if (bodyEntry is not null && !bodyEntry.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    body = new EpfFile(bodyEntry);
                }
                catch
                {
                    body = null;
                }
            }
        }

        int count = piece.Frames.Count;
        if (body is { Frames.Count: > 0 })
        {
            count = Math.Max(count, body.Frames.Count);
        }

        if (count == 0)
        {
            return [];
        }

        bool thumb = maxFrames <= 1;
        int start = 0;
        if (thumb)
        {
            start = Math.Min(KhanPieces.IdleDirection, count - 1);
            count = 1;
            if (!KhanPieces.IsFigureSlot(info) && piece.Frames.Count > 0)
            {
                int overlayIndex = KhanPieces.FrameIndex(piece.Frames.Count, start);
                EpfFrame overlayFrame = piece.Frames[overlayIndex];
                if (!overlayFrame.Render(palette, additive).HasVisiblePixels())
                {
                    return [];
                }
            }
        }
        else
        {
            count = Math.Min(count, maxFrames);
        }

        var overlays = extraOverlays?
            .Where(o =>
                !o.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase) &&
                (bodyEntry is null || !o.Name.Equals(bodyEntry.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(o =>
            {
                try
                {
                    return new EpfFile(o);
                }
                catch
                {
                    return null;
                }
            })
            .Where(o => o is not null)
            .Cast<EpfFile>()
            .ToList() ?? [];

        EpfFile? behind = null;
        EpfFile? pair = null;
        string? pairSlot = KhanPieces.PairSlot(info);
        if (pairSlot is not null)
        {
            ArchiveEntry? pairEntry = archive.GetEntry(KhanPieces.PairFileName(info, pairSlot));
            if (pairEntry is not null)
            {
                try
                {
                    var pairFile = new EpfFile(pairEntry);
                    if (KhanPieces.PairDrawsBehind(pairSlot))
                    {
                        behind = pairFile;
                    }
                    else
                    {
                        pair = pairFile;
                    }
                }
                catch
                {
                    // pair is optional
                }
            }
        }

        Palette bodyPal = PaletteLocator.Named(archive, paletteOverride) is not null || bodyEntry is null
            ? palette
            : PaletteLocator.ForEntry(archive, bodyEntry) ?? palette;

        var frames = new List<PixelBuffer>(count);
        for (int i = 0; i < count; i++)
        {
            int index = thumb ? start : i;
            frames.Add(CompositeKhanFrame(behind, body, bodyPal, piece, palette, pair, overlays, index, additive, info));
        }

        return frames;
    }

    private static PixelBuffer CompositeKhanFrame(
        EpfFile? behind,
        EpfFile? body,
        Palette bodyPal,
        EpfFile piece,
        Palette piecePal,
        EpfFile? pair,
        IReadOnlyList<EpfFile> overlays,
        int index,
        bool additive,
        KhanPieceInfo info)
    {
        int extraX = KhanPieces.LayerOffsetX(info);
        bool pieceBehind = KhanPieces.DrawsBehind(info)
            || (body is not null && KhanPieces.IsAccessory(info)
                && AccessoryHidesBody(body, bodyPal, piece, piecePal, index, extraX, additive));
        int originX = 0;
        int originY = 0;
        int w = 1;
        int h = 1;

        Include(behind, extraX);
        Include(body, 0);
        Include(piece, extraX);
        Include(pair, extraX);
        foreach (EpfFile overlay in overlays)
        {
            Include(overlay, extraX);
        }

        originX = Math.Max(0, -originX);
        originY = Math.Max(0, -originY);
        w += originX;
        h += originY;

        var canvas = new PixelBuffer(Math.Max(1, w), Math.Max(1, h));
        Blit(behind, piecePal, extraX);
        if (pieceBehind)
        {
            Blit(piece, piecePal, extraX);
        }

        Blit(body, bodyPal, 0);
        if (!pieceBehind)
        {
            Blit(piece, piecePal, extraX);
        }

        Blit(pair, piecePal, extraX);
        foreach (EpfFile overlay in overlays)
        {
            Blit(overlay, piecePal, extraX);
        }

        return canvas;

        void Include(EpfFile? file, int dx)
        {
            if (file is null || file.Frames.Count == 0)
            {
                return;
            }

            EpfFrame frame = file.Frames[KhanPieces.FrameIndex(file.Frames.Count, index)];
            int x = frame.Left + dx;
            int y = frame.Top;
            originX = Math.Min(originX, x);
            originY = Math.Min(originY, y);
            w = Math.Max(w, x + frame.Width);
            h = Math.Max(h, y + frame.Height);
            w = Math.Max(w, file.Width + dx);
            h = Math.Max(h, file.Height);
        }

        void Blit(EpfFile? file, Palette pal, int dx)
        {
            if (file is null || file.Frames.Count == 0)
            {
                return;
            }

            EpfFrame frame = file.Frames[KhanPieces.FrameIndex(file.Frames.Count, index)];
            canvas.Blit(frame.Render(pal, additive), frame.Left + dx + originX, frame.Top + originY);
        }
    }

    private static bool AccessoryHidesBody(
        EpfFile body,
        Palette bodyPal,
        EpfFile piece,
        Palette piecePal,
        int index,
        int extraX,
        bool additive)
    {
        EpfFrame bodyFrame = body.Frames[KhanPieces.FrameIndex(body.Frames.Count, index)];
        EpfFrame pieceFrame = piece.Frames[KhanPieces.FrameIndex(piece.Frames.Count, index)];
        PixelBuffer bodyPx = bodyFrame.Render(bodyPal, additive);
        PixelBuffer piecePx = pieceFrame.Render(piecePal, additive);
        int bodyVisible = 0;
        int covered = 0;
        for (int y = 0; y < bodyPx.Height; y++)
        {
            for (int x = 0; x < bodyPx.Width; x++)
            {
                if (bodyPx.Bgra[(y * bodyPx.Width + x) * 4 + 3] < 16)
                {
                    continue;
                }

                bodyVisible++;
                int px = bodyFrame.Left + x - (pieceFrame.Left + extraX);
                int py = bodyFrame.Top + y - pieceFrame.Top;
                if ((uint)px >= (uint)piecePx.Width || (uint)py >= (uint)piecePx.Height)
                {
                    continue;
                }

                if (piecePx.Bgra[(py * piecePx.Width + px) * 4 + 3] >= 16)
                {
                    covered++;
                }
            }
        }

        return bodyVisible > 0 && covered * 4 > bodyVisible;
    }

    private static List<PixelBuffer> EncodedImageFrames(ArchiveEntry entry)
    {
        PixelBuffer? pixels = WpfImages.TryDecodeImage(entry.ReadAllBytes());
        return pixels is null ? [] : [pixels];
    }

    private static List<PixelBuffer> ExtractFloorTile(IArchive archive, ArchiveEntry entry, string? paletteOverride)
    {
        if (!FloorTiles.IsFloorName(entry))
        {
            return [];
        }

        int index = Math.Clamp(entry.FrameIndex ?? 0, 0, Math.Max(0, FloorTiles.Count(entry.Size) - 1));
        bool snowTile = Path.GetFileName(entry.Name).Equals("TILEAS.BMP", StringComparison.OrdinalIgnoreCase);
        Palette pal = PaletteLocator.Named(archive, paletteOverride)
            ?? (snowTile ? PaletteLocator.ForSnowFloor(archive) : PaletteLocator.ForFloorTile(archive, index + 1))
            ?? FallbackPalette();
        return [pal.Render(FloorTiles.ReadTile(entry, index), FloorTiles.TileWidth, FloorTiles.TileHeight)];
    }

    private static List<PixelBuffer> ExtractEpf(
        IArchive archive,
        ArchiveEntry entry,
        Palette palette,
        int maxFrames,
        bool padToSheet = true)
    {
        int start = entry.FrameIndex ?? 0;
        int want = entry.FrameIndex is not null ? 1 : Math.Max(1, maxFrames);
        var epf = new EpfFile(entry, start, want);
        int count = epf.Frames.Count;
        if (count == 0)
        {
            return [];
        }

        var frames = new List<PixelBuffer>(count);
        bool thumb = maxFrames <= 1 || entry.FrameIndex is not null || !padToSheet;
        int canvasW = 1;
        int canvasH = 1;
        if (!thumb)
        {
            canvasW = Math.Max(1, epf.Width);
            canvasH = Math.Max(1, epf.Height);
            foreach (EpfFrame frame in epf.Frames)
            {
                canvasW = Math.Max(canvasW, frame.Left + frame.Width);
                canvasH = Math.Max(canvasH, frame.Top + frame.Height);
            }
        }

        for (int n = 0; n < count; n++)
        {
            int i = start + n;
            Palette framePal = PaletteForItemFrame(archive, entry, i, palette);
            PixelBuffer rendered = epf.Frames[n].Render(framePal);
            if (thumb)
            {
                frames.Add(rendered);
                continue;
            }

            var canvas = new PixelBuffer(canvasW, canvasH);
            canvas.Blit(rendered, epf.Frames[n].Left, epf.Frames[n].Top);
            frames.Add(canvas);
        }

        return frames;
    }

    private static List<PixelBuffer> ExtractMpf(
        IArchive archive,
        ArchiveEntry entry,
        Palette palette,
        int maxFrames,
        bool padToSheet = true)
    {
        var mpf = new MpfFile(entry, maxFrames);
        if (mpf.Frames.Count == 0)
        {
            return [];
        }

        Palette pal = palette;
        int count = Math.Min(mpf.Frames.Count, Math.Max(1, maxFrames));
        bool thumb = maxFrames <= 1;
        bool pad = padToSheet && !thumb;
        int w = Math.Max(1, mpf.Width);
        int h = Math.Max(1, mpf.Height);
        var frames = new List<PixelBuffer>(count);
        int start = 0;
        if (thumb && mpf.StopFrameCount > 0)
        {
            start = Math.Clamp(mpf.StopFrameIndex, 0, mpf.Frames.Count - 1);
            count = 1;
        }

        for (int i = 0; i < count; i++)
        {
            int idx = thumb ? start : i;
            MpfFrame frame = mpf.Frames[idx];
            PixelBuffer rendered = frame.Render(pal);
            if (!pad)
            {
                frames.Add(rendered);
                continue;
            }

            var canvas = new PixelBuffer(w, h);
            canvas.Blit(rendered, frame.Left, frame.Top);
            frames.Add(canvas);
        }

        return frames;
    }

    private static List<PixelBuffer> ExtractSpf(ArchiveEntry entry, Palette palette, int maxFrames)
    {
        var spf = new SpfFile(entry, Math.Max(1, maxFrames));
        return spf.Frames.Take(Math.Max(1, maxFrames)).Select(f => f.Render(spf, palette)).ToList();
    }

    private static Palette FallbackPalette()
    {
        using var ms = new MemoryStream(new byte[256 * 3]);
        for (int i = 0; i < 256; i++)
        {
            ms.WriteByte((byte)i);
            ms.WriteByte((byte)i);
            ms.WriteByte((byte)i);
        }

        ms.Position = 0;
        return new Palette(ms);
    }

    private static PixelBuffer RenderTile(ArchiveEntry entry, Palette palette)
    {
        string ext = entry.Extension.ToLowerInvariant();
        return ext switch
        {
            ".pcx" => new PcxFile(entry).Image,
            ".spf" => AssembleSpf(new SpfFile(entry), palette),
            ".hpf" => new HpfFile(entry).Render(palette),
            _ => new HpfFile(entry).Render(palette)
        };
    }

    private static PixelBuffer AssembleSpf(SpfFile spf, Palette? palette)
    {
        if (spf.Frames.Count == 0)
        {
            return new PixelBuffer(1, 1);
        }

        int width = Math.Max(1, spf.Frames.Max(f => f.Left + f.Width));
        int height = Math.Max(1, spf.Frames.Max(f => f.Top + f.Height));
        var canvas = new PixelBuffer(width, height);
        foreach (SpfFrame frame in spf.Frames)
        {
            canvas.Blit(frame.Render(spf, palette), frame.Left, frame.Top);
        }

        return canvas;
    }

    private static PixelBuffer AssembleEpf(EpfFile epf, Palette palette)
    {
        int width = Math.Max(1, Math.Max(epf.Width, epf.Frames.Max(f => Math.Max(f.Right, f.Left + f.Width))));
        int height = Math.Max(1, Math.Max(epf.Height, epf.Frames.Max(f => Math.Max(f.Bottom, f.Top + f.Height))));
        var canvas = new PixelBuffer(width, height);
        foreach (EpfFrame frame in epf.Frames)
        {
            canvas.Blit(frame.Render(palette), frame.Left, frame.Top);
        }

        return canvas;
    }

    private static bool LooksLikeSlices(IReadOnlyList<(int Left, int Top, int Width, int Height)> boxes)
    {
        if (boxes.Count <= 1)
        {
            return false;
        }

        int minL = boxes.Min(b => b.Left);
        int minT = boxes.Min(b => b.Top);
        int maxR = boxes.Max(b => b.Left + b.Width);
        int maxB = boxes.Max(b => b.Top + b.Height);
        int union = Math.Max(1, (maxR - minL) * (maxB - minT));
        int sum = boxes.Sum(b => Math.Max(1, b.Width * b.Height));
        return sum <= union * 3 / 2;
    }

    private static Palette PaletteForItemFrame(IArchive archive, ArchiveEntry entry, int frameIndex, Palette fallback)
    {
        if (!entry.Name.StartsWith("item", StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        PaletteTable? table = LoadTable(archive, "itempal.tbl");
        if (table is null)
        {
            return PaletteLocator.Find(archive, "item000.pal") ?? fallback;
        }

        int fileNumber = ParseDigits(entry.Name);
        int tileNumber = Math.Max(1, (fileNumber - 1) * 266 + frameIndex + 1);
        int palNum = table.GetPaletteNumber(tileNumber);
        return PaletteLocator.FindNumbered(archive, "item", palNum) ?? fallback;
    }

    private static PaletteTable? LoadTable(IArchive archive, string name)
    {
        ArchiveEntry? entry = archive.GetEntry(name);
        if (entry is null)
        {
            return null;
        }

        var table = new PaletteTable();
        table.AddTable(entry);
        return table;
    }

    private static int ParseDigits(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        var digits = new string(stem.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out int n) ? n : 0;
    }

    private static string MpfPaletteNote(MpfFile mpf)
    {
        IReadOnlyList<int> numbers = mpf.PaletteNumbers.Count > 0 ? mpf.PaletteNumbers : [mpf.PaletteNumber];
        string baked = string.Join(", ", numbers.Select(n => $"mns{n:D3}.pal"));
        return $"Palette #{mpf.PaletteNumber} → {baked} (dummy TOC slot, not extra frames).\nColor variants are palette swaps of the same indexed pixels. Use the strip under Palette; ★ is stored in this MPF. A/B/C sibling files are separate sprites of the same id.";
    }

    private static string MonsterSpriteNote(ArchiveEntry entry)
    {
        int fileId = ParseDigits(entry.Name);
        int image = fileId + 16384;
        return $"In-game DisplayNpc Image = {fileId} + 16384 = {image} (opcode 0x07). Image >= 32768 is a ground item, not an MPF.";
    }

    private static string paletteOverrideName(IArchive archive, Palette palette) =>
        PaletteLocator.Palettes(archive).FirstOrDefault()?.Name ?? "(default)";

    private static IReadOnlyList<int> MotionRange(MpfFile mpf, string group)
    {
        List<int> Range(int start, int count)
        {
            var list = new List<int>();
            for (int i = 0; i < Math.Max(count, 0); i++)
            {
                int idx = start + i;
                if (idx >= 0 && idx < mpf.Frames.Count)
                {
                    list.Add(idx);
                }
            }

            return list;
        }

        return group switch
        {
            "Idle" => Range(mpf.StopFrameIndex, Math.Max(mpf.StopFrameCount, mpf.StopMotionFrameCount)),
            "Walk" => Range(mpf.WalkFrameIndex, mpf.WalkFrameCount),
            "Attack" => Range(mpf.AttackFrameIndex, mpf.AttackFrameCount),
            "Attack2" => Range(mpf.Attack2FrameIndex, mpf.Attack2FrameCount),
            "Attack3" => Range(mpf.Attack3FrameIndex, mpf.Attack3FrameCount),
            _ => Enumerable.Range(0, mpf.Frames.Count).ToList()
        };
    }
}
