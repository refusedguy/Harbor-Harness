namespace Harbor.TestKit;

/// <summary>
///     A clock the test owns: nothing moves unless the test moves it.
/// </summary>
/// <remarks>
///     <para>
///         Eight hand-written <see cref="TimeProvider" /> fakes existed across
///         eight files, and they were the same clock with three dials set
///         differently: a wall clock (<c>GetUtcNow</c> plus an
///         <c>Advance</c>), a tick clock (<c>GetTimestamp</c> plus an
///         <c>AdvanceMs</c>), and a frozen clock (a timer that never fires).
///         Two of them also recorded the delays they were asked for, and two
///         of them fired their timer callback immediately so a
///         <c>Task.Delay</c>-shaped retry policy would observe an
///         already-elapsed delay.
///     </para>
///     <para>
///         All of that is here, with the behaviour that DIFFERS between the
///         copies made explicit rather than implied by which class a test
///         happened to declare: the timer fires on creation only when
///         <paramref name="fireTimerOnCreate" /> says so, and the recorded
///         delays are always available.
///     </para>
///     <para>
///         The tick frequency is one millisecond, so a duration in ticks and
///         a duration in milliseconds are the same number and every deadline
///         a test writes is exact integer arithmetic.
///     </para>
/// </remarks>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<TimeSpan> _delays = [];
    private readonly List<ManualTimer> _timers = [];
    private readonly Lock _gate = new();

    private DateTimeOffset _now;

    /// <summary>
    ///     Creates a clock that starts at <see cref="DateTimeOffset.UnixEpoch" />
    ///     and does not move on its own.
    /// </summary>
    /// <param name="start">The instant the clock reads before it is advanced.</param>
    /// <param name="fireTimerOnCreate">
    ///     Whether <see cref="CreateTimer" /> invokes the callback immediately.
    ///     The frozen clocks did not, and a grace timer that fires the moment
    ///     it is created is not a grace timer; the recording clocks did, so
    ///     that a retry policy observing a <c>Task.Delay</c> would see an
    ///     already-elapsed delay.
    /// </param>
    /// <param name="fireTimerOnAdvance">
    ///     Whether <see cref="Advance" /> fires timers whose deadline the move
    ///     reached. Off by default, which keeps the recording-clock behaviour.
    ///     Turn it on for a deadline carried by
    ///     <c>CancellationTokenSource(TimeSpan, TimeProvider)</c>: advancing the
    ///     clock then cancels the token deterministically, with no wall-clock
    ///     sleep and no tolerance to tune.
    /// </param>
    public ManualTimeProvider(
        DateTimeOffset? start = null,
        bool fireTimerOnCreate = false,
        bool fireTimerOnAdvance = false)
    {
        _now = start ?? DateTimeOffset.UnixEpoch;
        FireTimerOnCreate = fireTimerOnCreate;
        FireTimerOnAdvance = fireTimerOnAdvance;
    }

    /// <summary>
    ///     Whether a timer created through <see cref="CreateTimer" /> invokes
    ///     its callback immediately.
    /// </summary>
    public bool FireTimerOnCreate { get; }

    /// <summary>
    ///     Whether <see cref="Advance" /> fires timers that the move made due.
    /// </summary>
    public bool FireTimerOnAdvance { get; }

    /// <summary>
    ///     Timers still armed on this clock. A caller that owns a deadline can
    ///     assert this is zero after its run to prove the timer was released.
    /// </summary>
    public int ActiveTimerCount
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count;
            }
        }
    }

    /// <summary>
    ///     The delays this clock was asked for, in the order they were asked.
    /// </summary>
    public IReadOnlyList<TimeSpan> Delays
    {
        get
        {
            lock (_gate)
            {
                return [.. _delays];
            }
        }
    }

    /// <summary>
    ///     Milliseconds this clock has been advanced by. The same number
    ///     <see cref="GetTimestamp" /> reports.
    /// </summary>
    public long ElapsedMilliseconds { get; private set; }

    /// <summary>
    ///     Moves the clock forward.
    /// </summary>
    /// <param name="delta">How far to move it.</param>
    public void Advance(TimeSpan delta) => Move(delta, (long)delta.TotalMilliseconds);

    /// <summary>
    ///     Moves the clock forward by whole milliseconds.
    /// </summary>
    /// <param name="milliseconds">How far to move it.</param>
    public void AdvanceMs(long milliseconds) =>
        Move(TimeSpan.FromMilliseconds(milliseconds), milliseconds);

    private void Move(TimeSpan delta, long milliseconds)
    {
        List<ManualTimer>? due = null;
        lock (_gate)
        {
            _now += delta;
            ElapsedMilliseconds += milliseconds;

            if (!FireTimerOnAdvance)
            {
                return;
            }

            due = [];
            for (int i = _timers.Count - 1; i >= 0; i--)
            {
                ManualTimer timer = _timers[i];
                if (!timer.IsDue(_now))
                {
                    continue;
                }

                due.Add(timer);
                if (timer.Period == Timeout.InfiniteTimeSpan || timer.Period <= TimeSpan.Zero)
                {
                    timer.Deadline = null;
                    _timers.RemoveAt(i);
                }
                else
                {
                    DateTimeOffset next = timer.Deadline!.Value + timer.Period;
                    while (next <= _now)
                    {
                        next += timer.Period;
                    }

                    timer.Deadline = next;
                }
            }
        }

        // Fire outside the lock: a CTS callback can dispose its own timer,
        // and that disposal takes the same lock.
        if (due is not null)
        {
            foreach (ManualTimer timer in due)
            {
                timer.Fire();
            }
        }
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _now;

    /// <inheritdoc />
    public override long TimestampFrequency => 1_000;

    /// <inheritdoc />
    public override long GetTimestamp() => ElapsedMilliseconds;

    /// <inheritdoc />
    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ManualTimer? armed = null;
        lock (_gate)
        {
            _delays.Add(dueTime);
            if (FireTimerOnAdvance && !FireTimerOnCreate)
            {
                armed = new ManualTimer(this, callback, state, dueTime, period);
                _timers.Add(armed);
            }
        }

        if (FireTimerOnCreate)
        {
            callback(state);
        }

        return armed ?? NoopTimer.Instance;
    }

    /// <summary>
    ///     The timer the recording dials return: a handle that changes nothing
    ///     and disposes quietly.
    /// </summary>
    private sealed class NoopTimer : ITimer
    {
        public static readonly NoopTimer Instance = new();

        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => default;
    }

    /// <summary>
    ///     A one-shot or periodic timer whose deadline lives on the virtual
    ///     clock, used only when <see cref="FireTimerOnAdvance" /> is set.
    /// </summary>
    private sealed class ManualTimer(
        ManualTimeProvider owner,
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period) : ITimer
    {
        private bool _disposed;

        /// <summary>When the timer fires, or null when it is disarmed.</summary>
        public DateTimeOffset? Deadline { get; set; } =
            dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;

        /// <summary>The repeat interval, or an infinite/one-shot value.</summary>
        public TimeSpan Period { get; private set; } = period;

        /// <summary>Whether the virtual clock has reached the deadline.</summary>
        public bool IsDue(DateTimeOffset now) => Deadline is { } deadline && now >= deadline;

        /// <summary>Run the callback unless the timer was disposed.</summary>
        public void Fire()
        {
            if (!_disposed)
            {
                callback(state);
            }
        }

        /// <inheritdoc />
        public bool Change(TimeSpan newDueTime, TimeSpan newPeriod)
        {
            if (_disposed)
            {
                return false;
            }

            Period = newPeriod;
            Deadline = newDueTime == Timeout.InfiniteTimeSpan ? null : owner._now + newDueTime;
            return true;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _disposed = true;
            lock (owner._gate)
            {
                owner._timers.Remove(this);
            }
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}
