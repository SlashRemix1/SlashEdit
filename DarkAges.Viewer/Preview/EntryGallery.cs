using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using DarkAges.Formats.Catalog;
using DarkAges.Formats.Data;
using DarkAges.Formats.Drawing;
using DarkAges.Formats.Imaging;
using DarkAges.Formats.Maps;
using DarkAges.Viewer.Imaging;
using DarkAges.Viewer.Maps;

namespace DarkAges.Viewer.Preview;

public sealed class EntryGallery
{
    public const int SpriteCellSize = 80;
    public const int SpriteColumns = 8;
    public const int MapCellWidth = 400;
    public const int MapCellHeight = 300;
    public const int MapColumns = 3;
    public static readonly int[] PageSizeChoices = [10, 20, 50, 100, 200, 500];

    private readonly List<GalleryItem> _cells = [];
    private List<ArchiveEntry> _source = [];
    private IArchive? _archive;
    private IReadOnlyList<MapAssets> _mapTilesets = [];
    private string? _tilesetName;
    private MapTilesetOption? _tileset;
    private int _floorOffset;
    private string? _mapHint;
    private string? _paletteOverride;
    private WarpTable _warps = WarpTable.Empty;

    public ObservableCollection<GalleryRow> Rows { get; } = [];
    public IReadOnlyList<GalleryItem> Cells => _cells;
    public int Count => _cells.Count;
    public int TotalCount => _source.Count;
    public int Page { get; private set; }
    public int PageSize { get; set; } = 200;
    public int CellWidth { get; private set; } = SpriteCellSize;
    public int CellHeight { get; private set; } = SpriteCellSize;
    public int Columns { get; private set; } = SpriteColumns;
    public bool IsMapGallery { get; private set; }
    public int PageCount => Math.Max(1, (int)Math.Ceiling(_source.Count / (double)Math.Max(1, PageSize)));
    public int PageStart => _source.Count == 0 ? 0 : Page * PageSize + 1;
    public int PageEnd => Math.Min(_source.Count, (Page + 1) * PageSize);
    public int Frame { get; private set; }
    public int MaxFrameCount => _cells.Count == 0 ? 1 : Math.Max(1, _cells.Max(c => c.FrameCount));

    public void SetSource(
        IArchive archive,
        IEnumerable<ArchiveEntry> entries,
        IReadOnlyList<MapAssets>? mapTilesets = null,
        string? tilesetName = null,
        string? mapHint = null,
        MapTilesetOption? tileset = null,
        int floorOffset = 0,
        string? paletteOverride = null,
        WarpTable? warps = null)
    {
        _archive = archive;
        _mapTilesets = mapTilesets ?? [];
        _tilesetName = tilesetName;
        _tileset = tileset;
        _floorOffset = Math.Max(0, floorOffset);
        _mapHint = mapHint;
        _warps = warps ?? WarpTable.Empty;
        SetPalette(paletteOverride);
        var visual = entries.Where(IsVisual).ToList();
        if (KhanPieces.IsKhanArchive(archive))
        {
            visual = KhanPieces.CollapseForGallery(visual).ToList();
        }

        _source = visual;
        IsMapGallery = _source.Count > 0 && _source.All(e => e.Extension.Equals(".map", StringComparison.OrdinalIgnoreCase));
        if (IsMapGallery)
        {
            CellWidth = MapCellWidth;
            CellHeight = MapCellHeight;
            Columns = MapColumns;
        }
        else
        {
            CellWidth = SpriteCellSize;
            CellHeight = SpriteCellSize;
            Columns = SpriteColumns;
        }

        Page = 0;
    }

    public void SetTileset(string? tilesetName) => _tilesetName = tilesetName;

    public void SetTileset(MapTilesetOption? tileset)
    {
        _tileset = tileset;
        _tilesetName = tileset?.Assets is null ? "(auto)" : tileset.Label;
    }

    public void SetFloorOffset(int floorOffset) => _floorOffset = Math.Max(0, floorOffset);

