using System;
using Harbor.Ui.Framework.Converters;

namespace Harbor.Ui.Framework.Animation;

/// <summary>
///     Eases the running-cost readout from the value it is currently showing to
///     the next value the core reports. The platform VM owns the UI-thread timer
///     and calls <see cref="Advance" /> on each interval.
/// </summary>
/// <remarks>
/// <para>
///     #676: this type is a display, not a cost model. It used to grow the
///     readout by a per-second rate of its own — a constant with no counterpart
///     anywhere in the core, so the displayed total was invented, and grew
///     smoothly enough to read like a live bill. The only authority for a dollar
///     amount is <c>Pricing.CalculateCost(Usage)</c>, a function of a usage
///     snapshot: the core reports what was spent, never how fast, so there is no
///     per-second rate for a view layer to have come from. Nothing was misplaced
///     here, either — the defect was the category, and the fix is the contract
///     between the layers: a cost is a fact the core states.
/// </para>
/// <para>
///     So what is left is the honest half of an animation — a slide between two
///     numbers the core handed over. Elapsed time decides how far along that
///     slide the frame sits; it contributes nothing to the value. The readout
///     therefore stays inside the hull of what has been reported, a reported
///     number does not drift while a run is going, and a cost the core has not
///     reported renders as the em-dash placeholder instead of a fabricated zero
///     — the same "no data ⇒ no cell" rule <c>StatusBarText.CostCell</c> already
///     follows (#457).
/// </para>
/// <para>
///     A host that reports nothing mid-run — the current Avalonia shell does not
///     — simply shows the last number it was given, which is still a core value.
///     That is the intended degradation: a stale dollar figure beats a smooth
///     lie.
/// </para>
/// </remarks>
public sealed class CostAnimator : IDisposable
{
    /// <summary>
    ///     How long a slide between two reported values lasts. Four frames at the
    ///     framework's 10 Hz <c>AnimationClock</c> cadence — long enough to read
    ///     as a number ticking up, far too short to be mistaken for accrual.
    /// </summary>
    private const int SlideMilliseconds = 400;

    /// <summary>
    ///     Stand-in for "the core has not reported a cost yet". Rendered instead
    ///     of a zero so absence is not mistaken for a measurement.
    /// </summary>
    private const string NotYetReported = "—";

    private decimal _displayCost;
    private decimal _targetCost;
    private decimal _slideFrom;
    private DateTime? _slideStart;
    private bool _hasReportedCost;
    private bool _disposed;

    /// <summary>
    ///     The most recent cost the core reported. Assigning a new one is what
    ///     drives the animation — the animator has no other input.
    /// </summary>
    public decimal BaseCost
    {
        get => _targetCost;
        set => Report(value);
    }

    /// <summary>
    ///     The cost on screen: always a value the core reported, or a point on
    ///     the way to one.
    /// </summary>
    public decimal DisplayCost => _displayCost;

    /// <summary>Whether a run is in progress (between <see cref="Start" /> and <see cref="Stop" />).</summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    ///     The readout as text, or the em-dash placeholder while the core has
    ///     reported no cost at all.
    /// </summary>
    public string AnimatedText => _hasReportedCost
        ? StatusMappers.CostToUsd(_displayCost)
        : NotYetReported;

    /// <summary>Raised after each <see cref="Advance" /> so the host can repaint.</summary>
    public event Action? Tick;

    /// <summary>
    ///     Begins a run at the first cost the core reports. It is shown at once:
    ///     there is no earlier reported value to slide from, and a figure left
    ///     over from a previous run is not one to interpolate out of.
    /// </summary>
    /// <param name="costUsd">Cost in USD, exactly as the core reported it.</param>
    public void Start(decimal costUsd)
    {
        Report(costUsd);
        IsRunning = true;
    }

    /// <summary>Ends the run, freezing the readout on the last reported value.</summary>
    public void Stop()
    {
        _slideStart = null;
        _displayCost = _targetCost;
        IsRunning = false;
    }

    /// <summary>
    ///     Moves the readout along its slide toward the last reported value and
    ///     parks on it there, then raises <see cref="Tick" /> on every call while
    ///     a run is active. Does nothing once stopped.
    /// </summary>
    public void Advance()
    {
        if (!IsRunning)
        {
            return;
        }

        if (_slideStart is { } slideStart)
        {
            double elapsedMs = (DateTime.UtcNow - slideStart).TotalMilliseconds;
            if (elapsedMs >= SlideMilliseconds)
            {
                // Land on the reported figure exactly — an approximation of it
                // would be one more number the core never said.
                _slideStart = null;
                _displayCost = _targetCost;
            }
            else
            {
                var progress = (decimal)(elapsedMs / SlideMilliseconds);
                _displayCost = _slideFrom + ((_targetCost - _slideFrom) * progress);
            }
        }

        Tick?.Invoke();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Tick = null;
        _disposed = true;
    }

    /// <summary>
    ///     Takes a cost the core reported. The first one appears immediately;
    ///     a later one starts a slide from whatever is on screen — but only
    ///     during a run, since outside one there is no animation to drive and
    ///     the number should simply become the number.
    /// </summary>
    /// <param name="costUsd">Cost in USD, exactly as the core reported it.</param>
    private void Report(decimal costUsd)
    {
        bool isFirstReport = !_hasReportedCost;
        _hasReportedCost = true;
        _targetCost = costUsd;

        if (isFirstReport || !IsRunning)
        {
            _slideStart = null;
            _displayCost = costUsd;
            return;
        }

        _slideFrom = _displayCost;
        _slideStart = DateTime.UtcNow;
    }
}
