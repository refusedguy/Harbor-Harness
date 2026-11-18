namespace Harbor.Tui.CellForge.Input;

/// <summary>How many clicks form the current chain.</summary>
internal enum ClickChain : byte
{
    Single = 1,
    Double = 2,
    Triple = 3,
}

/// <summary>
/// Central click-chain recognizer for the input layer (issue #2492). Terminal
/// protocols never report a double or triple click: <c>MouseEvent.cs</c> calls
/// that a "consumer-side timing heuristic", and every consumer used to own a
/// different one. This is the counter they can share. It is a pure engine type:
/// no renderer, no widget, no timers of its own, and no wall clock. The caller
/// feeds it clean <see cref="MouseEventType.Click"/> events and reads the chain
/// back; time comes from the injected <see cref="TimeProvider"/>.
/// </summary>
internal sealed class ClickChainTracker
{
    /// <summary>Maximum gap between clicks of one chain (Textual/Zed use 500 ms).</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromMilliseconds(500);

    private readonly TimeProvider _time;
    private readonly TimeSpan _window;
    private readonly int _tolerance;
    private bool _hasPrevious;
    private MouseButton _button;
    private int _column;
    private int _row;
    private long _previousTimestamp;
    private ClickChain _previousChain;

    /// <summary>Creates a tracker.</summary>
    /// <param name="timeProvider">Clock for the window; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="window">Maximum gap between two clicks of the same chain.</param>
    /// <param name="tolerance">
    /// Cell jitter allowed between clicks, per axis (Chebyshev distance). Zero
    /// means the same cell, which is what Textual's <c>Click.chain</c> requires.
    /// </param>
    internal ClickChainTracker(
        TimeProvider? timeProvider = null,
        TimeSpan? window = null,
        int tolerance = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tolerance);
        if (window is { } value && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "The click window must be positive.");
        }

        _time = timeProvider ?? TimeProvider.System;
        _window = window ?? Window;
        _tolerance = tolerance;
    }

    /// <summary>
    /// Records one clean click and returns its chain position. A click extends
    /// the chain only when it uses the same button, lands on the same cell
    /// (within tolerance) and arrives strictly inside <see cref="Window"/> of
    /// the previous click. Anything else starts a new <see cref="ClickChain.Single"/>
    /// chain. Further clicks stay at <see cref="ClickChain.Triple"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The event is not a clean click.</exception>
    internal ClickChain Register(MouseEvent click)
    {
        if (click.Type != MouseEventType.Click)
        {
            throw new ArgumentException("Only MouseEventType.Click participates in a chain.", nameof(click));
        }

        long now = _time.GetTimestamp();
        bool chained = _hasPrevious
            && click.Button == _button
            && Math.Abs((long)click.Column - _column) <= _tolerance
            && Math.Abs((long)click.Row - _row) <= _tolerance
            && _time.GetElapsedTime(_previousTimestamp, now) < _window;

        ClickChain chain = chained && _previousChain < ClickChain.Triple
            ? (ClickChain)((int)_previousChain + 1)
            : ClickChain.Single;

        _hasPrevious = true;
        _previousTimestamp = now;
        _previousChain = chain;
        _button = click.Button;
        _column = click.Column;
        _row = click.Row;
        return chain;
    }

    /// <summary>Drops the chain (drag, focus change, or gesture boundary).</summary>
    internal void Reset()
    {
        _hasPrevious = false;
        _previousTimestamp = 0;
        _previousChain = default;
        _button = default;
        _column = 0;
        _row = 0;
    }
}
