using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DarkAges.Formats.Drawing;
using DarkAges.Formats.Imaging;
using WinForms = System.Windows.Forms;

namespace DarkAges.Viewer;

public partial class PixelPaintControl : System.Windows.Controls.UserControl
{
    private enum Tool
    {
        Pencil,
        Fill,
        Eyedropper
    }

    private readonly Stack<PixelBuffer> _undo = new();
    private readonly Stack<PixelBuffer> _redo = new();
    private PixelBuffer? _pixels;
    private Palette? _palette;
    private WriteableBitmap? _bitmap;
    private Tool _tool = Tool.Pencil;
    private int _zoom = 8;
    private byte _b = 255, _g = 255, _r = 255, _a = 255;
    private bool _drawing;
    private bool _erasing;
    private bool _panning;
    private bool _loading;
    private int _lastX = int.MinValue;
    private int _lastY = int.MinValue;
    private System.Windows.Point _panStart;
    private double _panOffsetX;
    private double _panOffsetY;

    public PixelPaintControl()
    {
        InitializeComponent();
        UpdateColorSwatch();
        UpdateZoomLabel();
    }

    public event EventHandler? PixelsChanged;

    public PixelBuffer? Pixels => _pixels;

    public void Load(PixelBuffer? pixels, Palette? palette = null)
    {
        _loading = true;
        _undo.Clear();
        _redo.Clear();
        _pixels = pixels?.Clone();
        _palette = palette;
        _drawing = false;
        RebuildPaletteStrip();
        RebuildBitmap();
        _loading = false;
    }

    public void Clear() => Load(null, null);

    private void ToolChanged(object sender, RoutedEventArgs e)
    {
        if (FillBox is null || EyedropBox is null)
        {
            return;
        }

        if (FillBox.IsChecked == true)
        {
            _tool = Tool.Fill;
        }
        else if (EyedropBox.IsChecked == true)
        {
            _tool = Tool.Eyedropper;
        }
        else
        {
            _tool = Tool.Pencil;
        }
    }

    private void ZoomInClicked(object sender, RoutedEventArgs e) => SetZoom(_zoom + 1);

