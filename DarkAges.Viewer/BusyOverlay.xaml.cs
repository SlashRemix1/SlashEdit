using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace DarkAges.Viewer;

public partial class BusyOverlay : System.Windows.Controls.UserControl
{
    private Storyboard? _spin;
    private int _depth;
    private Action? _onCancel;

    public BusyOverlay()
    {
        InitializeComponent();
    }

    public bool IsBusy => _depth > 0 || Visibility == Visibility.Visible;

    public bool CanCancel => _onCancel is not null;

    public void ShowBusy(string message, bool determinate = false)
    {
        _depth++;
        MessageText.Text = string.IsNullOrWhiteSpace(message) ? "Working…" : message;
        Bar.IsIndeterminate = !determinate;
        if (determinate)
        {
            Bar.Minimum = 0;
            Bar.Maximum = 1;
            Bar.Value = 0;
        }

        CancelButton.IsEnabled = true;
        CancelButton.Content = "Cancel";
        Visibility = Visibility.Visible;
        _spin ??= (Storyboard)FindResource("Spin");
        _spin.Begin();
        Focus();
    }

    public void SetCancellable(Action? onCancel)
    {
        _onCancel = onCancel;
        CancelButton.Visibility = onCancel is null ? Visibility.Collapsed : Visibility.Visible;
        if (onCancel is not null && IsVisible)
        {
            CancelButton.Focus();
        }
    }

    public void SetMessage(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            MessageText.Text = message;
        }
    }

    public void SetProgress(int done, int total, string? message = null)
    {
        if (total > 0)
        {
            Bar.IsIndeterminate = false;
            Bar.Minimum = 0;
            Bar.Maximum = total;
            Bar.Value = Math.Clamp(done, 0, total);
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            MessageText.Text = message;
        }
        else if (total > 0)
        {
            MessageText.Text = $"{done} / {total}";
        }
    }

    public void HideBusy()
    {
        _depth = Math.Max(0, _depth - 1);
        if (_depth > 0)
        {
            return;
        }

        _onCancel = null;
        CancelButton.Visibility = Visibility.Collapsed;
        CancelButton.IsEnabled = true;
        CancelButton.Content = "Cancel";
        _spin?.Stop();
        Visibility = Visibility.Collapsed;
        Bar.IsIndeterminate = true;
    }

    private void CancelClicked(object sender, RoutedEventArgs e) => RequestCancel();

    private void OverlayPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _onCancel is not null)
        {
            RequestCancel();
            e.Handled = true;
        }
    }

    private void RequestCancel()
    {
        Action? cancel = _onCancel;
        if (cancel is null)
        {
            return;
        }

        CancelButton.IsEnabled = false;
        CancelButton.Content = "Cancelling…";
        MessageText.Text = "Cancelling…";
        cancel();
    }
}
