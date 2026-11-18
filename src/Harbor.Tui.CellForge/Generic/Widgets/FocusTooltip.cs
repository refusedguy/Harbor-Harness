// #2374: focus-driven tooltip primitive (gpui-kit Tooltip, contract only).
//
// Terminals have no reliable hover, so the hint follows the FocusRouter's
// current target instead of the mouse. The host owns the text: it supplies a
// describe(target) function, and the tooltip snapshots that text when the hint
// appears. No key is routed here and no overlay layer is registered, so the '?'
// key legend (#2325) and WhichKeyHelpOverlay keep their own site. No plugin
// seam, no registry, no new extension axis (#555).

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// A hint that appears after a focus target has been current for a delay and
/// disappears after a timeout. It observes an
/// <see cref="Harbor.Tui.CellForge.Input.FocusRouter"/> on each
/// <see cref="Tick"/>; hosts call Tick once per frame (or whenever focus may
/// have changed) and paint the result into a rect they choose.
/// </summary>
/// <remarks>
/// A focus change hides the current hint immediately and restarts the delay.
/// Losing focus (an empty router) does the same. An empty description keeps the
/// hint hidden until focus moves again. The clock is injected as a
/// <see cref="TimeProvider"/>, so delay and timeout are measured in real time
/// and stay testable without a wall-clock race.
/// </remarks>
public sealed class FocusTooltip
{
    /// <summary>Default pause before a focused element's hint appears.</summary>
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>Default time a shown hint stays visible.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(4000);

    /// <summary>Smallest rect that can hold the bordered hint.</summary>
    public const int MinWidth = 5;

    /// <summary>Smallest rect that can hold the bordered hint.</summary>
    public const int MinHeight = 3;

    private readonly Harbor.Tui.CellForge.Input.FocusRouter _router;
    private readonly Func<Harbor.Tui.CellForge.Input.IFocusTarget, string?> _describe;
    private readonly TimeProvider _time;
    private const long FocusAtSentinel = -1;

    private string? _targetId;
    private long _focusAt;
    private long _shownAt;
    private string _text = string.Empty;
    private bool _visible;
    private bool _suppressed;

    /// <summary>
    /// Creates a tooltip bound to <paramref name="router"/>.
    /// <paramref name="describe"/> reads the description of the currently
    /// focused target and may return null or empty to mean "no hint".
    /// A non-positive <paramref name="delay"/> shows the hint on the next tick;
    /// a non-positive <paramref name="timeout"/> keeps a shown hint until focus
    /// moves (no automatic hide).
    /// </summary>
    public FocusTooltip(
        Harbor.Tui.CellForge.Input.FocusRouter router,
        Func<Harbor.Tui.CellForge.Input.IFocusTarget, string?> describe,
        TimeSpan? delay = null,
        TimeSpan? timeout = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(describe);
        _router = router;
        _describe = describe;
        _time = timeProvider ?? TimeProvider.System;
        Delay = delay ?? DefaultDelay;
        Timeout = timeout ?? DefaultTimeout;
        _focusAt = FocusAtSentinel;
    }

    /// <summary>Pause between focus and the hint appearing.</summary>
    public TimeSpan Delay { get; }

    /// <summary>Time a shown hint stays visible; non-positive means sticky.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Whether a hint is currently visible.</summary>
    public bool IsVisible => _visible;

    /// <summary>Hint text currently painted (empty when hidden).</summary>
    public string Text => _visible ? _text : string.Empty;

    /// <summary>Id of the focus target this tooltip is tracking, if any.</summary>
    public string? TargetId => _targetId;

    /// <summary>Raised when visibility changes so a host can repaint.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Samples focus and advances the delay/timeout. Returns true when the
    /// visible state changed. The clock is the only source of elapsed time:
    /// repeated ticks at the same instant never advance the hint.
    /// </summary>
    public bool Tick()
    {
        bool changed = TickCore();
        if (changed)
        {
            RaiseChanged();
        }

        return changed;
    }

    private bool TickCore()
    {
        long now = _time.GetTimestamp();
        Harbor.Tui.CellForge.Input.IFocusTarget? target = _router.Current;
        string? id = target?.Id;
        bool changed = false;

        if (!string.Equals(id, _targetId, StringComparison.Ordinal))
        {
            _targetId = id;
            _focusAt = now;
            _shownAt = now;
            _suppressed = false;
            if (_visible)
            {
                _visible = false;
                _text = string.Empty;
                changed = true;
            }
        }

        if (target is null)
        {
            return changed;
        }

        if (_visible)
        {
            if (Timeout > TimeSpan.Zero && _time.GetElapsedTime(_shownAt, now) >= Timeout)
            {
                _visible = false;
                _suppressed = true; // one hint per focus, no re-show after a timeout
                _text = string.Empty;
                changed = true;
            }

            return changed;
        }

        if (_suppressed || _time.GetElapsedTime(_focusAt, now) < Delay)
        {
            return changed;
        }

        string? text = _describe(target);
        if (string.IsNullOrEmpty(text))
        {
            _suppressed = true;
            return changed;
        }

        _text = text;
        _shownAt = now;
        _visible = true;
        changed = true;
        return changed;
    }

    /// <summary>
    /// Drops the current hint and cancels its pending delay. The next tick
    /// re-reads focus, so this is only needed when a host hides the tooltip
    /// for a reason outside the focus router. Returns true when a hint was
    /// visible.
    /// </summary>
    public bool Reset()
    {
        _targetId = null;
        _focusAt = FocusAtSentinel;
        _suppressed = false;
        bool wasVisible = _visible;
        _visible = false;
        _text = string.Empty;
        if (wasVisible)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return wasVisible;
    }

    /// <summary>
    /// Paints a bordered hint into <paramref name="rect"/> when visible.
    /// Long text is clipped with an ellipsis. Rects smaller than
    /// <see cref="MinWidth"/> x <see cref="MinHeight"/> paint nothing.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!_visible || rect.Width < MinWidth || rect.Height < MinHeight)
        {
            return;
        }

        var border = new CellStyle(ChatPalette.Border);
        var textStyle = new CellStyle(ChatPalette.Text);
        int inner = rect.Width - 4;
        string text = Fit(_text, inner);
        string pad = new(' ', inner - text.Length);

        buffer.SetText(rect.X, rect.Y, "┌" + new string('─', rect.Width - 2) + "┐", border);
        buffer.SetText(rect.X, rect.Y + 1, "│ " + text + pad + " │", textStyle);
        buffer.SetText(rect.X, rect.Y + 2, "└" + new string('─', rect.Width - 2) + "┘", border);
    }

    private static string Fit(string text, int width)
    {
        if (text.Length <= width)
        {
            return text;
        }

        if (width <= 1)
        {
            return width == 1 ? "…" : string.Empty;
        }

        return text[..(width - 1)] + "…";
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
