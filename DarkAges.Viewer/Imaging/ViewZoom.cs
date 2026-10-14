using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DarkAges.Viewer.Imaging;

public static class ViewZoom
{
    public const double Min = 0.25;
    public const double Max = 4.0;

    public static readonly string[] Presets =
        ["25%", "50%", "75%", "100%", "150%", "200%", "300%", "400%"];

    public static double Clamp(double zoom) =>
        Math.Clamp(Math.Round(zoom * 100.0) / 100.0, Min, Max);

    public static double Step(double zoom, int wheelDelta)
    {
        double factor = wheelDelta > 0 ? 1.15 : 1.0 / 1.15;
        return Clamp(zoom * factor);
    }

    public static double Parse(object? item)
    {
        if (item is not string text)
        {
            return 1;
        }

        text = text.Trim();
        if (text.EndsWith('%')
            && double.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
        {
            return Clamp(percent / 100.0);
        }

        if (text.EndsWith('x') || text.EndsWith('X'))
        {
            if (double.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double factor))
            {
                return Clamp(factor);
            }
        }

        return 1;
    }

    public static string Label(double zoom) => $"{Math.Round(zoom * 100):0}%";

    public static int ClosestPresetIndex(double zoom)
    {
        int best = 3;
        double bestDelta = double.MaxValue;
        for (int i = 0; i < Presets.Length; i++)
        {
            double delta = Math.Abs(Parse(Presets[i]) - zoom);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                best = i;
            }
        }

        return best;
    }

    public static void Apply(ScaleTransform? transform, double zoom)
    {
        if (transform is null)
        {
            return;
        }

        transform.ScaleX = zoom;
        transform.ScaleY = zoom;
    }

    public static void ScrollToward(ScrollViewer? scroller, System.Windows.Point mouseInScroller, System.Windows.Point contentLocal, double newZoom)
    {
        if (scroller is null)
        {
            return;
        }

        scroller.UpdateLayout();
        scroller.ScrollToHorizontalOffset(Math.Max(0, contentLocal.X * newZoom - mouseInScroller.X));
        scroller.ScrollToVerticalOffset(Math.Max(0, contentLocal.Y * newZoom - mouseInScroller.Y));
    }
}