    public void SetPalette(string? name) =>
        _paletteOverride = string.IsNullOrWhiteSpace(name) || name.Equals("(auto)", StringComparison.OrdinalIgnoreCase)
            ? null
            : name;

    public async Task FillPageAsync(int page, CancellationToken cancellationToken, IProgress<int>? progress)
    {
        if (_archive is null)
        {
            return;
        }

        Page = Math.Clamp(page, 0, PageCount - 1);
        _cells.Clear();
        Rows.Clear();
        int start = Page * PageSize;
        int take = Math.Min(PageSize, Math.Max(0, _source.Count - start));
        if (take == 0)
        {
            progress?.Report(0);
            return;
        }

        var pageEntries = _source.GetRange(start, take);
        const int batchSize = 64;
        GalleryRow? row = null;
        IArchive archive = _archive;

        for (int offset = 0; offset < pageEntries.Count; offset += batchSize)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            int n = Math.Min(batchSize, pageEntries.Count - offset);
            var slice = pageEntries.GetRange(offset, n);
            int cellW = CellWidth;
            int cellH = CellHeight;
            IReadOnlyList<MapAssets> tilesets = _mapTilesets;
            string? tilesetName = _tilesetName;
            MapTilesetOption? tileset = _tileset;
            int floorOffset = _floorOffset;
            string? mapHint = _mapHint;
            string? paletteOverride = _paletteOverride;
            WarpTable warps = _warps;
            int baseIndex = offset;
            List<GalleryItem?> decoded = await Task.Run(() =>
            {
                var items = new GalleryItem?[slice.Count];
                int completed = 0;
                Parallel.For(0, slice.Count, (i, state) =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        state.Stop();
                        return;
                    }

                    items[i] = DecodeThumb(
                        archive, slice[i], cellW, cellH, tilesets, tilesetName, mapHint, tileset, floorOffset, paletteOverride, warps);
                    progress?.Report(baseIndex + Interlocked.Increment(ref completed));
                });
                return items.ToList();
            }).ConfigureAwait(true);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            foreach (GalleryItem? item in decoded)
            {
                if (item is null)
                {
                    continue;
                }

                _cells.Add(item);
                if (row is null || row.Items.Count >= Columns)
                {
                    row = new GalleryRow();
                    Rows.Add(row);
                }

                row.Items.Add(item);
            }
        }

        bool khan = _cells.Any(c => KhanPieces.TryParse(c.Name, out _));
        Frame = khan ? KhanPieces.IdleDirection : 0;
        foreach (GalleryItem cell in _cells)
        {
            cell.SetFrame(Frame);
        }
    }

    public void SetFrame(int frame)
    {
        Frame = Math.Max(0, frame);
        foreach (GalleryItem cell in _cells)
        {
            cell.SetFrame(Frame);
        }
    }

    public void Advance()
    {
        EnsureAllFrames();
        int max = MaxFrameCount;
        SetFrame(max <= 1 ? 0 : (Frame + 1) % max);
    }

    public void Retreat()
    {
        EnsureAllFrames();
        int max = MaxFrameCount;
        SetFrame(max <= 1 ? 0 : (Frame - 1 + max) % max);
    }

    public void EnsureAllFrames()
    {
        Parallel.ForEach(_cells, cell =>
        {
            try
            {
                cell.EnsureAllFrames();
            }
            catch
            {
                // leave the thumbnail as-is if a frame set fails to decode
            }
        });
    }

    public static bool IsVisual(ArchiveEntry entry)
    {
        string ext = entry.Extension.ToLowerInvariant();
        return ext is ".epf" or ".mpf" or ".spf" or ".hpf" or ".efa" or ".pcx" or ".bmp" or ".map"
            or ".hea" or ".jpf" or ".jpg" or ".jpeg" or ".png" or ".rgba" or ".bik"
            or ".fnt" or ".lft" or ".upf";
    }

    private static int PeekFrameCount(ArchiveEntry entry)
    {
        try
        {
            string ext = entry.Extension.ToLowerInvariant();
            if (entry.FrameIndex is not null)
            {
                return 1;
            }

            return ext switch
            {
                ".epf" => EpfFile.PeekFrameCount(entry),
                ".mpf" => MpfFile.PeekFrameCount(entry),
                ".spf" => SpfFile.PeekFrameCount(entry),
                ".efa" => EfaFile.PeekFrameCount(entry),
                ".rgba" => RgbaImage.PeekFrameCount(entry),
                ".bik" => BinkFile.TryParse(entry, out BinkInfo bink) ? Math.Max(1, bink.FrameCount) : 1,
                ".fnt" => FntFile.PeekFrameCount(entry),
                ".lft" => 256,
                ".upf" => UpfFile.PeekFrameCount(entry),
                _ => 1
            };
        }
        catch
        {
            return 1;
        }
    }

    private static GalleryItem? DecodeThumb(
        IArchive archive,
        ArchiveEntry entry,
        int cellWidth,
        int cellHeight,
        IReadOnlyList<MapAssets> tilesets,
        string? tilesetName,
        string? mapHint,
        MapTilesetOption? tileset,
        int floorOffset,
        string? paletteOverride,
        WarpTable warps)
    {
        try
        {
            PixelBuffer? pixels = null;
            if (entry.Extension.Equals(".bik", StringComparison.OrdinalIgnoreCase))
            {
                return GalleryItem.Placeholder(archive, entry, cellWidth, cellHeight);
            }

            if (entry.Extension.Equals(".map", StringComparison.OrdinalIgnoreCase))
            {
                if (tilesets.Count == 0)
                {
                    return GalleryItem.Placeholder(archive, entry, cellWidth, cellHeight);
                }

                MapFile map = MapFile.FromBytes(entry.ReadAllBytes());
                string hint = PreviewBuilder.MapHint(mapHint, entry);
                bool snow = tileset?.Snow == true;
                MapAssets assets = tileset?.Assets
                    ?? (string.IsNullOrWhiteSpace(tilesetName) || tilesetName.Equals("(auto)", StringComparison.OrdinalIgnoreCase)
                        ? MapAssets.Pick(tilesets, map, hint)
                        : tilesets.FirstOrDefault(t => t.Label.Equals(tilesetName, StringComparison.OrdinalIgnoreCase))
                            ?? MapAssets.Pick(tilesets, map, hint));
                Palette? pal = PaletteLocator.Named(assets.Seo, paletteOverride)
                    ?? PaletteLocator.Named(assets.Ia, paletteOverride);
                pixels = assets.Render(
                    map,
                    snow,
                    maxEdge: Math.Min(160, Math.Max(80, cellWidth - 16)),
                    floorOffset: floorOffset,
                    paletteOverride: pal);
            }
            else
            {
                IReadOnlyList<PixelBuffer> frames = PreviewBuilder.ExtractFrames(
                    archive, entry, maxFrames: 1, paletteOverride: paletteOverride);
                if (frames.Count > 0)
                {
                    pixels = frames[0];
                }
            }

            if (pixels is null || (LegendItems.IsItemGraphic(entry) && !pixels.HasVisiblePixels()))
            {
                return null;
            }

            BitmapSource image = GalleryItem.ToCellBitmap(pixels, cellWidth, cellHeight);
            int declared = PeekFrameCount(entry);
            int logical = KhanPieces.TryParse(entry.Name, out _) ? KhanPieces.IdleDirection : 0;
            bool maps = entry.Extension.Equals(".map", StringComparison.OrdinalIgnoreCase);
            bool caption = maps || FloorTiles.IsFloorName(entry) || LegendItems.IsItemGraphic(entry)
                || entry.Extension.Equals(".rgba", StringComparison.OrdinalIgnoreCase);
            return new GalleryItem(
                archive,
                entry,
                [image],
                allFramesLoaded: declared <= 1 || maps,
                logical,
                declared,
                cellWidth,
                cellHeight,
                showCaption: caption,
                paletteOverride: paletteOverride);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class GalleryRow
{
    public ObservableCollection<GalleryItem> Items { get; } = [];
}

public sealed class GalleryItem : INotifyPropertyChanged
{
    private readonly IArchive? _archive;
    private readonly ArchiveEntry? _entry;
    private BitmapSource[] _frames;
    private int _index;
    private int _logicalFrame;
    private readonly int _declaredCount;
    private readonly string? _paletteOverride;
    private bool _allFramesLoaded;

    public GalleryItem(
        IArchive? archive,
        ArchiveEntry? entry,
        BitmapSource[] frames,
        bool allFramesLoaded,
        int logicalFrame = 0,
        int declaredCount = 1,
        int cellWidth = EntryGallery.SpriteCellSize,
        int cellHeight = EntryGallery.SpriteCellSize,
        bool showCaption = false,
        string? paletteOverride = null)
    {
        _archive = archive;
        _entry = entry;
        _paletteOverride = paletteOverride;
        Name = entry?.DisplayName ?? "";
        CellWidth = cellWidth;
        CellHeight = cellHeight;
        ShowCaption = showCaption;
        _frames = frames.Length == 0 ? [PlaceholderBitmap()] : frames;
        _declaredCount = Math.Max(declaredCount, _frames.Length);
        _logicalFrame = logicalFrame;
        _allFramesLoaded = allFramesLoaded || _declaredCount <= 1;
        _index = 0;
    }

    public string Name { get; }
    public int CellWidth { get; }
    public int CellHeight { get; }
    public bool ShowCaption { get; }
    public ArchiveEntry? Entry => _entry;
    public int FrameCount => Math.Max(_declaredCount, _frames.Length);
    public BitmapSource Image => _frames[Math.Clamp(_index, 0, _frames.Length - 1)];

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetFrame(int frame)
    {
        _logicalFrame = Math.Max(0, frame);
        if (!_allFramesLoaded && _logicalFrame == (KhanPieces.TryParse(Name, out _) ? KhanPieces.IdleDirection : 0))
        {
            _index = 0;
            OnPropertyChanged(nameof(Image));
            return;
        }

        EnsureAllFrames();
        _index = _frames.Length <= 1 ? 0 : Math.Clamp(_logicalFrame, 0, _frames.Length - 1);
        OnPropertyChanged(nameof(Image));
    }

    public void EnsureAllFrames()
    {
        if (_allFramesLoaded || _archive is null || _entry is null)
        {
            return;
        }

        try
        {
            string ext = _entry.Extension.ToLowerInvariant();
            if (ext is ".hpf" or ".pcx" or ".bik" or ".fnt" or ".lft" or ".upf" or ".hea" or ".map")
            {
                _allFramesLoaded = true;
                return;
            }

            IReadOnlyList<PixelBuffer> frames = PreviewBuilder.ExtractFrames(
                _archive, _entry, maxFrames: 32, paletteOverride: _paletteOverride, padToSheet: false);
            if (frames.Count == 0)
            {
                _allFramesLoaded = true;
                return;
            }

            var images = new BitmapSource[frames.Count];
            for (int i = 0; i < frames.Count; i++)
            {
                images[i] = ToCellBitmap(frames[i], CellWidth, CellHeight);
            }

            _frames = images;
            _index = Math.Clamp(_logicalFrame, 0, _frames.Length - 1);
            _allFramesLoaded = true;
        }
        catch
        {
            _allFramesLoaded = true;
        }
    }

    public static GalleryItem Placeholder(IArchive archive, ArchiveEntry entry, int cellWidth = EntryGallery.SpriteCellSize, int cellHeight = EntryGallery.SpriteCellSize) =>
        new(archive, entry, [PlaceholderBitmap()], allFramesLoaded: true, 0, 1, cellWidth, cellHeight, showCaption: true);

    internal static BitmapSource ToCellBitmap(PixelBuffer pixels, int cellWidth, int cellHeight) =>
        WpfImages.ToBitmap(pixels.Contain(Math.Max(1, cellWidth - 8), Math.Max(1, cellHeight - 22)));

    private static BitmapSource PlaceholderBitmap()
    {
        var pixels = new PixelBuffer(74, 74);
        pixels.Clear(28, 28, 34, 255);
        return WpfImages.ToBitmap(pixels);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
