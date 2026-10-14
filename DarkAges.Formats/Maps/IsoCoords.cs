namespace DarkAges.Formats.Maps;

public static class IsoCoords
{
    public const int StepX = 28;
    public const int StepY = 14;
    public const int TileWidth = 56;
    public const int TileHeight = 27;
    public const int OriginY = 160;

    public static int OriginX(int mapHeight) => mapHeight * StepX;

    public static (int X, int Y) TileToScreen(int x, int y, int originX, int originY, int stepX = StepX, int stepY = StepY) =>
        (originX + (x - y) * stepX, originY + (x + y) * stepY);

    public static bool TryHit(
        int px,
        int py,
        int mapWidth,
        int mapHeight,
        out int tileX,
        out int tileY,
        int originX = -1,
        int originY = OriginY,
        int stepX = StepX,
        int stepY = StepY,
        int tileW = TileWidth,
        int tileH = TileHeight)
    {
        tileX = 0;
        tileY = 0;
        if (mapWidth <= 0 || mapHeight <= 0 || stepX <= 0 || stepY <= 0 || tileW < 2 || tileH < 2)
        {
            return false;
        }

        if (originX < 0)
        {
            originX = OriginX(mapHeight);
        }

        double a = (px - originX - tileW / 2.0) / stepX;
        double b = (py - originY - tileH / 2.0) / stepY;
        int guessX = (int)Math.Round((a + b) / 2.0, MidpointRounding.AwayFromZero);
        int guessY = (int)Math.Round((b - a) / 2.0, MidpointRounding.AwayFromZero);

        int best = int.MaxValue;
        int hitX = -1;
        int hitY = -1;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = guessX + dx;
                int y = guessY + dy;
                if ((uint)x >= (uint)mapWidth || (uint)y >= (uint)mapHeight)
                {
                    continue;
                }

                (int sx, int sy) = TileToScreen(x, y, originX, originY, stepX, stepY);
                if (!DiamondContains(px, py, sx, sy, tileW, tileH))
                {
                    continue;
                }

                int dist = Math.Abs(px - (sx + tileW / 2)) + Math.Abs(py - (sy + tileH / 2));
                if (dist < best)
                {
                    best = dist;
                    hitX = x;
                    hitY = y;
                }
            }
        }

        if (hitX < 0)
        {
            if ((uint)guessX >= (uint)mapWidth || (uint)guessY >= (uint)mapHeight)
            {
                return false;
            }

            tileX = guessX;
            tileY = guessY;
            return true;
        }

        tileX = hitX;
        tileY = hitY;
        return true;
    }

    public static bool DiamondContains(int px, int py, int left, int top, int width, int height)
    {
        if (px < left || py < top || px >= left + width || py >= top + height)
        {
            return false;
        }

        double nx = Math.Abs(px + 0.5 - (left + width / 2.0)) / (width / 2.0);
        double ny = Math.Abs(py + 0.5 - (top + height / 2.0)) / (height / 2.0);
        return nx + ny <= 1.02;
    }
}
