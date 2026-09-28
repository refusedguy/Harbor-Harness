namespace Harbor.Ui.Framework.Rendering;

/// <summary>
/// Background animation ticker (ENG5, issue #276): advances a monotonic tick
/// at ~10 Hz on a thread-pool timer so spinners and reactions animate on
/// wall-clock time instead of frame count. Frame-driven ticks freeze whenever
/// the pipeline skips a paint (quiet Idle) and speed up under burst frames;
/// <para/>
/// Consumption is opt-in: hosts attach the clock to the animated panel (the
/// status panel's <c>AnimationClock</c> property); unattached panels keep the
/// per-paint increment, so existing snapshots stay byte-identical.
/// Thread-safe, AOT-friendly (plain <see cref="Timer"/>, no reflection),
/// allocation-free per tick after construction.
/// </summary>
public sealed class AnimationClock : IDisposable
{
    /// <summary>Animation cadence: 10 ticks per second.</summary>
    public const int TargetHertz = 10;

    /// <summary>Wall-clock interval between ticks (100 ms).</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(1000 / TargetHertz);

    private readonly Timer _timer;
    private long _tick;
    private int _running;
    private bool _disposed;

    /// <summary>Raised on the timer thread after every tick with the new value. Subscribers must be quick and must not throw.</summary>
    public event EventHandler<AnimationTickEventArgs>? Advanced;

    public AnimationClock(bool start = false)
    {
        _timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
        if (start)
        {
            Start();
        }
    }

    /// <summary>Current monotonic tick (0 until the first tick).</summary>
    public long Tick => Volatile.Read(ref _tick);

    /// <summary>Whether the background timer is currently armed.</summary>
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>Arms the ~10 Hz background timer (idempotent).</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Interlocked.Exchange(ref _running, 1);
        _timer.Change(Interval, Interval);
    }

    /// <summary>Disarms the background timer; the tick value is kept (idempotent).</summary>
    public void Stop()
    {
        Interlocked.Exchange(ref _running, 0);
        if (!_disposed)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Advances the tick by one and raises <see cref="Advanced"/>.
    /// Exposed for deterministic tests and hosts that step manually.
    /// </summary>
    /// <returns>The new tick value.</returns>
    public long Advance()
    {
        long next = Interlocked.Increment(ref _tick);
        if (Advanced is { } handler)
        {
            handler(this, new AnimationTickEventArgs(next));
        }

        return next;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _timer.Dispose();
    }

    private void OnTimer(object? _)
    {
        if (IsRunning)
        {
            Advance();
        }
    }
}

/// <summary>Payload for <see cref="AnimationClock.Advanced"/>: the new monotonic tick.</summary>
public sealed class AnimationTickEventArgs(long tick) : EventArgs
{
    /// <summary>The new monotonic tick value.</summary>
    public long Tick { get; } = tick;
}
