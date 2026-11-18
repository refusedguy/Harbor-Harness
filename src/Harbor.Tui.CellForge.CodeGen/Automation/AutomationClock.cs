namespace Harbor.Tui.CellForge.CodeGen.Automation;

/// <summary>
///     Virtual clock for headless automation (issue #2438). Time only moves
///     when a test asks it to, so a locator wait is deterministic and never
///     depends on the wall clock or on a real terminal event loop.
/// </summary>
public sealed class AutomationClock
{
    private long _nowMs;

    /// <summary>Whether automatic time flow is suspended.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>Milliseconds elapsed on this virtual timeline.</summary>
    public long NowMilliseconds => _nowMs;

    /// <summary>Point-in-time snapshot returned by the clock method.</summary>
    public AutomationClockState State => new(_nowMs, IsPaused);

    /// <summary>Suspends automatic time flow.</summary>
    public void Pause() => IsPaused = true;

    /// <summary>Resumes automatic time flow.</summary>
    public void Resume() => IsPaused = false;

    /// <summary>
    ///     Advances the virtual timeline by <paramref name="milliseconds"/>,
    ///     even while paused. Pausing stops implicit flow; it never blocks a
    ///     test that explicitly requests a jump.
    /// </summary>
    public void FastForward(long milliseconds)
    {
        if (milliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(milliseconds), "A fast-forward cannot go backwards.");
        _nowMs += milliseconds;
    }
}
