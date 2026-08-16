using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
namespace Harbor.App.Avalonia.Views.Components;
/// <summary>
///     Reusable chat-row component — role pill + message body, with
///     optional timestamp. The role pill's accent color and the message
///     text color are both driven by <see cref="BrushKey" /> (resolved by
///     <c>BrushKeyConverter</c> at bind time).
/// </summary>
/// <remarks>
///     <para>
///         Inspired by React presentational components: this control owns
///         NO state of its own — it just renders four properties
///         (<see cref="RoleLabel" />, <see cref="Text" />,
///         <see cref="BrushKey" />, <see cref="Timestamp" />). The parent
///         owns the data and lets the bubble render it.
///     </para>
/// </remarks>
[PseudoClasses(":empty", ":compact")]
public sealed partial class ChatBubble : UserControl
{
    /// <summary>Property for <see cref="RoleLabel" />.</summary>
    public static readonly StyledProperty<string> RoleLabelProperty =
        AvaloniaProperty.Register<ChatBubble, string>(nameof(RoleLabel), "user");

    /// <summary>Property for <see cref="Text" />.</summary>
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<ChatBubble, string>(nameof(Text), string.Empty);

    /// <summary>Property for <see cref="BrushKey" />.</summary>
    public static readonly StyledProperty<string> BrushKeyProperty =
        AvaloniaProperty.Register<ChatBubble, string>(nameof(BrushKey), "ChatUserBrush");

    /// <summary>Property for <see cref="Timestamp" />.</summary>
    public static readonly StyledProperty<string?> TimestampProperty =
        AvaloniaProperty.Register<ChatBubble, string?>(nameof(Timestamp));

    /// <summary>Property for <see cref="IsCompact" />.</summary>
    public static readonly StyledProperty<bool> IsCompactProperty =
        AvaloniaProperty.Register<ChatBubble, bool>(nameof(IsCompact));

    /// <summary>Construct the bubble.</summary>
    public ChatBubble()
    {
        // Skip InitializeComponent in headless test mode (no Application
        // means ReflectionBinding throws). Real apps still call it via
        // the auto-generated partial class from the AXAML compile task.
        if (Application.Current is not null)
        {
            InitializeComponent();
        }
        UpdatePseudoClasses();
        this.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextProperty || e.Property == IsCompactProperty)
            {
                UpdatePseudoClasses();
            }
        };
    }

    /// <summary>Short role label shown in the left pill ("user" / "assistant" / etc.).</summary>
    public string RoleLabel
    {
        get => this.GetValue(RoleLabelProperty);
        set => this.SetValue(RoleLabelProperty, value);
    }

    /// <summary>Message body text (plain text for now; Markdown pending).</summary>
    public string Text
    {
        get => this.GetValue(TextProperty);
        set => this.SetValue(TextProperty, value);
    }

    /// <summary>Resource key for the role accent color (resolved by BrushKeyConverter).</summary>
    public string BrushKey
    {
        get => this.GetValue(BrushKeyProperty);
        set => this.SetValue(BrushKeyProperty, value);
    }

    /// <summary>Optional timestamp string. When null/empty, the timestamp row is hidden.</summary>
    public string? Timestamp
    {
        get => this.GetValue(TimestampProperty);
        set => this.SetValue(TimestampProperty, value);
    }

    /// <summary>Toggle compact padding (smaller bubble for tool/system lines).</summary>
    public bool IsCompact
    {
        get => this.GetValue(IsCompactProperty);
        set => this.SetValue(IsCompactProperty, value);
    }

    private void UpdatePseudoClasses()
    {
        this.PseudoClasses.Set(":empty", string.IsNullOrEmpty(Text));
        this.PseudoClasses.Set(":compact", IsCompact);
    }
}
