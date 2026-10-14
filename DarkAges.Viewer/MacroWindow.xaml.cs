using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DarkAges.Viewer;

public partial class MacroWindow : Window
{
    public MacroWindow()
    {
        InitializeComponent();
        SlotHost.ItemsSource = Slots;
    }

    public ObservableCollection<MacroSlotView> Slots { get; } = [];

    public event Action<int>? SlotSet;
    public event Action<int>? SlotClear;
    public event Action<int>? SlotApply;

    public void Bind(IReadOnlyList<MacroSlotView> slots)
    {
        Slots.Clear();
        foreach (MacroSlotView slot in slots)
        {
            Slots.Add(slot);
        }
    }

    private void SetClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: int key })
        {
            SlotSet?.Invoke(key);
        }
    }

    private void ClearClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: int key })
        {
            SlotClear?.Invoke(key);
        }
    }

    private void PreviewClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: int key })
        {
            SlotApply?.Invoke(key);
        }
    }

    private void WindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (MapEditorWindow.TryMacroKey(e.Key, out int key) && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            SlotApply?.Invoke(key);
        }
    }
}

public sealed class MacroSlotView : INotifyPropertyChanged
{
    private static readonly SolidColorBrush IdleBorder = Freeze(System.Windows.Media.Color.FromRgb(58, 63, 75));
    private static readonly SolidColorBrush ActiveBorder = Freeze(System.Windows.Media.Color.FromRgb(110, 168, 254));
    private static readonly SolidColorBrush EmptyBorder = Freeze(System.Windows.Media.Color.FromRgb(42, 46, 54));

    private BitmapSource? _image;
    private string _label = "empty";
    private string _caption = "";
    private bool _hasStamp;
    private bool _selected;

    public MacroSlotView(int key)
    {
        Key = key;
        _caption = key.ToString();
    }

    public int Key { get; }

    public string Caption
    {
        get => _caption;
        set
        {
            if (_caption == value)
            {
                return;
            }

            _caption = value;
            OnPropertyChanged();
        }
    }

    public string Label
    {
        get => _label;
        set
        {
            if (_label == value)
            {
                return;
            }

            _label = value;
            OnPropertyChanged();
        }
    }

    public BitmapSource? Image
    {
        get => _image;
        set
        {
            _image = value;
            OnPropertyChanged();
        }
    }

    public bool HasStamp
    {
        get => _hasStamp;
        set
        {
            if (_hasStamp == value)
            {
                return;
            }

            _hasStamp = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SlotBorder));
            OnPropertyChanged(nameof(SlotOpacity));
        }
    }

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
            {
                return;
            }

            _selected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SlotBorder));
        }
    }

    public System.Windows.Media.Brush SlotBorder => _selected ? ActiveBorder : _hasStamp ? IdleBorder : EmptyBorder;

    public double SlotOpacity => _hasStamp ? 1 : 0.45;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static SolidColorBrush Freeze(System.Windows.Media.Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
