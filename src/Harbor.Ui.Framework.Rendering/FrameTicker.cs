namespace Harbor.Ui.Framework.Rendering;

/// <summary>
/// FPS ticker (ENG7, issue #278): BubbleTea-pattern 60 fps cap as a gate over
/// the wake-driven frame loop — render on tick, not on event. Hosts coalesce
/// wake bursts through <see cref="ShouldRender"/>/<see cref="MsUntilDue"/>:
/// the first frame is always due, later frames at most one per
/// <see cref="MinInterval"/>.
/// <para/>
/// The ticker owns rate only, never content: identical-model short-circuit
/// stays with the host (it alone knows the model version). Forced frames
/// (animation clock running, layout springs, resize) bypass the host's
/// version check but still pace through <see cref="MsUntilDue"/> + delay, so
/// the cap holds for every frame kind.
/// <para/>
/// Deterministic: time is injected as monotonic milliseconds — no wall clock,
/// no timer thread (the ENG5 <see cref="AnimationClock"/> stays the only
/// background ticker; reuse, don't duplicate). Thread-safe counters for
/// moat-test pins.
/// </summary>
public sealed class FrameTicker
{
    /// <summary>Frame cap: 60 frames per second (btea default).</summary>
    public const int MaxHertz = 60;

    /// <summary>Minimum spacing between two rendered frames (~16.67 ms).</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(1000.0 / MaxHertz);

    private static readonly long IntervalMs = (long)MinInterval.TotalMilliseconds;

    private long _lastRenderMs;
    private bool _hasRendered;
    private long _rendered;
    private long _suppressed;
    private long _paced;

    /// <summary>Frames let through the gate.</summary>
    public long RenderedFrames => Volatile.Read(ref _rendered);

    /// <summary>Frames skipped (identical model or wake with nothing due).</summary>
    public long SuppressedFrames => Volatile.Read(ref _suppressed);

    /// <summary>Frames delayed to the next tick boundary (burst coalescing).</summary>
    public long PacedFrames => Volatile.Read(ref _paced);

    /// <summary>
    /// True when a frame may render at <paramref name="nowMs"/> (monotonic):
    /// always the first frame, then once <see cref="MinInterval"/> elapsed
    /// since the last <see cref="MarkRendered"/>.
    /// </summary>
    public bool ShouldRender(long nowMs)
    {
        if (!_hasRendered)
        {
            return true;
        }

        return nowMs - Volatile.Read(ref _lastRenderMs) >= IntervalMs;
    }

    /// <summary>
    /// Milliseconds until the next frame is due (0 when due now). Hosts
    /// <c>await Task.Delay</c> this instead of dropping a dirty frame, so a
    /// coalesced frame is deferred, never lost.
    /// </summary>
    public long MsUntilDue(long nowMs)
    {
        if (!_hasRendered)
        {
            return 0;
        }

        long elapsed = nowMs - Volatile.Read(ref _lastRenderMs);
        long remaining = IntervalMs - elapsed;
        return remaining > 0 ? remaining : 0;
    }

    /// <summary>Records a rendered frame at <paramref name="nowMs"/> (monotonic).</summary>
    public void MarkRendered(long nowMs)
    {
        Volatile.Write(ref _lastRenderMs, nowMs);
        _hasRendered = true;
        Interlocked.Increment(ref _rendered);
    }

    /// <summary>Records a short-circuited frame (identical model — zero backend writes by contract).</summary>
    public void MarkSuppressed() => Interlocked.Increment(ref _suppressed);

    /// <summary>Records a burst-coalesced frame deferred via <see cref="MsUntilDue"/>.</summary>
    public void MarkPaced() => Interlocked.Increment(ref _paced);
}
