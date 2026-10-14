namespace DarkAges.Viewer.Maps;

public sealed class IsoTileSelection
{
    public bool Dragging { get; private set; }
    public bool HasSelection { get; private set; }
    public int X0 { get; private set; }
    public int Y0 { get; private set; }
    public int X1 { get; private set; }
    public int Y1 { get; private set; }
    public int Width => Math.Abs(X1 - X0) + 1;
    public int Height => Math.Abs(Y1 - Y0) + 1;

    public void Begin(int x, int y)
    {
        Dragging = true;
        HasSelection = true;
        X0 = X1 = x;
        Y0 = Y1 = y;
    }

    public void Drag(int x, int y)
    {
        X1 = x;
        Y1 = y;
    }

    public void End(int mapWidth, int mapHeight, int maxSize = 48)
    {
        Dragging = false;
        Clamp(mapWidth, mapHeight, maxSize);
    }

    public void Clear()
    {
        Dragging = false;
        HasSelection = false;
    }

    public void Clamp(int mapWidth, int mapHeight, int maxSize = 48)
    {
        int x0 = Math.Clamp(Math.Min(X0, X1), 0, Math.Max(0, mapWidth - 1));
        int y0 = Math.Clamp(Math.Min(Y0, Y1), 0, Math.Max(0, mapHeight - 1));
        int x1 = Math.Clamp(Math.Max(X0, X1), 0, Math.Max(0, mapWidth - 1));
        int y1 = Math.Clamp(Math.Max(Y0, Y1), 0, Math.Max(0, mapHeight - 1));
        x1 = Math.Min(x1, x0 + maxSize - 1);
        y1 = Math.Min(y1, y0 + maxSize - 1);
        X0 = x0;
        Y0 = y0;
        X1 = x1;
        Y1 = y1;
    }

    public (int X, int Y)[] ScreenCorners(int mapHeight, int stepX = 28, int stepY = 14, int originY = 160)
    {
        int originX = mapHeight * stepX;
        (int nwx, int nwy) = MapAssets.TileToScreen(X0, Y0, originX, originY, stepX, stepY);
        (int nex, int ney) = MapAssets.TileToScreen(X1, Y0, originX, originY, stepX, stepY);
        (int sex, int sey) = MapAssets.TileToScreen(X1, Y1, originX, originY, stepX, stepY);
        (int swx, int swy) = MapAssets.TileToScreen(X0, Y1, originX, originY, stepX, stepY);
        const int pad = 2;
        return
        [
            (nwx + stepX, nwy - pad),
            (nex + stepX * 2 + pad, ney + stepY),
            (sex + stepX, sey + stepY * 2 + pad),
            (swx - pad, swy + stepY)
        ];
    }
}
