namespace DarkAges.Formats.Imaging;

/// <summary>
/// Composites an effect frame onto a small stage with a dummy player at the origin
/// so the full animation can be judged against a body.
/// </summary>
public static class EffectStage
{
    public const int PlayerWidth = 18;
    public const int PlayerHeight = 36;

    public static PixelBuffer Compose(
        PixelBuffer effect,
        int originX,
        int originY,
        bool additive)
    {
        int ox = originX;
        int oy = originY;
        int padLeft = Math.Max(40, ox);
        int padTop = Math.Max(52, oy);
        int padRight = Math.Max(40, effect.Width - ox);
        int padBottom = Math.Max(24, effect.Height - oy);
        var canvas = new PixelBuffer(padLeft + padRight, padTop + padBottom);
        int footX = padLeft;
        int footY = padTop;
        DrawPlayer(canvas, footX, footY);
        if (additive)
        {
            canvas.BlitAdd(effect, footX - ox, footY - oy);
        }
        else
        {
            canvas.Blit(effect, footX - ox, footY - oy);
        }

        return canvas;
    }

    public static PixelBuffer ComposeUnity(PixelBuffer effect, float pivotX, float pivotY)
    {
        int originX = (int)Math.Round(Math.Clamp(pivotX, 0, 1) * Math.Max(1, effect.Width));
        int originY = (int)Math.Round((1f - Math.Clamp(pivotY, 0, 1)) * Math.Max(1, effect.Height));
        return Compose(effect, originX, originY, additive: false);
    }

    public static void DrawPlayer(PixelBuffer canvas, int footX, int footY)
    {
        int cx = footX;
        int fy = footY;
        canvas.FillDiamond(cx - 14, fy - 6, 28, 14, 22, 18, 18, 160);

        FillRectBlend(canvas, cx - 3, fy - 14, 3, 12, 78, 48, 42, 255);
        FillRectBlend(canvas, cx + 1, fy - 14, 3, 12, 78, 48, 42, 255);

        FillRectBlend(canvas, cx - 6, fy - 28, 13, 16, 168, 92, 58, 255);
        FillRectBlend(canvas, cx - 7, fy - 18, 15, 5, 148, 78, 48, 255);

        FillRectBlend(canvas, cx - 8, fy - 26, 4, 10, 92, 62, 52, 255);
        FillRectBlend(canvas, cx + 5, fy - 26, 4, 10, 92, 62, 52, 255);

        FillRectBlend(canvas, cx - 4, fy - 36, 9, 9, 142, 176, 210, 255);
        FillRectBlend(canvas, cx - 5, fy - 35, 2, 4, 32, 36, 48, 255);
        FillRectBlend(canvas, cx + 4, fy - 35, 2, 4, 32, 36, 48, 255);
        canvas.SetPixel(cx, fy - 1, 24, 28, 32, 255);
    }

    private static void FillRectBlend(
        PixelBuffer canvas, int left, int top, int width, int height,
        byte b, byte g, byte r, byte a)
    {
        int x0 = Math.Max(0, left);
        int y0 = Math.Max(0, top);
        int x1 = Math.Min(canvas.Width, left + width);
        int y1 = Math.Min(canvas.Height, top + height);
        byte[] dst = canvas.Bgra;
        int dw = canvas.Width;
        for (int y = y0; y < y1; y++)
        {
            int row = y * dw;
            for (int x = x0; x < x1; x++)
            {
                int i = (row + x) * 4;
                dst[i] = b;
                dst[i + 1] = g;
                dst[i + 2] = r;
                dst[i + 3] = a;
            }
        }
    }
}
