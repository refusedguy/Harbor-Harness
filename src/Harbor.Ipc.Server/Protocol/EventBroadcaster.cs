namespace Harbor.Ipc.Protocol;
/// <summary>
///     Broadcasts <see cref="HarborEvent" />s to all connected client streams.
/// </summary>
/// <remarks>
///     <para>
///         The broadcaster subscribes to the host's <see cref="IEventBus" />
///         once and projects each <see cref="AgentEvent" /> down to a
///         <see cref="HarborEvent" /> with the shared
///         <see cref="AgentEventProjector" /> — the same table the in-process
///         client uses, so both hosts observe an identical stream (#495). Each
///         projected event is serialized once into an
///         <see cref="EventEnvelope" />, stamped
///         with a monotonic <see cref="EventEnvelope.Sequence"/>, appended to
///         the replay ring, and fanned out into each registered client's
///         outbound queue.
///     </para>
///     <para>
///         <b>Per-client writer tasks (B4/A4):</b> fan-out never touches the
///         wire directly — each client owns a bounded outbound channel and a
///         dedicated writer task that serializes frames through the shared
///         per-client write lock under a ~250 ms budget. One slow or stuck
///         client fills its own queue and gets evicted; nobody else stalls,
///         and the publishing agent loop never blocks on I/O.
///     </para>
///     <para>
///         <b>Failure isolation:</b> a writer that times out or throws closes
///         the client's stream (never silently drops it) — the client's read
///         loop sees EOF and can reconnect, rather than waiting forever on a
///         subscription that stopped delivering.
///     </para>
///     <para>
///         <b>Replay (A1):</b> the ring retains the last
///         <see cref="MaxReplayEnvelopes"/> envelopes. A reconnecting client
///         sends its <see cref="SubscribeToEventsRequest.LastSequence"/>; gaps
///         that fit the ring are replayed into its outbound queue BEFORE live
///         delivery starts (single queue ⇒ order preserved); larger gaps get
///         <c>ResyncRequired</c> so the client rebuilds from a fresh snapshot.
///     </para>
///     <para>
///         <b>Frame integrity:</b> each client carries its own
///         <see cref="SemaphoreSlim" /> write lock — the same lock the
///         per-client RPC loop uses for direct responses, so
///         broadcaster-pushed <see cref="EventEnvelope" /> frames never
///         interleave with dispatcher-written responses on the wire.
///     </para>
/// </remarks>
public sealed class EventBroadcaster : IAsyncDisposable
{
    /// <summary>Reconnect replay window (A1 MAX_GAP): envelopes retained server-side.</summary>
    public const int MaxReplayEnvelopes = 1000;

    private readonly Lock _clientsLock = new();
    private readonly List<ClientRegistration> _clients = new();
    private readonly IEventBus _eventBus;
    private readonly ILogger<EventBroadcaster> _logger;
    private readonly SessionLeaseRegistry? _leases;

    // Session whose run is currently streaming (A3) plus the session-scoped
    // turn indices, both owned by the SHARED projector (#495). Routing only
    // READS this state; an event type the projector does not know is counted
    // and logged, never dropped quietly.
    private readonly ProjectionState _projection = new();

    // Replay ring: fixed-capacity, overwrite-in-place (same shape as the
    // event bus scrollback). Envelopes are immutable; readers copy under lock.
    private readonly EventEnvelope[] _ring = new EventEnvelope[MaxReplayEnvelopes];
    private int _ringHead;
    private int _ringCount;

    // B4: per-client write budget — one slow/stuck client must never hold a
    // wire slot longer than this.
    private static readonly TimeSpan ClientWriteTimeout = TimeSpan.FromMilliseconds(250);

    private ulong _sequence;
    private int _disposed;

    // #495: how many events the projector could not map. Must stay 0 — a
    // non-zero value means a new AgentEvent subtype shipped without a case.
    private long _unmappedEventCount;
    private IDisposable? _eventBusSubscription;
    private TaskCompletionSource<bool>? _subscriptionReady;

    /// <summary>
    ///     Number of bus events the shared projector reported as
    ///     <see cref="EventProjectionOutcome.Unmapped" />. Always 0 in a healthy
    ///     build — the coverage test fails if a new <c>AgentEvent</c> subtype has no
    ///     case — and exposed so a host can be asserted on it in production too.
    /// </summary>
    public long UnmappedEventCount => Interlocked.Read(ref _unmappedEventCount);

