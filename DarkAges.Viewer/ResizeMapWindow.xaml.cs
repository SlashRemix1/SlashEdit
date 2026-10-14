using System.Windows;
using System.Windows.Controls;
using DarkAges.Formats.Maps;

namespace DarkAges.Viewer;

public partial class ResizeMapWindow : Window
{
    private readonly int _width;
    private readonly int _height;

    public ResizeMapWindow(int width, int height, int fillFloor)
    {
        InitializeComponent();
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        FillBox.Text = Math.Max(0, fillFloor).ToString();
        CurrentSizeText.Text = $"Current size  {_width} × {_height}";
        UpdatePreview();
    }

    public int LeftPad { get; private set; }
    public int RightPad { get; private set; }
    public int UpPad { get; private set; }
    public int DownPad { get; private set; }
    public ushort FillFloor { get; private set; }
    public int NewWidth { get; private set; }
    public int NewHeight { get; private set; }

    public bool HasChange => LeftPad != 0 || RightPad != 0 || UpPad != 0 || DownPad != 0;

    private void ValuesChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (NewSizeText is null)
        {
            return;
        }

        if (!TryReadSides(out int left, out int right, out int up, out int down))
        {
            NewSizeText.Text = "Enter whole numbers (negatives shrink).";
            return;
        }

        if (!MapFile.TryPadSize(_width, _height, left, up, right, down, out int width, out int height))
        {
            NewSizeText.Text = $"That size is outside 1–{MapFile.MaxDimension}.";
            return;
        }

        NewSizeText.Text = $"New size  {width} × {height}";
    }

    private void ApplyClicked(object sender, RoutedEventArgs e)
    {
        if (!TryReadSides(out int left, out int right, out int up, out int down))
        {
            System.Windows.MessageBox.Show(this, "X left, X right, Y up, and Y down must be whole numbers.");
            return;
        }

        if (!MapFile.TryPadSize(_width, _height, left, up, right, down, out int width, out int height))
        {
            System.Windows.MessageBox.Show(this, $"Resulting size must be between 1×1 and {MapFile.MaxDimension}×{MapFile.MaxDimension}.");
            return;
        }

        ushort fill = 0;
        ushort.TryParse(FillBox.Text.Trim(), out fill);
        LeftPad = left;
        RightPad = right;
        UpPad = up;
        DownPad = down;
        FillFloor = fill;
        NewWidth = width;
        NewHeight = height;
        DialogResult = true;
    }

    private bool TryReadSides(out int left, out int right, out int up, out int down)
    {
        left = right = up = down = 0;
        return TryParseSide(LeftBox.Text, out left)
            && TryParseSide(RightBox.Text, out right)
            && TryParseSide(UpBox.Text, out up)
            && TryParseSide(DownBox.Text, out down);
    }

    private static bool TryParseSide(string? text, out int value)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0)
        {
            value = 0;
            return true;
        }

        return int.TryParse(text, out value);
    }
}
