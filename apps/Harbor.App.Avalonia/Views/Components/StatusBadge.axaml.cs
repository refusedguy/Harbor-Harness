using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
namespace Harbor.App.Avalonia.Views.Components;
/// <summary>
///     Reusable status badge — colored dot + label, packed in a pill.
///     Bind <see cref="StatusText" /> (the label) and <see cref="BrushKey" />
///     (the resource key for the dot fill, resolved at bind time via
///     <c>BrushKeyConverter</c>).
/// </summary>
/// <remarks>
///     <para>
///         Inspired by React presentational components: this is a pure
///         presentational component with no business logic. The parent
///         owns the state and just hands us two strings. Multiple parents
///         (status bar, session row, tool-call card header) can reuse it
///         without each one re-implementing the dot+label layout.
///     </para>
/// </remarks>
[PseudoClasses(":empty")]
public sealed partial class StatusBadge : UserControl
{
    /// <summary>Property for <see cref="StatusText" />.</summary>
    public static readonly StyledProperty<string> StatusTextProperty =
        AvaloniaProperty.Register<StatusBadge, string>(nameof(StatusText), string.Empty);

    /// <summary>Property for <see cref="BrushKey" />.</summary>
    public static readonly StyledProperty<string> BrushKeyProperty =
        AvaloniaProperty.Register<StatusBadge, string>(nameof(BrushKey), "StatusIdleBrush");

    /// <summary>Property for <see cref="ShowDot" />.</summary>
    public static readonly StyledProperty<bool> ShowDotProperty =
        AvaloniaProperty.Register<StatusBadge, bool>(nameof(ShowDot), true);

    /// <summary>Construct the badge.</summary>
    public StatusBadge()
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
            if (e.Property == StatusTextProperty)
            {
                UpdatePseudoClasses();
            }
        };
    }

    /// <summary>Label text shown inside the badge.</summary>
    public string StatusText
    {
        get => this.GetValue(StatusTextProperty);
        set => this.SetValue(StatusTextProperty, value);
    }

    /// <summary>Resource key for the dot fill (resolved by BrushKeyConverter).</summary>
    public string BrushKey
    {
        get => this.GetValue(BrushKeyProperty);
        set => this.SetValue(BrushKeyProperty, value);
    }

    /// <summary>Toggle the leading ellipse. Default <c>true</c>.</summary>
    public bool ShowDot
    {
        get => this.GetValue(ShowDotProperty);
        set => this.SetValue(ShowDotProperty, value);
    }

    private void UpdatePseudoClasses() => this.PseudoClasses.Set(":empty", string.IsNullOrEmpty(StatusText));
}
