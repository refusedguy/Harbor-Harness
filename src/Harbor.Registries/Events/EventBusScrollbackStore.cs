namespace Harbor.Registries.Events;

/// <summary>
///     Bounded scrollback for <see cref="InMemoryEventBus" />: a fixed-capacity
///     ring buffer of pre-allocated slots that retains published events for
///     late-attaching subscribers, plus the retention-arming contract that
///     decides whether the ring is maintained at all.
/// </summary>
/// <remarks>
///     <para>
///         <b>#1512 (#95 slice) — verbatim extract.</b> This type was the
///         scrollback half of <see cref="InMemoryEventBus" />: the ring, its
///         lock, the head/count pair and the arming flag moved here as ONE
///         unit, because the three share a contract — the flag gates the
///         append, the lock guards the slots, and the first reader arms the
///         flag. The bus keeps its public surface and delegates to this type;
///         no logic changed.
///     </para>
///     <para>
///         <b>B7perf (§PERF):</b> scrollback was an <c>ImmutableArray</c>
///         CAS-appended per publish, which allocated a fresh builder + copy
///         (~8 KB at capacity 1000) on EVERY publish once the buffer reached
///         capacity — even with zero subscribers attached. Scrollback is a
///         fixed-capacity <c>AgentEvent[]</c> ring allocated once in the
///         constructor: publishing OVERWRITES the oldest slot in place (events
///         are immutable records, so sharing slots is safe) and allocates
///         nothing. Readers receive a point-in-time copy.
///     </para>
///     <para>
///         <b>Retention is armed by its reader (#518).</b> The ring exists so
///         that history is available as an explicit pull
///         (<see cref="GetScrollback" />; declared contract in
///         <c>docs/EVENT_TOPOLOGY.md</c> §4, #44). Until the first read no
///         ring is maintained: capacity alone used to cost a slot write under
///         a lock on every publish and, worse, made the bus's fast path false
///         for every shipped preset — 0/200 qualifying publishes on CLI and
///         desktop, 200/200 only headless, for a ring that nothing read. The
///         first <see cref="GetScrollback" /> call arms retention permanently;
///         from then on history is maintained and the full path is taken. The
///         ring was <b>not</b> deleted: <see cref="IEventBus" /> is public in
///         the zero-dependency <c>Harbor.Abstractions</c> assembly that every
///         plugin references, and a live bus is handed to out-of-tree plugin
///         code via <c>IPluginLoadHost.EventBus</c> — so "no production
///         callers" is not a provable claim here; only the cost was provably
///         wrong.
///     </para>
/// </remarks>
internal sealed class EventBusScrollbackStore
{
    /// <summary>
    ///     Maximum number of events retained in scrollback. Zero disables
    ///     scrollback retention entirely (publishes skip the ring buffer).
    /// </summary>
    private readonly int _maxScrollback;

    /// <summary>
    ///     Whether the scrollback ring is currently being maintained (#518).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Retention used to be armed by capacity alone, which meant a ring
    ///         that nobody ever read still cost a slot write under a lock on every
    ///         publish <em>and</em> silently disqualified the fast path for every
    ///         shipped preset (<c>EventBusScrollback = 1000</c> → 0/200 qualifying
    ///         publishes on CLI and desktop).
    ///     </para>
    ///     <para>
    ///         It is now armed by the first call to <see cref="GetScrollback" />.
    ///         A ring is a potential, not an obligation: until somebody asks for
    ///         history there is nothing to retain, and the publish is genuinely
    ///         unobservable. The transition is one-way (<c>false</c> → <c>true</c>),
    ///         so <c>volatile</c> is sufficient — no lock, no ABA, and the
    ///         publish path stays a single lock-free read.
    ///     </para>
    ///     <para>
    ///         Deleting the ring instead was rejected: <see cref="IEventBus" /> is
    ///         public in the zero-dependency <c>Harbor.Abstractions</c> assembly
    ///         every plugin references, and a live bus is handed to out-of-tree
    ///         plugin code via <c>IPluginLoadHost.EventBus</c>. See
    ///         <c>tests/Harbor.Core.Tests/EventBusRetentionArmingGuardTests.cs</c>.
    ///     </para>
    /// </remarks>
    private volatile bool _retentionArmed;

