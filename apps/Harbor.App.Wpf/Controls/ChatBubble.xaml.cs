using System.Windows;
using System.Windows.Controls;

namespace Harbor.App.Wpf.Controls;

/// <summary>
///     Reusable WPF chat-row component — role pill + message body, with
///     optional timestamp. Mirrors the Avalonia
///     <c>Harbor.App.Avalonia.Views.Components.ChatBubble</c> control so
///     the same look is shared across both desktop frameworks.
/// </summary>
/// <remarks>
///     Bindable properties (set on the control itself):
///     <list type="bullet">
///         <item><c>RoleLabel</c> — short lowercase role label</item>
///         <item><c>Text</c> — message body</item>
///         <item><c>BrushKey</c> — resource key for the role accent color</item>
///         <item><c>Timestamp</c> — optional timestamp string</item>
///         <item><c>IsCompact</c> — toggle compact padding</item>
///     </list>
/// </remarks>
public partial class ChatBubble : UserControl
{
    /// <summary>Dependency property for <see cref="RoleLabel"/>.</summary>
    public static readonly DependencyProperty RoleLabelProperty =
        DependencyProperty.Register(nameof(RoleLabel), typeof(string), typeof(ChatBubble),
            new PropertyMetadata("user"));

    /// <summary>Dependency property for <see cref="Text"/>.</summary>
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(ChatBubble),
            new PropertyMetadata(string.Empty));

    /// <summary>Dependency property for <see cref="BrushKey"/>.</summary>
    public static readonly DependencyProperty BrushKeyProperty =
        DependencyProperty.Register(nameof(BrushKey), typeof(string), typeof(ChatBubble),
            new PropertyMetadata("ChatUserBrush"));

    /// <summary>Dependency property for <see cref="Timestamp"/>.</summary>
    public static readonly DependencyProperty TimestampProperty =
        DependencyProperty.Register(nameof(Timestamp), typeof(string), typeof(ChatBubble),
            new PropertyMetadata(null));

    /// <summary>Dependency property for <see cref="IsCompact"/>.</summary>
    public static readonly DependencyProperty IsCompactProperty =
        DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(ChatBubble),
            new PropertyMetadata(false));

    /// <summary>Short role label shown in the left pill.</summary>
    public string RoleLabel
    {
        get => (string)GetValue(RoleLabelProperty);
        set => SetValue(RoleLabelProperty, value);
    }

    /// <summary>Message body text.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Resource key for the role accent color.</summary>
    public string BrushKey
    {
        get => (string)GetValue(BrushKeyProperty);
        set => SetValue(BrushKeyProperty, value);
    }

    /// <summary>Optional timestamp string.</summary>
    public string? Timestamp
    {
        get => (string?)GetValue(TimestampProperty);
        set => SetValue(TimestampProperty, value);
    }

    /// <summary>Toggle compact padding.</summary>
    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    /// <summary>Construct the bubble.</summary>
    public ChatBubble()
    {
        InitializeComponent();
    }
}
