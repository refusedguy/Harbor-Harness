using System.Windows;
using System.Windows.Controls;

namespace Harbor.App.Wpf.Controls;

/// <summary>
///     Reusable WPF sidebar row for a session. Mirrors the Avalonia
///     <c>Harbor.App.Avalonia.Views.Components.SessionRow</c> control so
///     the same look is shared across both desktop frameworks.
/// </summary>
public partial class SessionRow : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SessionRow),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(SessionRow),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty RelativeTimeProperty =
        DependencyProperty.Register(nameof(RelativeTime), typeof(string), typeof(SessionRow),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty MessageCountProperty =
        DependencyProperty.Register(nameof(MessageCount), typeof(int), typeof(SessionRow),
            new PropertyMetadata(0));

    public static readonly DependencyProperty StatusColorKeyProperty =
        DependencyProperty.Register(nameof(StatusColorKey), typeof(string), typeof(SessionRow),
            new PropertyMetadata("MochaOverlay0"));

    public static readonly DependencyProperty IsDirtyProperty =
        DependencyProperty.Register(nameof(IsDirty), typeof(bool), typeof(SessionRow),
            new PropertyMetadata(false));

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(SessionRow),
            new PropertyMetadata(false));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public string RelativeTime
    {
        get => (string)GetValue(RelativeTimeProperty);
        set => SetValue(RelativeTimeProperty, value);
    }

    public int MessageCount
    {
        get => (int)GetValue(MessageCountProperty);
        set => SetValue(MessageCountProperty, value);
    }

    public string StatusColorKey
    {
        get => (string)GetValue(StatusColorKeyProperty);
        set => SetValue(StatusColorKeyProperty, value);
    }

    public bool IsDirty
    {
        get => (bool)GetValue(IsDirtyProperty);
        set => SetValue(IsDirtyProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public SessionRow()
    {
        InitializeComponent();
    }
}
