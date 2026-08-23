namespace Harbor.Ipc.Protocol;
/// <summary>
///     Broadcasts <see cref="HarborEvent" />s to all connected client streams.
/// </summary>
/// <remarks>
///     <para>
///         The broadcaster subscribes to the host's <see cref="IEventBus" />
///         once and projects each <see cref="AgentEvent" /> down to a
///         <see cref="HarborEvent" /> (using a projection identical to
///         <c>InProcessHarborClient.ProjectEvent</c>). Each projected event
///         is then serialized into an <see cref="EventEnvelope" /> and
///         pushed to every registered client stream concurrently.
///     </para>
    ///     <para>
    ///         <b>Failure isolation:</b> if writing to one client throws
    ///         (broken pipe, client gone), the broadcaster removes that client
    ///         from its registration list and continues broadcasting to the
    ///         remaining clients. One dead client never blocks the others.
    ///     </para>
    ///     <para>
    ///         <b>Fan-out budget (B4):</b> the envelope is serialized once and
    ///         delivered to all clients concurrently via fully-observed tasks,
    ///         each bounded by a ~250 ms per-client write budget (lock acquire +
    ///         frame write). A slow or stuck client is unregistered when its
    ///         budget lapses instead of delaying every other client or the
    ///         publishing agent loop.
    ///     </para>
///     <para>
///         <b>Frame integrity:</b> each registered client carries its own
///         <see cref="SemaphoreSlim" /> write lock — the same lock the
///         per-client RPC loop uses to serialize its direct responses.
///         Without this, broadcaster-pushed <see cref="EventEnvelope" />
///         frames could interleave with dispatcher-written
///         <see cref="OkResponse" /> / <see cref="ErrorResponse" /> frames
///         on the same stream and corrupt the wire.
///     </para>
/// </remarks>
public sealed class EventBroadcaster : IAsyncDisposable
{
    private readonly List<ClientRegistration> _clients = new();
    private readonly Lock _clientsLock = new();
    private readonly IEventBus _eventBus;
    private readonly ILogger<EventBroadcaster> _logger;
    private int _currentTurn;
    private int _disposed;
    private IDisposable? _eventBusSubscription;
    private TaskCompletionSource<bool>? _subscriptionReady;

    // B4: per-client write budget — one slow/stuck client must never delay
    // delivery to the others (nor block the publishing agent loop) longer
    // than this.
    private static readonly TimeSpan ClientWriteTimeout = TimeSpan.FromMilliseconds(250);

    // B4: cheap snapshot cache. Membership mutations bump
    // <see cref="_membershipVersion" />; broadcasts rebuild the array only
    // when the version changed, so the steady state costs one integer
    // compare per event.
    private int _membershipVersion;
    private ClientRegistration[] _cachedSnapshot = Array.Empty<ClientRegistration>();
    private int _snapshotVersion = -1;

