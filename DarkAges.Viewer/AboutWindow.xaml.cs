using System.Windows;

namespace DarkAges.Viewer;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        TitleText.Text = AppInfo.Name;
        AuthorText.Text = AppInfo.Author;
        VersionText.Text = AppInfo.Version;
        UpdatedText.Text = AppInfo.LastUpdated;
    }
}
