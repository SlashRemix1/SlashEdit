using System.Windows;
using DarkAges.Formats.Maps;

namespace DarkAges.Viewer;

public partial class NewMapWindow : Window
{
    public NewMapWindow()
    {
        InitializeComponent();
    }

    public string FileName => NameBox.Text.Trim();
    public int MapWidth { get; private set; }
    public int MapHeight { get; private set; }
    public ushort FillFloor { get; private set; }

    private void CreateClicked(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WidthBox.Text.Trim(), out int width) || width < 1 || width > MapFile.MaxDimension
            || !int.TryParse(HeightBox.Text.Trim(), out int height) || height < 1 || height > MapFile.MaxDimension)
        {
            System.Windows.MessageBox.Show(this, $"Width and height must be between 1 and {MapFile.MaxDimension}.");
            return;
        }

        ushort fill = 0;
        ushort.TryParse(FillBox.Text.Trim(), out fill);
        if (string.IsNullOrWhiteSpace(FileName))
        {
            System.Windows.MessageBox.Show(this, "Enter a file name.");
            return;
        }

        MapWidth = width;
        MapHeight = height;
        FillFloor = fill;
        DialogResult = true;
    }
}
