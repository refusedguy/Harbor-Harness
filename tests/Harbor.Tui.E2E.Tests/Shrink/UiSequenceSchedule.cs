namespace Harbor.Tui.E2E.Tests.Shrink;

/// <summary>
///     Clock abstraction so a shrink run is driven by an injected clock, never
///     by wall-clock luck. Production uses <see cref="SystemShrinkClock" />;
///     tests use <see cref="ManualShrinkClock" />.
/// </summary>
public interface IShrinkClock
{
    DateTime UtcNow { get; }
}

/// <summary>
///     Wall-clock implementation for production runs.
/// </summary>
public sealed class SystemShrinkClock : IShrinkClock
{
    public static readonly SystemShrinkClock Instance = new();

    private SystemShrinkClock()
    {
    }

    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>
///     Virtual clock for tests. Time advances only when the harness advances
///     it, so a schedule replayed twice observes identical timestamps.
/// </summary>
public sealed class ManualShrinkClock : IShrinkClock
{
    private TimeSpan _elapsed;

    public DateTime UtcNow => DateTime.UnixEpoch + _elapsed;

    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delta));
        }

        _elapsed += delta;
    }
}

/// <summary>
///     The timing/interleaving a sequence replays under, controlled by the
///     harness: per-step delays derived from a seed, replayed on a virtual
///     clock. The same seed and step count always yield the same delays.
/// </summary>
public sealed record UiStepSchedule(int Seed, IReadOnlyList<TimeSpan> StepDelays)
{
    public static UiStepSchedule Create(int seed, int stepCount)
    {
        if (stepCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stepCount));
        }

        var rng = new Random(seed);
        var delays = new TimeSpan[stepCount];
        for (int i = 0; i < delays.Length; i++)
        {
            delays[i] = TimeSpan.FromMilliseconds(rng.Next(0, 4) * 10);
        }

        return new UiStepSchedule(seed, delays);
    }

    /// <summary>
    ///     Replays the delays on <paramref name="clock" />, returning one
    ///     timestamp per step. Two replays on fresh clocks with equal delays
    ///     produce equal timestamps.
    /// </summary>
    public IReadOnlyList<DateTime> ReplayOn(ManualShrinkClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var stamps = new DateTime[StepDelays.Count];
        for (int i = 0; i < stamps.Length; i++)
        {
            clock.Advance(StepDelays[i]);
            stamps[i] = clock.UtcNow;
        }

        return stamps;
    }
}
