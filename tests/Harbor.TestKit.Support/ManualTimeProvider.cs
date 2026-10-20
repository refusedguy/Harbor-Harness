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
    public ManualTimeProvider(DateTimeOffset? start = null, bool fireTimerOnCreate = false)
    {
        _now = start ?? DateTimeOffset.UnixEpoch;
        FireTimerOnCreate = fireTimerOnCreate;
    }

    /// <summary>
    ///     Whether a timer created through <see cref="CreateTimer" /> invokes
    ///     its callback immediately.
    /// </summary>
    public bool FireTimerOnCreate { get; }

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
    public void Advance(TimeSpan delta)
    {
        _now += delta;
        ElapsedMilliseconds += (long)delta.TotalMilliseconds;
    }

    /// <summary>
    ///     Moves the clock forward by whole milliseconds.
    /// </summary>
    /// <param name="milliseconds">How far to move it.</param>
    public void AdvanceMs(long milliseconds)
    {
        ElapsedMilliseconds += milliseconds;
        _now += TimeSpan.FromMilliseconds(milliseconds);
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
        lock (_gate)
        {
            _delays.Add(dueTime);
        }

        if (FireTimerOnCreate)
        {
            callback(state);
        }

        return NoopTimer.Instance;
    }

    /// <summary>
    ///     The timer every copy of this fake returned: a handle that changes
    ///     nothing and disposes quietly.
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
}