    private void ZoomOutClicked(object sender, RoutedEventArgs e) => SetZoom(_zoom - 1);

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        SetZoom(_zoom + (e.Delta > 0 ? 1 : -1));
        e.Handled = true;
    }

    private void SetZoom(int zoom)
    {
        _zoom = Math.Clamp(zoom, 1, 32);
        UpdateZoomLabel();
        RebuildBitmap();
    }

    private void UpdateZoomLabel()
    {
        if (ZoomLabel is not null)
        {
            ZoomLabel.Text = $"{_zoom}×";
        }
    }

    private void GridChanged(object sender, RoutedEventArgs e)
    {
        if (CanvasImage is not null)
        {
            RebuildBitmap();
        }
    }

    private void ColorSwatchClicked(object sender, MouseButtonEventArgs e)
    {
        using var dialog = new WinForms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(_a, _r, _g, _b),
            FullOpen = true
        };
        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        SetColor(dialog.Color.B, dialog.Color.G, dialog.Color.R, 255);
    }

    private void UndoClicked(object sender, RoutedEventArgs e) => Undo();

    private void RedoClicked(object sender, RoutedEventArgs e) => Redo();

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Undo();
            e.Handled = true;
        }
        else if ((e.Key == Key.Y && Keyboard.Modifiers == ModifierKeys.Control)
                 || (e.Key == Key.Z && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)))
        {
            Redo();
            e.Handled = true;
        }
    }

    private void OnMiddleDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        _panning = true;
        _panStart = e.GetPosition(Scroller);
        _panOffsetX = Scroller.HorizontalOffset;
        _panOffsetY = Scroller.VerticalOffset;
        Scroller.CaptureMouse();
        e.Handled = true;
    }

    private void OnMiddleUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        _panning = false;
        if (Scroller.IsMouseCaptured)
        {
            Scroller.ReleaseMouseCapture();
        }
    }

    private void OnSurfaceDown(object sender, MouseButtonEventArgs e)
    {
        if (_pixels is null || _panning)
        {
            return;
        }

        Focus();
        if (!TryMap(e.GetPosition(CanvasImage), out int x, out int y))
        {
            return;
        }

        _erasing = e.ChangedButton == MouseButton.Right && _tool != Tool.Eyedropper;
        if (_tool == Tool.Eyedropper || e.ChangedButton == MouseButton.Right && Keyboard.Modifiers == ModifierKeys.Alt)
        {
            Sample(x, y);
            return;
        }

        PushUndo();
        _drawing = true;
        _lastX = x;
        _lastY = y;
        Scroller.CaptureMouse();
        if (_tool == Tool.Fill)
        {
            Fill(x, y);
            FinishStroke();
            return;
        }

        Stamp(x, y);
        Present();
    }

    private void OnSurfaceMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_panning && e.MiddleButton == MouseButtonState.Pressed)
        {
            System.Windows.Point now = e.GetPosition(Scroller);
            Scroller.ScrollToHorizontalOffset(_panOffsetX - (now.X - _panStart.X));
            Scroller.ScrollToVerticalOffset(_panOffsetY - (now.Y - _panStart.Y));
            return;
        }

        if (!_drawing || _pixels is null)
        {
            return;
        }

        if (!TryMap(e.GetPosition(CanvasImage), out int x, out int y))
        {
            return;
        }

        Line(_lastX, _lastY, x, y);
        _lastX = x;
        _lastY = y;
        Present();
    }

    private void OnSurfaceUp(object sender, MouseButtonEventArgs e)
    {
        if (_drawing)
        {
            FinishStroke();
        }
    }

    private void OnSurfaceLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_drawing && e.LeftButton != MouseButtonState.Pressed && e.RightButton != MouseButtonState.Pressed)
        {
            FinishStroke();
        }
    }

    private void FinishStroke()
    {
        _drawing = false;
        _lastX = int.MinValue;
        if (Scroller.IsMouseCaptured)
        {
            Scroller.ReleaseMouseCapture();
        }

        Present();
        if (!_loading)
        {
            PixelsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool TryMap(System.Windows.Point pos, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (_pixels is null || _zoom <= 0)
        {
            return false;
        }

        x = (int)Math.Floor(pos.X / _zoom);
        y = (int)Math.Floor(pos.Y / _zoom);
        return (uint)x < (uint)_pixels.Width && (uint)y < (uint)_pixels.Height;
    }

    private void Stamp(int cx, int cy)
    {
        if (_pixels is null)
        {
            return;
        }

        int size = Math.Max(1, (int)SizeSlider.Value);
        int x0 = cx - size / 2;
        int y0 = cy - size / 2;
        byte b = _erasing ? (byte)0 : _b;
        byte g = _erasing ? (byte)0 : _g;
        byte r = _erasing ? (byte)0 : _r;
        byte a = _erasing ? (byte)0 : _a;
        Snap(ref b, ref g, ref r, ref a);
        _pixels.FillRect(x0, y0, size, size, b, g, r, a);
    }

    private void Line(int x0, int y0, int x1, int y1)
    {
        int dx = Math.Abs(x1 - x0);
        int dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        while (true)
        {
            Stamp(x0, y0);
            if (x0 == x1 && y0 == y1)
            {
                break;
            }

            int e2 = 2 * err;
            if (e2 > -dy)
            {
                err -= dy;
                x0 += sx;
            }

            if (e2 < dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    private void Fill(int x, int y)
    {
        if (_pixels is null)
        {
            return;
        }

        var target = _pixels.GetPixel(x, y);
        byte b = _erasing ? (byte)0 : _b;
        byte g = _erasing ? (byte)0 : _g;
        byte r = _erasing ? (byte)0 : _r;
        byte a = _erasing ? (byte)0 : _a;
        Snap(ref b, ref g, ref r, ref a);
        if (target == (b, g, r, a))
        {
            return;
        }

        var stack = new Stack<(int X, int Y)>();
        stack.Push((x, y));
        int guard = _pixels.Width * _pixels.Height + 8;
        while (stack.Count > 0 && guard-- > 0)
        {
            (int px, int py) = stack.Pop();
            if ((uint)px >= (uint)_pixels.Width || (uint)py >= (uint)_pixels.Height)
            {
                continue;
            }

            if (_pixels.GetPixel(px, py) != target)
            {
                continue;
            }

            _pixels.SetPixel(px, py, b, g, r, a);
            stack.Push((px + 1, py));
            stack.Push((px - 1, py));
            stack.Push((px, py + 1));
            stack.Push((px, py - 1));
        }
    }

    private void Sample(int x, int y)
    {
        if (_pixels is null)
        {
            return;
        }

        var (b, g, r, a) = _pixels.GetPixel(x, y);
        if (a == 0)
        {
            return;
        }

        SetColor(b, g, r, a);
    }

    private void Snap(ref byte b, ref byte g, ref byte r, ref byte a)
    {
        if (_palette is null || a < 16)
        {
            return;
        }

        byte index = _palette.NearestIndex(r, g, b, a);
        if (index == 0)
        {
            b = 0;
            g = 0;
            r = 0;
            a = 0;
            return;
        }

        var (pr, pg, pb) = _palette[index];
        r = pr;
        g = pg;
        b = pb;
        a = 255;
    }

    private void SetColor(byte b, byte g, byte r, byte a)
    {
        _b = b;
        _g = g;
        _r = r;
        _a = a;
        UpdateColorSwatch();
        PencilBox.IsChecked = true;
        _tool = Tool.Pencil;
    }

    private void UpdateColorSwatch() =>
        ColorSwatch.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(_a == 0 ? (byte)255 : _a, _r, _g, _b));

    private void PushUndo()
    {
        if (_pixels is null)
        {
            return;
        }

        _undo.Push(_pixels.Clone());
        _redo.Clear();
        if (_undo.Count <= 32)
        {
            return;
        }

        PixelBuffer[] kept = _undo.Take(32).Reverse().ToArray();
        _undo.Clear();
        foreach (PixelBuffer item in kept)
        {
            _undo.Push(item);
        }
    }

    private void Undo()
    {
        if (_undo.Count == 0 || _pixels is null)
        {
            return;
        }

        _redo.Push(_pixels.Clone());
        _pixels = _undo.Pop();
        RebuildBitmap();
        PixelsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        if (_pixels is not null)
        {
            _undo.Push(_pixels.Clone());
        }

        _pixels = _redo.Pop();
        RebuildBitmap();
        PixelsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildPaletteStrip()
    {
        PaletteHost.Children.Clear();
        if (_palette is null)
        {
            PaletteScroller.Visibility = Visibility.Collapsed;
            return;
        }

        PaletteScroller.Visibility = Visibility.Visible;
        for (int i = 0; i < 256; i++)
        {
            var (r, g, b) = _palette[i];
            int index = i;
            var swatch = new System.Windows.Shapes.Rectangle
            {
                Width = 12,
                Height = 12,
                Margin = new Thickness(1),
                Fill = i == 0
                    ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 40, 48))
                    : new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b)),
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(58, 63, 75)),
                StrokeThickness = 1,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = i == 0 ? "Transparent (0)" : $"Index {i}  #{r:X2}{g:X2}{b:X2}"
            };
            swatch.MouseLeftButtonDown += (_, _) =>
            {
                if (index == 0)
                {
                    SetColor(0, 0, 0, 0);
                    return;
                }

                SetColor(b, g, r, 255);
            };
            PaletteHost.Children.Add(swatch);
        }
    }

    private void RebuildBitmap()
    {
        if (CanvasImage is null)
        {
            return;
        }

        if (_pixels is null)
        {
            CanvasImage.Source = null;
            CanvasImage.Width = 0;
            CanvasImage.Height = 0;
            if (HintText is not null)
            {
                HintText.Text = "Select a sprite to paint.";
            }

            return;
        }

        if (HintText is not null)
        {
            HintText.Text = $"{_pixels.Width}×{_pixels.Height}  wheel zoom  middle-pan  right-erase";
        }

        Present();
    }

    private void Present()
    {
        if (_pixels is null || CanvasImage is null)
        {
            return;
        }

        int zw = _pixels.Width * _zoom;
        int zh = _pixels.Height * _zoom;
        if (_bitmap is null || _bitmap.PixelWidth != zw || _bitmap.PixelHeight != zh)
        {
            _bitmap = new WriteableBitmap(Math.Max(1, zw), Math.Max(1, zh), 96, 96, PixelFormats.Bgra32, null);
            CanvasImage.Source = _bitmap;
            CanvasImage.Width = zw;
            CanvasImage.Height = zh;
        }

        var scaled = new byte[zw * zh * 4];
        bool grid = GridBox?.IsChecked == true && _zoom >= 6;
        for (int y = 0; y < _pixels.Height; y++)
        {
            for (int x = 0; x < _pixels.Width; x++)
            {
                var (b, g, r, a) = _pixels.GetPixel(x, y);
                if (a == 0)
                {
                    bool dark = ((x + y) & 1) == 0;
                    b = dark ? (byte)40 : (byte)55;
                    g = b;
                    r = b;
                    a = 255;
                }

                for (int oy = 0; oy < _zoom; oy++)
                {
                    int row = ((y * _zoom + oy) * zw + x * _zoom) * 4;
                    for (int ox = 0; ox < _zoom; ox++)
                    {
                        int i = row + ox * 4;
                        bool line = grid && (ox == 0 || oy == 0);
                        scaled[i] = line ? (byte)(b / 2) : b;
                        scaled[i + 1] = line ? (byte)(g / 2) : g;
                        scaled[i + 2] = line ? (byte)(r / 2) : r;
                        scaled[i + 3] = 255;
                    }
                }
            }
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, zw, zh), scaled, zw * 4, 0);
    }
}