    /// <summary>
    ///     Construct a broadcaster. Call <see cref="Start" /> to begin
    ///     listening to <paramref name="eventBus" />.
    /// </summary>
    /// <param name="eventBus">Host event bus.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="leases">
    ///     Optional session-lease registry enabling addressed delivery (A3);
    ///     null keeps legacy broadcast-to-all behavior.
    /// </param>
    public EventBroadcaster(IEventBus eventBus, ILogger<EventBroadcaster> logger, SessionLeaseRegistry? leases = null)
    {
        _eventBus = eventBus;
        _logger = logger;
        _leases = leases;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _eventBusSubscription?.Dispose();

        List<ClientRegistration> members;
        lock (_clientsLock)
        {
            members = new List<ClientRegistration>(_clients);
            _clients.Clear();
        }

        foreach (var client in members)
        {
            await client.StopWriterAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Subscribe to the event bus and begin broadcasting.</summary>
    public void Start() => _eventBusSubscription = _eventBus.Subscribe(OnEventAsync);

    /// <summary>Completes when the first client registers for event streaming.</summary>
    public Task SubscriptionReady
    {
        get
        {
            if (_subscriptionReady is null)
            {
                Interlocked.CompareExchange(ref _subscriptionReady,
                    new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously), null);
            }
            return _subscriptionReady.Task;
        }
    }

    /// <summary>
    ///     Register a client stream for event delivery, optionally replaying
    ///     everything after <paramref name="lastSequence" /> (the client's
    ///     last processed <see cref="EventEnvelope.Sequence"/>).
    /// </summary>
    /// <param name="clientStream">The client's reply stream.</param>
    /// <param name="writeLock">
    ///     Per-client write lock shared with the RPC server's response path.
    /// </param>
    /// <param name="lastSequence">
    ///     Null = first subscription (no replay). Non-null = replay request.
    /// </param>
    /// <param name="clientId">The connection id used for addressed delivery.</param>
    /// <returns>The ack data delivered to the subscriber.</returns>
    public async Task<SubscriptionAckData> RegisterAsync(
        Stream clientStream,
        SemaphoreSlim writeLock,
        ulong? lastSequence,
        string clientId)
    {
        var registration = new ClientRegistration(clientStream, writeLock, clientId);

        bool resyncRequired = false;
        lock (_clientsLock)
        {
            // Replace any existing registration for the same stream.
            bool replaced = false;
            for (int i = 0; i < _clients.Count; i++)
            {
                if (ReferenceEquals(_clients[i].Stream, clientStream))
                {
                    _clients[i] = registration;
                    replaced = true;
                    break;
                }
            }

            if (!replaced)
            {
                // Gap/replay bookkeeping only matters for a fresh connection.
                ulong oldest = OldestSequenceUnsafe();
                if (lastSequence.HasValue)
                {
                    if (lastSequence.Value + 1 >= oldest || _ringCount == 0)
                    {
                        // Gap fits the replay window: enqueue every buffered
                        // envelope newer than the client's position, in order.
                        // Membership is added AFTER these are queued, so the
                        // single outbound channel guarantees replay-before-live.
                        ReplayIntoUnsafe(registration, lastSequence.Value);
                    }
                    else
                    {
                        // The client missed more than we retain — it must
                        // rebuild from a fresh snapshot.
                        resyncRequired = true;
                    }
                }

                _clients.Add(registration);
            }
        }

        registration.StartWriter(_logger);
        _subscriptionReady?.TrySetResult(true);

        return new SubscriptionAckData
        {
            ServerSequence = Volatile.Read(ref _sequence),
            ResyncRequired = resyncRequired
        };
    }

    /// <summary>
    ///     Remove a client stream from the broadcast list and stop its writer.
    ///     Does NOT dispose the stream or the lock — the per-client RPC loop
    ///     owns those and cleans up when the connection closes.
    /// </summary>
    public async Task UnregisterAsync(Stream clientStream)
    {
        ClientRegistration? removed = null;
        lock (_clientsLock)
        {
            for (int i = 0; i < _clients.Count; i++)
            {
                if (ReferenceEquals(_clients[i].Stream, clientStream))
                {
                    removed = _clients[i];
                    _clients.RemoveAt(i);
                    break;
                }
            }
        }

        if (removed is not null)
        {
            await removed.StopWriterAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask OnEventAsync(AgentEvent evt, CancellationToken ct)
    {
        var projection = AgentEventProjector.Project(evt, _projection);
        if (projection.Outcome == EventProjectionOutcome.Unmapped)
        {
            ReportUnmappedEvent(evt);
            return;
        }

        if (projection.Event is not { } projected) return;

        string target = ResolveTarget(evt);

        var data = HarborEventMapping.ToData(projected);
        byte[] eventBytes = MessagePackSerializer.Serialize(data, cancellationToken: ct);
        var envelope = new EventEnvelope
        {
            EventBytes = eventBytes,
            Sequence = Interlocked.Increment(ref _sequence),
            TargetClientId = string.IsNullOrEmpty(target) ? null : target
        };

        AppendRing(envelope);

        ClientRegistration[] snapshot;
        lock (_clientsLock)
        {
            snapshot = new ClientRegistration[_clients.Count];
            for (int i = 0; i < _clients.Count; i++)
            {
                snapshot[i] = _clients[i];
            }
        }

        if (snapshot.Length == 0) return;

        // Fan-out = non-blocking enqueue into per-client queues, SKIPPING
        // clients the event is not addressed to (A3: null target = broadcast).
        // A client whose queue is full is evicted below (it stopped reading —
        // the wire budget cannot save it anyway).
        bool[] alive = new bool[snapshot.Length];
        for (int i = 0; i < snapshot.Length; i++)
        {
            if (!snapshot[i].IsTargetOf(envelope))
            {
                // Not addressed here — perfectly healthy, never evict.
                alive[i] = true;
                continue;
            }

            alive[i] = snapshot[i].TryDeliver(envelope);
        }

        for (int i = 0; i < snapshot.Length; i++)
        {
            if (!alive[i])
            {
                _logger.LogDebug("Evicting client whose outbound queue is full");
                await EvictAsync(snapshot[i]).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     Addressing rules (A3): session-carrying events target their
    ///     session's lease owner; everything else inherits the active run's
    ///     owner; unleased periods broadcast to everyone.
    /// </summary>
    /// <remarks>
    ///     A PURE read of the projection state (#495) — it used to mutate
    ///     <c>_activeSessionId</c> from inside the routing switch, which is why the
    ///     two copies of the projection had drifted apart. The active session is
    ///     now set by <see cref="AgentEventProjector" /> when the run starts.
    /// </remarks>
    private string ResolveTarget(AgentEvent evt)
    {
        if (_leases is null) return string.Empty;

        switch (evt)
        {
            case AgentStartEvent started:
                return _leases.GetOwner(started.SessionId) ?? string.Empty;
            case CompactionStartedEvent compaction:
                return _leases.GetOwner(compaction.SessionId) ?? ActiveOwner();
            case CompactionCompletedEvent completed:
                return _leases.GetOwner(completed.SessionId) ?? ActiveOwner();
        }

        return ActiveOwner();
    }

    /// <summary>Live lookup of the active run's owner — empty when unleased.</summary>
    private string ActiveOwner()
    {
        if (_projection.ActiveSessionId is not { } active || _leases is null) return string.Empty;
        return _leases.GetOwner(active) ?? string.Empty;
    }

    /// <summary>
    ///     #495: an event type the shared projector does not know. Logged at Error
    ///     and counted, so a new <c>AgentEvent</c> subtype can never vanish from the
    ///     stream without a trace.
    /// </summary>
    private void ReportUnmappedEvent(AgentEvent evt)
    {
        Interlocked.Increment(ref _unmappedEventCount);
        _logger.LogError(
            "No AgentEvent -> HarborEvent mapping for {EventType}; event NOT delivered. Add a case to AgentEventProjector (Harbor.Ipc.Abstractions).",
            evt.GetType().FullName);
    }

    private async Task EvictAsync(ClientRegistration dead)
    {
        bool removed = false;
        lock (_clientsLock)
        {
            for (int i = 0; i < _clients.Count; i++)
            {
                if (ReferenceEquals(_clients[i].Stream, dead.Stream))
                {
                    _clients.RemoveAt(i);
                    removed = true;
                    break;
                }
            }
        }

        if (removed)
        {
            await dead.StopWriterAsync().ConfigureAwait(false);
            // Close the stream so the CLIENT notices immediately (EOF on its
            // read loop) instead of silently losing the subscription — this
            // is what makes reconnect possible at all.
            try { await dead.Stream.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Suppress evicted-stream dispose error"); }
        }
    }

    // ── Replay ring ────────────────────────────────────────────────────────

    private void AppendRing(EventEnvelope envelope)
    {
        lock (_clientsLock)
        {
            if (_ringCount < MaxReplayEnvelopes)
            {
                _ring[(_ringHead + _ringCount) % MaxReplayEnvelopes] = envelope;
                _ringCount++;
            }
            else
            {
                _ring[_ringHead] = envelope;
                _ringHead = (_ringHead + 1) % MaxReplayEnvelopes;
            }
        }
    }

    /// <summary>Sequence of the OLDEST retained envelope (next sequence when empty).</summary>
    private ulong OldestSequenceUnsafe()
        => _ringCount == 0 ? _sequence + 1 : _ring[_ringHead].Sequence;

    private void ReplayIntoUnsafe(ClientRegistration registration, ulong lastSeen)
    {
        for (int i = 0; i < _ringCount; i++)
        {
            var envelope = _ring[(_ringHead + i) % MaxReplayEnvelopes];
            if (envelope.Sequence <= lastSeen) continue;
            if (envelope.TargetClientId is not null && envelope.TargetClientId != registration.ClientId) continue;
            registration.TryDeliver(envelope);
        }
    }

    // ── Internal: AgentEvent → HarborEvent projection ──────────────────────
    //
    // #495: there is NO local projection left. The table lives in
    // AgentEventProjector (Harbor.Ipc.Abstractions) and is shared with the
    // in-process client, so a new event case cannot be added here and forgotten
    // there. What stays host-local is `_projection` (the state the table reads)
    // and `ReportUnmappedEvent` (the loud failure path).

    /// <summary>
    ///     Data returned to a subscribing client: the server's current
    ///     sequence plus whether it must rebuild from a snapshot.
    ///     Serialized into the subscribe OkResponse payload.
    /// </summary>
    public sealed class SubscriptionAckData
    {
        /// <summary>Server's most recently assigned envelope sequence.</summary>
        public ulong ServerSequence { get; init; }

        /// <summary>True when the client's gap exceeds the replay window.</summary>
        public bool ResyncRequired { get; init; }
    }

    /// <summary>
    ///     A registered client: its reply stream, connection id, the shared
    ///     write lock, and a bounded outbound queue drained by a dedicated
    ///     writer task.
    /// </summary>
    private sealed class ClientRegistration
    {
        // Bounded honestly: TryWrite failing means the client stopped
        // consuming at wire speed — eviction, not silent growth.
        private readonly Channel<EventEnvelope> _outbound =
            Channel.CreateBounded<EventEnvelope>(new BoundedChannelOptions(4096)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

        public ClientRegistration(Stream stream, SemaphoreSlim writeLock, string clientId)
        {
            Stream = stream;
            WriteLock = writeLock;
            ClientId = clientId;
        }

        /// <summary>The client's reply stream.</summary>
        public Stream Stream { get; }

        /// <summary>The connection id used for addressed delivery.</summary>
        public string ClientId { get; }

        /// <summary>Per-client write lock shared with the RPC server's per-client loop.</summary>
        public SemaphoreSlim WriteLock { get; }

        /// <summary>True when the envelope is broadcast or addressed to THIS client.</summary>
        public bool IsTargetOf(EventEnvelope envelope)
            => envelope.TargetClientId is null || envelope.TargetClientId == ClientId;

        private CancellationTokenSource? _writerCts;
        private Task? _writerTask;

        /// <summary>Queue one envelope; false when the client must be evicted.</summary>
        public bool TryDeliver(EventEnvelope envelope) => _outbound.Writer.TryWrite(envelope);

        /// <summary>Spawn the background writer draining the queue to the wire.</summary>
        public void StartWriter(ILogger logger)
        {
            _writerCts = new CancellationTokenSource();
            _writerTask = WriteLoopAsync(_writerCts.Token, logger);
        }

        /// <summary>Complete the queue and await the writer's exit.</summary>
        public async Task StopWriterAsync()
        {
            _outbound.Writer.TryComplete();
            if (_writerCts is not null)
            {
                try { await _writerCts.CancelAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException ex)
                {
                    // Already torn down by a concurrent stop — nothing to do.
                    _ = ex;
                }
            }

            if (_writerTask is not null)
            {
                try { await _writerTask.ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    // Expected on stop.
                }
                catch (Exception ex)
                {
                    // The stream was already broken; the eviction path owns
                    // the teardown, so a fault here carries no new signal.
                    _ = ex.Message;
                }
            }

            if (_writerCts is not null)
            {
                _writerCts.Dispose();
                _writerCts = null;
            }
        }

        private async Task WriteLoopAsync(CancellationToken ct, ILogger logger)
        {
            try
            {
                await foreach (var envelope in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    if (!await WriteLock.WaitAsync(ClientWriteTimeout, ct).ConfigureAwait(false))
                    {
                        // Could not take the wire within budget → client too slow.
                        await EvictSelfAsync(logger).ConfigureAwait(false);
                        return;
                    }

                    using var frameCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    frameCts.CancelAfter(ClientWriteTimeout);
                    try
                    {
                        await WireCodec.WriteResponseAsync(Stream, envelope, frameCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        WriteLock.Release();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown path.
            }
            catch (Exception ex)
            {
                // Broken pipe / timeout: tear THIS client down loudly — the
                // stream close gives the client a clean reconnect signal.
                logger.LogDebug(ex, "Client writer failed; closing stream");
                await EvictSelfAsync(logger).ConfigureAwait(false);
            }
        }

        private async Task EvictSelfAsync(ILogger logger)
        {
            try { await Stream.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { logger.LogDebug(ex, "Supress stream dispose during eviction"); }
        }
    }
}
