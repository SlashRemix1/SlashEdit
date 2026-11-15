namespace DarkAges.Viewer.Services;

public static class PlaybackSpeed
{
    public static readonly int[] FpsChoices = [2, 4, 6, 8, 10, 12, 15, 20, 30];
    public const int DefaultFps = 10;

    public static int ClampFps(int fps)
    {
        foreach (int choice in FpsChoices)
        {
            if (choice == fps)
            {
                return fps;
            }
        }

        return DefaultFps;
    }

    public static TimeSpan Interval(int fps)
    {
        int safe = Math.Max(1, ClampFps(fps));
        double ms = Math.Clamp(1000.0 / safe, 16, 2000);
        return TimeSpan.FromMilliseconds(ms);
    }

    public static string Label(int fps) => $"{ClampFps(fps)} fps";
}
