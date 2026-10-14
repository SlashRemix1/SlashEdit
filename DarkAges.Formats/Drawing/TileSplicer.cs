using DarkAges.Formats.Catalog;
using DarkAges.Formats.Imaging;
using DarkAges.Formats.Maps;

namespace DarkAges.Formats.Drawing;

public enum TileSheetLayout
{
    IsoDiamond,
    PackedGrid,
    WallColumns
}

public sealed class TileSlice
{
    public TileSlice(int index, int column, int row, PixelBuffer pixels)
    {
        Index = index;
        Column = column;
        Row = row;
        Pixels = pixels;
    }

    public int Index { get; }
    public int Column { get; }
    public int Row { get; }
    public PixelBuffer Pixels { get; }
}

public static class TileSplicer
{
    public static IReadOnlyList<TileSlice> Extract(
        PixelBuffer sheet,
        TileSheetLayout layout,
        int originX = 0,
        int originY = 0,
        int columns = 0,
        int rows = 0,
        int wallHeight = 0)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        return layout switch
        {
            TileSheetLayout.PackedGrid => ExtractPacked(sheet, originX, originY, columns, rows),
            TileSheetLayout.WallColumns => ExtractWalls(sheet, originX, originY, columns, wallHeight),
            _ => ExtractIso(sheet, originX, originY, columns, rows)
        };
    }

    public static PixelBuffer OverlayGrid(
        PixelBuffer sheet,
        TileSheetLayout layout,
        int originX = 0,
        int originY = 0,
        int columns = 0,
        int rows = 0,
        int wallHeight = 0)
    {
        PixelBuffer preview = sheet.Clone();
        foreach (TileSlice slice in Extract(sheet, layout, originX, originY, columns, rows, wallHeight))
        {
            (int x, int y) = SliceOrigin(layout, originX, originY, slice.Column, slice.Row, wallHeight > 0 ? wallHeight : preview.Height);
            if (layout == TileSheetLayout.IsoDiamond)
            {
                StrokeDiamond(preview, x, y, FloorTiles.TileWidth, FloorTiles.TileHeight, 110, 168, 254, 220);
            }
            else
            {
                StrokeRect(preview, x, y, slice.Pixels.Width, slice.Pixels.Height, 110, 168, 254, 220);
            }
        }

        return preview;
    }

    public static void MaskFloorDiamond(PixelBuffer tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        for (int y = 0; y < tile.Height; y++)
        {
            for (int x = 0; x < tile.Width; x++)
            {
                if (!IsoCoords.DiamondContains(x, y, 0, 0, FloorTiles.TileWidth, FloorTiles.TileHeight))
                {
                    tile.SetPixel(x, y, 0, 0, 0, 0);
                }
            }
        }
    }

    public static byte[] ToFloorTile(PixelBuffer pixels, Palette palette, bool dither = false)
    {
        var tile = new PixelBuffer(FloorTiles.TileWidth, FloorTiles.TileHeight);
        tile.Blit(pixels, 0, 0);
        MaskFloorDiamond(tile);
        return ColorQuantizer.Index(tile, palette, dither);
    }

    public static byte[] ToWallTile(PixelBuffer pixels, Palette palette, bool dither = false)
    {
        int height = Math.Max(1, pixels.Height);
        var tile = new PixelBuffer(HpfWriter.TileWidth, height);
        tile.Blit(pixels, 0, 0);
        return ColorQuantizer.Index(tile, palette, dither);
    }

    public static byte[] ImportFloors(byte[] atlas, IReadOnlyList<TileSlice> slices, Palette palette, int startIndex, bool dither = false)
    {
        byte[] data = atlas ?? [];
        for (int i = 0; i < slices.Count; i++)
        {
            byte[] indexed = ToFloorTile(slices[i].Pixels, palette, dither);
            data = FloorTiles.WriteTile(data, startIndex + i, indexed);
        }

        return data;
    }

    private static List<TileSlice> ExtractIso(PixelBuffer sheet, int originX, int originY, int columns, int rows)
    {
        GuessIso(sheet, originX, originY, ref columns, ref rows);
        var list = new List<TileSlice>(Math.Max(0, columns * rows));
        int index = 0;
        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < columns; col++)
            {
                (int sx, int sy) = IsoCoords.TileToScreen(col, row, originX, originY);
                var tile = new PixelBuffer(FloorTiles.TileWidth, FloorTiles.TileHeight);
                CopyRect(sheet, tile, sx, sy);
                MaskFloorDiamond(tile);
                if (tile.HasVisiblePixels())
                {
                    list.Add(new TileSlice(index, col, row, tile));
                }

                index++;
            }
        }

        return list;
    }

    private static List<TileSlice> ExtractPacked(PixelBuffer sheet, int originX, int originY, int columns, int rows)
    {
        int tw = FloorTiles.TileWidth;
        int th = FloorTiles.TileHeight;
        if (columns <= 0)
        {
            columns = Math.Max(1, (sheet.Width - originX) / tw);
        }

        if (rows <= 0)
        {
            rows = Math.Max(1, (sheet.Height - originY) / th);
        }

        var list = new List<TileSlice>();
        int index = 0;
        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < columns; col++)
            {
                int sx = originX + col * tw;
                int sy = originY + row * th;
                var tile = new PixelBuffer(tw, th);
                CopyRect(sheet, tile, sx, sy);
                MaskFloorDiamond(tile);
                if (tile.HasVisiblePixels())
                {
                    list.Add(new TileSlice(index, col, row, tile));
                }

                index++;
            }
        }

        return list;
    }

    private static List<TileSlice> ExtractWalls(PixelBuffer sheet, int originX, int originY, int columns, int wallHeight)
    {
        int tw = HpfWriter.TileWidth;
        int th = wallHeight > 0 ? wallHeight : Math.Max(1, sheet.Height - originY);
        if (columns <= 0)
        {
            columns = Math.Max(1, (sheet.Width - originX) / tw);
        }

        var list = new List<TileSlice>();
        for (int col = 0; col < columns; col++)
        {
            int sx = originX + col * tw;
            var tile = new PixelBuffer(tw, th);
            CopyRect(sheet, tile, sx, originY);
            if (tile.HasVisiblePixels())
            {
                list.Add(new TileSlice(col, col, 0, tile));
            }
        }

        return list;
    }

    private static void GuessIso(PixelBuffer sheet, int originX, int originY, ref int columns, ref int rows)
    {
        if (columns > 0 && rows > 0)
        {
            return;
        }

        int usableW = Math.Max(1, sheet.Width - originX);
        int usableH = Math.Max(1, sheet.Height - originY);
        if (columns <= 0)
        {
            columns = Math.Max(1, (usableW + IsoCoords.StepX - 1) / IsoCoords.StepX);
        }

        if (rows <= 0)
        {
            rows = Math.Max(1, (usableH + IsoCoords.StepY - 1) / IsoCoords.StepY);
        }

        columns = Math.Clamp(columns, 1, 128);
        rows = Math.Clamp(rows, 1, 128);
    }

    private static (int X, int Y) SliceOrigin(TileSheetLayout layout, int originX, int originY, int col, int row, int wallHeight)
    {
        return layout switch
        {
            TileSheetLayout.PackedGrid => (originX + col * FloorTiles.TileWidth, originY + row * FloorTiles.TileHeight),
            TileSheetLayout.WallColumns => (originX + col * HpfWriter.TileWidth, originY),
            _ => IsoCoords.TileToScreen(col, row, originX, originY)
        };
    }

    private static void CopyRect(PixelBuffer source, PixelBuffer dest, int srcX, int srcY)
    {
        for (int y = 0; y < dest.Height; y++)
        {
            int sy = srcY + y;
            if ((uint)sy >= (uint)source.Height)
            {
                continue;
            }

            for (int x = 0; x < dest.Width; x++)
            {
                int sx = srcX + x;
                if ((uint)sx >= (uint)source.Width)
                {
                    continue;
                }

                var (b, g, r, a) = source.GetPixel(sx, sy);
                dest.SetPixel(x, y, b, g, r, a);
            }
        }
    }

    private static void StrokeRect(PixelBuffer dest, int left, int top, int width, int height, byte b, byte g, byte r, byte a)
    {
        for (int x = 0; x < width; x++)
        {
            dest.SetPixel(left + x, top, b, g, r, a);
            dest.SetPixel(left + x, top + height - 1, b, g, r, a);
        }

        for (int y = 0; y < height; y++)
        {
            dest.SetPixel(left, top + y, b, g, r, a);
            dest.SetPixel(left + width - 1, top + y, b, g, r, a);
        }
    }

    private static void StrokeDiamond(PixelBuffer dest, int left, int top, int width, int height, byte b, byte g, byte r, byte a)
    {
        int cx = left + width / 2;
        int cy = top + height / 2;
        int hw = width / 2;
        int hh = Math.Max(1, height / 2);
        PlotLine(dest, cx, top, left + width - 1, cy, b, g, r, a);
        PlotLine(dest, left + width - 1, cy, cx, top + height - 1, b, g, r, a);
        PlotLine(dest, cx, top + height - 1, left, cy, b, g, r, a);
        PlotLine(dest, left, cy, cx, top, b, g, r, a);
        _ = (hw, hh);
    }

    private static void PlotLine(PixelBuffer dest, int x0, int y0, int x1, int y1, byte b, byte g, byte r, byte a)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            dest.SetPixel(x0, y0, b, g, r, a);
            if (x0 == x1 && y0 == y1)
            {
                break;
            }

            int e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }
}
