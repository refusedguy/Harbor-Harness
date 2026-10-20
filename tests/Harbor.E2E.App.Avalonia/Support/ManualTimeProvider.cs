namespace Harbor.E2E.App.Avalonia;

/// <summary>
///     A hand-written <see cref="TimeProvider" /> whose clock only moves when a
///     test moves it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why not <c>Microsoft.Extensions.TimeProvider.Testing</c>.</b> That
///         package's <c>FakeTimeProvider</c> would do the same job, but it is a
///         NEW dependency and the repository's central package pins are owned
///         by another change right now. Adding one here would collide with
///         that work. The repo already carries a hand-written
///         <c>TimeProvider</c> (the <c>ManualTimeProvider</c> in
///         <c>tests/Harbor.Registries.Tests/ProviderRegistryCatalogCacheTests.cs</c>),
///         so this is the established shape. That one only overrides
///         <see cref="TimeProvider.GetUtcNow" />, which is enough for a TTL
///         check but NOT for a delay: <see cref="TimeProvider" /> has no
///         <c>Delay</c> instance member, so a delay is
///         <c>Task.Delay(span, timeProvider, ct)</c>, and the BCL routes that
///         through <see cref="TimeProvider.CreateTimer" />. Driving a delay
///         therefore needs a timer, which is what this subclass adds.
///     </para>
///     <para>
///         <b>The mechanics.</b> <see cref="CreateTimer" /> records the timer
///         with a deadline relative to the current virtual instant.
///         <see cref="Advance" /> moves the clock forward and fires every
///         timer whose deadline the clock has reached, outside the lock so a
///         callback may call back into the provider.
///     </para>
/// </remarks>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now;
    private long _timestamp;

    /// <summary>Construct a clock parked at <paramref name="start" />.</summary>
    public ManualTimeProvider(DateTimeOffset start) => _now = start;

    /// <summary>The current virtual instant.</summary>
    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_gate)
            {
                return _now;
            }
        }
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => UtcNow;

    /// <inheritdoc />
    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _timestamp;
        }
    }

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var timer = new ManualTimer(this, callback, state, dueTime, period);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    /// <summary>
    ///     Move the clock forward by <paramref name="delta" /> and fire every
    ///     timer that the move made due.
    /// </summary>
    /// <param name="delta">How far to move. Never negative, since a clock that ran
    ///     backwards would make a "not yet due" timer fire early.</param>
    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), delta, "The manual clock only moves forward.");
        }

        List<ManualTimer> due = [];

        lock (_gate)
        {
            _now += delta;
            _timestamp += delta.Ticks;

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
                    // One-shot: disarm it and forget it.
                    timer.Deadline = null;
                    _timers.RemoveAt(i);
                }
                else
                {
                    // Periodic: skip the deadlines the jump swallowed.
                    DateTimeOffset next = timer.Deadline!.Value + timer.Period;
                    while (next <= _now)
                    {
                        next += timer.Period;
                    }

                    timer.Deadline = next;
                }
            }
        }

        // Fire outside the lock: a callback may dispose its own timer, and
        // Task.Delay's completion runs the awaiting continuation inline.
        foreach (ManualTimer timer in due)
        {
            timer.Fire();
        }
    }

    /// <summary>Drop a timer that was disposed.</summary>
    private void Forget(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    /// <summary>One scheduled callback, armed against the virtual clock.</summary>
    private sealed class ManualTimer(
        ManualTimeProvider owner,
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period) : ITimer
    {
        private readonly TimerCallback _callback = callback;
        private readonly object? _state = state;
        private bool _disposed;

        /// <summary>When the timer fires, or null when it is disarmed.</summary>
        public DateTimeOffset? Deadline { get; set; } =
            dueTime == Timeout.InfiniteTimeSpan ? null : owner.UtcNow + dueTime;

        /// <summary>The repeat interval, or <see cref="Timeout.InfiniteTimeSpan" /> for one-shot.</summary>
        public TimeSpan Period { get; private set; } = period;

        /// <summary>Whether the virtual clock has reached the deadline.</summary>
        public bool IsDue(DateTimeOffset now) => Deadline is { } deadline && now >= deadline;

        /// <summary>Run the callback unless the timer was disposed.</summary>
        public void Fire()
        {
            if (_disposed)
            {
                return;
            }

            _callback(_state);
        }

        /// <inheritdoc />
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed)
            {
                return false;
            }

            Period = period;
            Deadline = dueTime == Timeout.InfiniteTimeSpan ? null : owner.UtcNow + dueTime;
            return true;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _disposed = true;
            owner.Forget(this);
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}