    /// <summary>
    ///     Pre-allocated scrollback slots. Fixed capacity
    ///     (<see cref="_maxScrollback" />); entries are overwritten oldest-first
    ///     and never reallocated, so steady-state publishing allocates nothing.
    ///     Events are immutable records, so handing out references after the
    ///     slot is overwritten is safe (readers snapshot under the lock).
    /// </summary>
    private readonly AgentEvent[] _scrollbackRing;

    /// <summary>Guards <see cref="_scrollbackRing" />, <see cref="_ringHead"/> and <see cref="_ringCount"/>.</summary>
    private readonly object _scrollbackLock = new();

    /// <summary>Index of the OLDEST entry currently held in <see cref="_scrollbackRing"/>.</summary>
    private int _ringHead;

    /// <summary>Number of valid entries in <see cref="_scrollbackRing"/> (≤ <see cref="_maxScrollback"/>).</summary>
    private int _ringCount;

    /// <summary>
    ///     Construct a store with a bounded scrollback buffer of the supplied
    ///     capacity.
    /// </summary>
    /// <param name="maxScrollback">Maximum number of events retained for late-attaching subscribers. Zero or negative disables scrollback.</param>
    public EventBusScrollbackStore(int maxScrollback)
    {
        _maxScrollback = maxScrollback > 0 ? maxScrollback : 0;
        _scrollbackRing = _maxScrollback > 0 ? new AgentEvent[_maxScrollback] : Array.Empty<AgentEvent>();
    }

    /// <summary>
    ///     Whether history is currently being retained (#518). False until the
    ///     first <see cref="GetScrollback" /> call arms the ring, and true from
    ///     then on — the transition is one-way.
    /// </summary>
    public bool RetentionArmed => _retentionArmed;

    /// <summary>
    ///     Copy the requested tail of the retained history into an exact-size
    ///     array. Arms retention on the first call (#518) and returns an empty
    ///     list when the store is disabled or nothing has been retained.
    /// </summary>
    /// <param name="maxEvents">Maximum number of events to return; zero or negative returns an empty list.</param>
    public IReadOnlyList<AgentEvent> GetScrollback(int maxEvents)
    {
        if (_maxScrollback == 0)
        {
            return Array.Empty<AgentEvent>();
        }

        // #518: this call IS the reader, so it is what arms retention. The flag
        // is set BEFORE the snapshot lock, never after: once armed, every
        // publish that reads the flag appends to the ring, so history is
        // complete from this point on. Publishes that raced the arm (they read
        // the flag as false microseconds earlier) are the only ones a reader can
        // miss — an acceptable, documented gap for a diagnostic pull, and the
        // price of not maintaining a ring for nobody.
        _retentionArmed = true;

        if (maxEvents <= 0)
        {
            return Array.Empty<AgentEvent>();
        }

        // Copy the requested tail under the lock into an exact-size array.
        // Readers get a stable point-in-time snapshot: repeated calls see the
        // same history (§PERF-008 regression guarantee), and concurrent
        // publishers can never observe a torn view. The allocation happens
        // only on this (cold) diagnostic path — PublishAsync never allocates
        // for scrollback.
        lock (_scrollbackLock)
        {
            if (_ringCount == 0)
            {
                return Array.Empty<AgentEvent>();
            }

            int count = Math.Min(maxEvents, _ringCount);
            var result = new AgentEvent[count];
            int start = (_ringHead + _ringCount - count) % _maxScrollback;
            for (int i = 0; i < count; i++)
            {
                result[i] = _scrollbackRing[(start + i) % _maxScrollback];
            }

            return result;
        }
    }

    /// <summary>
    ///     Overwrite-append an event into the fixed-capacity ring. When the
    ///     buffer is at capacity the oldest slot is reused in place — no
    ///     allocation, ever. The lock scope covers only the index arithmetic
    ///     and the slot write; fan-out and logging stay outside.
    /// </summary>
    public void Append(AgentEvent @event)
    {
        if (_maxScrollback == 0 || !_retentionArmed)
        {
            return; // scrollback disabled, or no reader has asked for history yet (#518)
        }

        lock (_scrollbackLock)
        {
            if (_ringCount < _maxScrollback)
            {
                // Under capacity — fill the next free slot.
                _scrollbackRing[(_ringHead + _ringCount) % _maxScrollback] = @event;
                _ringCount++;
            }
            else
            {
                // At capacity — overwrite the oldest entry and advance the head.
                _scrollbackRing[_ringHead] = @event;
                _ringHead = (_ringHead + 1) % _maxScrollback;
            }
        }
    }
}
