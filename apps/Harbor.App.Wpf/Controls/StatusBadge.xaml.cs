using System.Windows;
using System.Windows.Controls;

namespace Harbor.App.Wpf.Controls;

/// <summary>
///     Reusable WPF status badge — colored dot + label pill. Mirrors the
///     Avalonia <c>Harbor.App.Avalonia.Views.Components.StatusBadge</c>
///     control.
/// </summary>
public partial class StatusBadge : UserControl
{
    public static readonly DependencyProperty StatusTextProperty =
        DependencyProperty.Register(nameof(StatusText), typeof(string), typeof(StatusBadge),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty BrushKeyProperty =
        DependencyProperty.Register(nameof(BrushKey), typeof(string), typeof(StatusBadge),
            new PropertyMetadata("StatusIdleBrush"));

    public static readonly DependencyProperty ShowDotProperty =
        DependencyProperty.Register(nameof(ShowDot), typeof(bool), typeof(StatusBadge),
            new PropertyMetadata(true));

    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public string BrushKey
    {
        get => (string)GetValue(BrushKeyProperty);
        set => SetValue(BrushKeyProperty, value);
    }

    public bool ShowDot
    {
        get => (bool)GetValue(ShowDotProperty);
        set => SetValue(ShowDotProperty, value);
    }

    public StatusBadge()
    {
        InitializeComponent();
    }
}
