using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DarkAges.Viewer;

internal static class DarkTitleBar
{
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const int DwmwcpRound = 2;

    public static void ApplyToNewWindows()
    {
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnLoaded));
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
        {
            Apply(window);
        }
    }

    public static void Apply(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int dark = 1;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref dark, sizeof(int));

        int round = DwmwcpRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));

        uint caption = ToColorRef(System.Windows.Media.Color.FromRgb(0x1B, 0x1D, 0x23));
        uint text = ToColorRef(System.Windows.Media.Color.FromRgb(0xE8, 0xE6, 0xE3));
        uint border = ToColorRef(System.Windows.Media.Color.FromRgb(0x3A, 0x3F, 0x4B));
        _ = DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(uint));
        _ = DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(uint));
        _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(uint));
    }

    private static uint ToColorRef(System.Windows.Media.Color color) =>
        (uint)(color.R | (color.G << 8) | (color.B << 16));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);
}
