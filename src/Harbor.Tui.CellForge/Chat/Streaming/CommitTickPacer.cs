namespace Harbor.Tui.CellForge.Streaming;

/// <summary>How much of the pending line queue a tick should drain.</summary>
public enum DrainPlanKind : byte
{
    /// <summary>Reveal one line this tick — looks like typing at ~tick rate.</summary>
    Single = 0,

    /// <summary>Reveal everything queued this tick — catch-up burst.</summary>
    BatchAll = 1,
}

/// <summary>Queue pressure snapshot for <see cref="CommitTickPacer.Decide"/>.</summary>
public readonly record struct QueueSnapshot(int Depth, TimeSpan OldestAge);

/// <summary>
/// Adaptive commit pacing with codex-rs hysteresis (widgets §3.4):
/// enter CatchUp when the queue holds ≥ 8 lines OR the oldest line is older
/// than 120 ms; exit when ≤ 2 lines AND ≤ 40 ms, held for 250 ms inside the
/// mode; re-entry blocked for another 250 ms after exit (anti-flapping);
/// age > 300 ms forces a batch regardless of mode.
///
/// Deterministic: time is injected as monotonic milliseconds — no wall clock.
/// </summary>
public sealed class CommitTickPacer
{
    public const int EnterDepth = 8;
    public static readonly TimeSpan EnterAge = TimeSpan.FromMilliseconds(120);
    public const int ExitDepth = 2;
    public static readonly TimeSpan ExitAge = TimeSpan.FromMilliseconds(40);
    public static readonly TimeSpan ExitHold = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan ReenterHold = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan SevereAge = TimeSpan.FromMilliseconds(300);

    /// <summary>Current pacing mode.</summary>
    public bool IsCatchUp { get; private set; }

    private long _enteredAtMs = long.MinValue;
    private long _exitedAtMs = long.MinValue;

    /// <summary>
    /// Decides how much to drain on this tick and advances the hysteresis
    /// state machine. <paramref name="nowMs"/> is monotonic.
    /// </summary>
    public DrainPlanKind Decide(QueueSnapshot snap, long nowMs)
    {
        if (!IsCatchUp)
        {
            // Severe lag forces an immediate batch without switching modes.
            if (snap.OldestAge > SevereAge)
            {
                return DrainPlanKind.BatchAll;
            }

            bool pressure = snap.Depth >= EnterDepth || snap.OldestAge > EnterAge;
            bool reentryAllowed = _exitedAtMs == long.MinValue || nowMs - _exitedAtMs >= ReenterHold.TotalMilliseconds;
            if (pressure && reentryAllowed)
            {
                IsCatchUp = true;
                _enteredAtMs = nowMs;
            }

            return DrainPlanKind.Single;
        }

        // CatchUp mode: drain everything; leave only after both thresholds are
        // calm AND the minimum stay has elapsed.
        bool calm = snap.Depth <= ExitDepth && snap.OldestAge <= ExitAge;
        bool heldLongEnough = nowMs - _enteredAtMs >= ExitHold.TotalMilliseconds;
        if (calm && heldLongEnough)
        {
            IsCatchUp = false;
            _exitedAtMs = nowMs;
        }

        return DrainPlanKind.BatchAll;
    }
}
