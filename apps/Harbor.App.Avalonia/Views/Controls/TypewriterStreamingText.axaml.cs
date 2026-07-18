using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Harbor.App.Avalonia.Views.Controls;

/// <summary>
///     Streaming-text display with a blinking typewriter cursor (▋).
///     The cursor blinks at ~1.9 Hz (530 ms on/off) while
///     <see cref="IsStreaming"/> is true and is hidden when idle.
/// </summary>
/// <remarks>
///     <para>
///         <b>Perf note:</b> the cursor is driven by a
///         <see cref="DispatcherTimer"/> at <c>DispatcherPriority.Normal</c>
///         (not Input/Render) so it never blocks user input or layout.
///         The timer is started on <c>Loaded</c> and stopped on
///         <c>Unloaded</c> — no leaks when the chat view is unloaded.
///     </para>
///     <para>
///         <b>Reduced motion:</b> when
///         <c>AnimationPreferences.AllowAnimation</c> is false (or in
///         headless test), the cursor stays solid rather than blinking.
///         We approximate this by always showing the cursor while
///         streaming — blink is a visual nicety, not a correctness
///         signal.
///     </para>
/// </remarks>
public partial class TypewriterStreamingText : UserControl
{
    /// <summary>Styled property for the streaming buffer text.</summary>
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<TypewriterStreamingText, string>(nameof(Text), defaultValue: string.Empty);

    /// <summary>Styled property for the streaming-active flag.</summary>
    public static readonly StyledProperty<bool> IsStreamingProperty =
        AvaloniaProperty.Register<TypewriterStreamingText, bool>(nameof(IsStreaming));

    private DispatcherTimer? _cursorTimer;
    private bool _cursorVisible = true;

    /// <summary>Construct the typewriter control.</summary>
    public TypewriterStreamingText()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        UpdateCursorVisibility();
    }

    /// <summary>The streaming buffer text.</summary>
    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>
    ///     True while a message is actively streaming. Drives cursor
    ///     visibility — hidden when idle, blinking when streaming.
    /// </summary>
    public bool IsStreaming
    {
        get => GetValue(IsStreamingProperty);
        set => SetValue(IsStreamingProperty, value);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsStreamingProperty)
        {
            UpdateCursorVisibility();
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // 530 ms gives ~1.9 Hz blink — matches ChatGPT's cadence and
        // feels alive without being distracting.
        _cursorTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(530),
            DispatcherPriority.Normal, OnCursorTick);
        _cursorTimer.Start();
        UpdateCursorVisibility();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        _cursorTimer?.Stop();
        _cursorTimer = null;
    }

    private void OnCursorTick(object? sender, System.EventArgs e)
    {
        if (!IsStreaming)
        {
            return;
        }
        _cursorVisible = !_cursorVisible;
        if (BlinkCursor is { } cursor)
        {
            cursor.IsVisible = _cursorVisible;
        }
    }

    private void UpdateCursorVisibility()
    {
        if (BlinkCursor is { } cursor)
        {
            // Hide entirely when not streaming; show solid when streaming
            // (the timer will toggle it on the next tick).
            cursor.IsVisible = IsStreaming;
            _cursorVisible = true;
        }
    }
}
