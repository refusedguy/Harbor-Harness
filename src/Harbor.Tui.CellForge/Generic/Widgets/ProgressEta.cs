// #2690 (steal-list 2026-11, idea 6): ETA model for a progress bar. A pure
// type: it owns no timer and never reads the wall clock except through the
// injected TimeProvider, so a host decides when to take a sample and a test
// controls time. BCL-only, AOT-compatible, render-thread owned.

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
///     Estimates time remaining from a bounded history of (timestamp, ratio)
///     samples. One instance belongs to one progress indication; a host calls
///     <see cref="AddSample(double)" /> whenever it observes a new ratio.
/// </summary>
/// <remarks>
///     Speed is the average over the retained window, from its oldest sample
///     to its newest. A window never spans a regression: a ratio that moves
///     backwards clears the history first, so a restarted or wrapped
///     operation cannot report a speed from two different runs. Stalled
///     progress (no ratio change) has no estimate rather than an infinite one.
/// </remarks>
public sealed class ProgressEta
{
    /// <summary>Default number of retained samples.</summary>
    public const int DefaultMaxSamples = 100;

    private readonly TimeProvider _timeProvider;
    private readonly int _maxSamples;
    private readonly List<Sample> _samples;

    /// <summary>
    ///     Creates an empty estimator.
    /// </summary>
    /// <param name="timeProvider">Clock used by <see cref="AddSample(double)" />.</param>
    /// <param name="maxSamples">Maximum retained samples; must be at least 2.</param>
    public ProgressEta(TimeProvider timeProvider, int maxSamples = DefaultMaxSamples)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (maxSamples < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSamples), maxSamples, "At least two samples are required to estimate speed.");
        }

        _timeProvider = timeProvider;
        _maxSamples = maxSamples;
        _samples = new List<Sample>(maxSamples);
    }

    /// <summary>Number of retained samples, from 0 to <see cref="MaxSamples"/>.</summary>
    public int SampleCount => _samples.Count;

    /// <summary>Maximum number of retained samples.</summary>
    public int MaxSamples => _maxSamples;

    /// <summary>
    ///     <c>ratio</c> units per second over the retained window, or
    ///     <see langword="null"/> until two samples at different instants show
    ///     forward progress.
    /// </summary>
    public double? Speed
    {
        get
        {
            if (_samples.Count < 2)
            {
                return null;
            }

            Sample first = _samples[0];
            Sample last = _samples[^1];
            double seconds = (last.At - first.At).TotalSeconds;
            if (seconds <= 0)
            {
                return null;
            }

            double delta = last.Ratio - first.Ratio;
            return delta > 0 ? delta / seconds : null;
        }
    }

    /// <summary>
    ///     Seconds left at the current speed, or <see langword="null"/> when no
    ///     estimate is available. Zero once the ratio has reached 1.
    /// </summary>
    public double? SecondsRemaining
    {
        get
        {
            if (_samples.Count == 0)
            {
                return null;
            }

            double ratio = _samples[^1].Ratio;
            if (ratio >= 1)
            {
                return 0;
            }

            if (Speed is not { } speed || speed <= 0)
            {
                return null;
            }

            double remaining = Math.Max(0, (1 - ratio) / speed);
            return double.IsFinite(remaining) ? remaining : null;
        }
    }

    /// <summary>
    ///     Text to append to a progress label, for example <c>"10s"</c> or
    ///     <c>"2m 4s"</c>. <see langword="null"/> when there is no estimate.
    /// </summary>
    public string? Suffix
    {
        get
        {
            if (SecondsRemaining is not { } seconds || seconds <= 0)
            {
                // A finished operation has nothing left to estimate.
                return null;
            }

            if (seconds < 1)
            {
                return "<1s";
            }

            int total = (int)Math.Ceiling(seconds);
            if (total < 60)
            {
                return $"{total}s";
            }

            return $"{total / 60}m {total % 60}s";
        }
    }

    /// <summary>Records the current ratio at the clock's current instant.</summary>
    /// <param name="ratio">Progress in 0..1; NaN counts as 0, values above 1 clamp to 1.</param>
    public void AddSample(double ratio) => AddSample(_timeProvider.GetUtcNow(), ratio);

    /// <summary>
    ///     Records a ratio at an explicit instant. Useful when a host already
    ///     has a timestamp and does not want a second clock read.
    /// </summary>
    /// <param name="timestamp">When the ratio was observed.</param>
    /// <param name="ratio">Progress in 0..1; NaN counts as 0, values above 1 clamp to 1.</param>
    public void AddSample(DateTimeOffset timestamp, double ratio)
    {
        double clamped = GenericProgressBar.ClampRatio(ratio);

        if (_samples.Count > 0 && clamped < _samples[^1].Ratio)
        {
            _samples.Clear();
        }

        if (_samples.Count > 0 && timestamp <= _samples[^1].At)
        {
            // A non-advancing clock would make the window's speed divide by
            // zero. Replace the newest sample instead of appending one.
            _samples[^1] = new Sample(timestamp, clamped);
            return;
        }

        _samples.Add(new Sample(timestamp, clamped));
        if (_samples.Count > _maxSamples)
        {
            _samples.RemoveAt(0);
        }
    }

    /// <summary>Forgets every sample, for example when a new operation starts.</summary>
    public void Reset() => _samples.Clear();

    private readonly record struct Sample(DateTimeOffset At, double Ratio);
}