    /// <summary>
    ///     Construct a broadcaster. Call <see cref="Start" /> to begin
    ///     listening to <paramref name="eventBus" />.
    /// </summary>
    public EventBroadcaster(IEventBus eventBus, ILogger<EventBroadcaster> logger)
    {
        _eventBus = eventBus;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _eventBusSubscription?.Dispose();
        // Note: we don't dispose the client streams here — the per-client
        // RPC loop in MessagePackRpcServer owns them and disposes them on
        // disconnect. We only drop our references.
        lock (_clientsLock)
        {
            _clients.Clear();
            _membershipVersion++;
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
    ///     Register a client stream to receive future events. The
    ///     <paramref name="writeLock" /> is the same lock the per-client
    ///     RPC loop uses to serialize its direct responses — sharing it
    ///     ensures broadcaster-pushed events and dispatcher responses
    ///     never interleave half-frames on the wire.
    /// </summary>
    public void Register(Stream clientStream, SemaphoreSlim writeLock)
    {
        lock (_clientsLock)
        {
            // Replace any existing registration for the same stream.
            for (int i = 0; i < _clients.Count; i++)
            {
                if (ReferenceEquals(_clients[i].Stream, clientStream))
                {
                    _clients[i] = new ClientRegistration(clientStream, writeLock);
                    _membershipVersion++;
                    return;
                }
            }
            _clients.Add(new ClientRegistration(clientStream, writeLock));
            _membershipVersion++;
            _subscriptionReady?.TrySetResult(true);
        }
    }

    /// <summary>
    ///     Remove a client stream from the broadcast list. Does NOT dispose
    ///     the stream or the lock — the per-client RPC loop owns those and
    ///     will clean them up when the connection closes.
    /// </summary>
    public void Unregister(Stream clientStream)
    {
        lock (_clientsLock)
        {
            for (int i = 0; i < _clients.Count; i++)
            {
                if (ReferenceEquals(_clients[i].Stream, clientStream))
                {
                    _clients.RemoveAt(i);
                    _membershipVersion++;
                    return;
                }
            }
        }
    }

    private async ValueTask OnEventAsync(AgentEvent evt, CancellationToken ct)
    {
        var projected = ProjectEvent(evt);
        if (projected is null) return;

        // Serialize the envelope exactly once, before the fan-out loop.
        var data = HarborEventMapping.ToData(projected);
        byte[] eventBytes = MessagePackSerializer.Serialize(data, cancellationToken: ct);
        var envelope = new EventEnvelope { EventBytes = eventBytes };

        ClientRegistration[] snapshot = SnapshotClients();
        if (snapshot.Length == 0) return;

        // B4: deliver concurrently with a per-client write budget. Every send is
        // a bounded, fully-observed task — no unobserved fire-and-forget. A
        // client that times out or throws is marked dead and unregistered after
        // the fan-out completes; it can never stall the others or the agent loop.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ClientWriteTimeout);

        var sends = new Task[snapshot.Length];
        var alive = new bool[snapshot.Length];
        for (int i = 0; i < snapshot.Length; i++)
        {
            sends[i] = SendToClientAsync(snapshot[i], envelope, timeoutCts.Token, alive, i);
        }

        await Task.WhenAll(sends).ConfigureAwait(false);

        RemoveDeadClients(snapshot, alive);
    }

    /// <summary>
    ///     One bounded send to one client: take the per-client write lock within
    ///     the budget, push the pre-serialized envelope, release the lock. Any
    ///     failure — timeout, broken pipe, shutdown cancellation — marks the
    ///     client dead instead of throwing to the fan-out loop.
    /// </summary>
    private async Task SendToClientAsync(
        ClientRegistration client,
        EventEnvelope envelope,
        CancellationToken writeCt,
        bool[] alive,
        int index)
    {
        try
        {
            // Take the per-client write lock so broadcaster-pushed frames never
            // interleave with dispatcher response frames on the same stream.
            if (!await client.WriteLock.WaitAsync(ClientWriteTimeout, writeCt).ConfigureAwait(false))
            {
                return; // could not acquire the lock within budget → treat as dead
            }
            try
            {
                await WireCodec.WriteResponseAsync(client.Stream, envelope, writeCt).ConfigureAwait(false);
                alive[index] = true;
            }
            finally
            {
                client.WriteLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Client stream write failed or timed out; unregistering");
        }
    }

    private void RemoveDeadClients(ClientRegistration[] snapshot, bool[] alive)
    {
        lock (_clientsLock)
        {
            bool removedAny = false;
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (alive[i]) continue;

                for (int j = 0; j < _clients.Count; j++)
                {
                    if (ReferenceEquals(_clients[j].Stream, snapshot[i].Stream))
                    {
                        _clients.RemoveAt(j);
                        removedAny = true;
                        break;
                    }
                }
            }

            if (removedAny)
            {
                _membershipVersion++;
            }
        }
        // Don't dispose the streams here — the per-client RPC loop owns them.
    }

    /// <summary>
    ///     Snapshot of the registered clients, cached across broadcasts and
    ///     rebuilt only when membership actually changed (B4).
    /// </summary>
    private ClientRegistration[] SnapshotClients()
    {
        lock (_clientsLock)
        {
            if (_snapshotVersion != _membershipVersion)
            {
                var clients = new ClientRegistration[_clients.Count];
                for (int i = 0; i < _clients.Count; i++)
                {
                    clients[i] = _clients[i];
                }
                _cachedSnapshot = clients;
                _snapshotVersion = _membershipVersion;
            }

            return _cachedSnapshot;
        }
    }

    // ── Internal: AgentEvent → HarborEvent projection ──────────────────────
    //
    // This projection is intentionally identical to the one in
    // InProcessHarborClient.ProjectEvent. Keeping them in lockstep means
    // in-process and IPC clients observe identical event streams for the
    // same agent run.

    private HarborEvent? ProjectEvent(AgentEvent evt)
    {
        return evt switch
        {
            AgentStartEvent e =>
                ResetTurnAndProject(e.SessionId),
            MessageStartEvent => null,
            MessageUpdateEvent e =>
                new HarborEvent.MessageUpdate(e.Partial, ExtractDelta(e.LlmEvent)),
            MessageEndEvent e =>
                new HarborEvent.MessageEnd(e.Message),
            ToolExecutionStartEvent e =>
                new HarborEvent.ToolStart(e.ToolCallId, e.ToolName),
            ToolExecutionEndEvent e =>
                new HarborEvent.ToolEnd(e.ToolCallId, e.Result),
            TurnStartEvent e =>
                TrackTurnAndProject(e.TurnIndex),
            TurnEndEvent =>
                new HarborEvent.TurnEnd(_currentTurn),
            AgentEndEvent => null,
            AgentErrorEvent e =>
                new HarborEvent.AgentError(e.Message),
            CompactionStartedEvent e =>
                new HarborEvent.CompactionStarted(e.SessionId),
            CompactionCompletedEvent e =>
                new HarborEvent.CompactionCompleted(e.SessionId, e.PrunedMessageCount, e.TokensSaved),
            SessionStatsEvent => null,
            _ => null
        };
    }

    private HarborEvent ResetTurnAndProject(string sessionId)
    {
        _currentTurn = 0;
        return new HarborEvent.AgentStarted(sessionId);
    }

    private HarborEvent TrackTurnAndProject(int turnIndex)
    {
        _currentTurn = turnIndex;
        return new HarborEvent.TurnStart(turnIndex);
    }

    private static string ExtractDelta(LlmEvent llmEvent)
    {
        return llmEvent switch
        {
            TextDeltaEvent e => e.Delta,
            ThinkingDeltaEvent e => e.Delta,
            ToolCallDeltaEvent e => e.ArgsDelta,
            _ => string.Empty
        };
    }

    /// <summary>
    ///     A registered client connection: its reply stream plus the
    ///     per-client write lock that serializes all writes to that stream.
    /// </summary>
    private sealed class ClientRegistration
    {

        public ClientRegistration(Stream stream, SemaphoreSlim writeLock)
        {
            Stream = stream;
            WriteLock = writeLock;
        }
        /// <summary>The client's reply stream.</summary>
        public Stream Stream { get; }

        /// <summary>Per-client write lock shared with the RPC server's per-client loop.</summary>
        public SemaphoreSlim WriteLock { get; }
    }
}
