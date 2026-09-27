namespace Harbor.Ipc.Protocol;
/// <summary>
///     MessagePack RPC server. Accepts client streams from the
///     <see cref="ServerPipeTransport" />, reads length-prefixed frames of
///     <see cref="HarborRequest" />s, dispatches them through the
///     <see cref="RequestDispatcher" />, and writes
///     <see cref="HarborResponse" />s back. Server-pushed
///     <see cref="EventEnvelope" />s are interleaved on the same stream.
/// </summary>
/// <remarks>
///     <para>
///         <b>Concurrency model:</b> each connected client gets its own
///         dedicated <c>Task</c> that owns the read loop for that stream.
///         Writes are serialized through a per-stream <see cref="SemaphoreSlim" />
///         so concurrent request-dispatcher completions and event-broadcaster
///         pushes never interleave half-frames.
///     </para>
///     <para>
///         <b>Non-blocking dispatch (D1):</b> long-running requests —
///         <see cref="SendPromptRequest" />s — are dispatched as tracked
///         background tasks while the read loop keeps reading. This is what makes
///         an <see cref="AbortAgentRequest" /> reachable during an in-flight run:
///         the loop is never stuck awaiting a prompt. Every in-flight run is
///         registered in a per-connection registry keyed by its request id (the
///         wire requests carry no session id; the singleton agent is single-flight,
///         so at most one prompt can be active per connection anyway) together with
///         its own linked cancellation token source. An abort request cancels every
///         registered run immediately and then flows through the dispatcher so the
///         agent's global abort source fires too. Responses always echo the
///         originating <see cref="HarborRequest.RequestId" />, so completion order
///         across concurrent runs is irrelevant to clients. All other requests are
///         dispatched inline, which preserves strict ordering for cheap,
///         order-sensitive operations (e.g. CreateSession → SendPrompt).
///     </para>
///     <para>
///         <b>Clean shutdown:</b> the accept loop is cancelled via the
///         <see cref="StopAsync" />-provided cancellation token. In-flight request
///         tasks observe their per-request cancellation tokens and drain — the
///         per-connection cleanup awaits every pending dispatch task after
///         cancelling it. The server never throws on shutdown — it logs and returns.
///     </para>
///     <para>
///         <b>Frame resilience + budget (D2):</b> reads go through a
///         per-connection <see cref="ResilientFrameReader" /> enforcing a frame-size
///         cap and an outstanding-buffered-bytes budget. Zero-length frames and
///         undecodable payloads are logged and skipped — the connection stays alive.
///         Only terminal stream conditions (EOF, I/O error, cancellation) or a
///         protocol-error frame-budget violation close a connection, and a bad
///         connection never takes down the accept loop or the server.
///     </para>
/// </remarks>
public sealed class MessagePackRpcServer : IAsyncDisposable
{
    private readonly EventBroadcaster _broadcaster;
    private readonly CancellationTokenSource _cts = new();
    private readonly RequestDispatcher _dispatcher;
    private readonly ILogger<MessagePackRpcServer> _logger;
    private readonly string? _expectedPsk;
    private readonly IIpcServerTransport _transport;
    private int _connectionSequence;
    private Task? _acceptTask;
    private int _disposed;

    /// <summary>
    ///     Construct an RPC server bound to the given transport.
    /// </summary>
    /// <param name="transport">The bound transport clients connect through.</param>
    /// <param name="dispatcher">Request dispatcher.</param>
    /// <param name="broadcaster">Event broadcaster.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="expectedPsk">
    ///     When non-null, every connection must authenticate with a
    ///     <see cref="PskAuthRequest" /> carrying this key BEFORE any other
    ///     request — all earlier requests are rejected with a structured
    ///     PSK_REQUIRED error (fail-closed). Mandatory for TCP/tailscale
    ///     listeners; optional for UDS.
    /// </param>
    public MessagePackRpcServer(
        IIpcServerTransport transport,
        RequestDispatcher dispatcher,
        EventBroadcaster broadcaster,
        ILogger<MessagePackRpcServer> logger,
        string? expectedPsk = null)
    {
        _transport = transport;
        _dispatcher = dispatcher;
        _broadcaster = broadcaster;
        _logger = logger;
        _expectedPsk = expectedPsk;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>
    ///     Begin accepting client connections. Returns once the transport is
    ///     bound; the accept loop runs in the background.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        var acceptReader = await _transport.BindAsync(ct).ConfigureAwait(false);
        _broadcaster.Start();
        _acceptTask = AcceptLoopAsync(acceptReader, _cts.Token);
    }

    /// <summary>
    ///     Stop accepting new connections, drain in-flight requests, and
    ///     close the transport.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();

        if (_acceptTask is not null)
        {
            try { await _acceptTask.ConfigureAwait(false); }
            catch (OperationCanceledException)
            { /* expected on shutdown */
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Accept loop ended with error"); }
        }

        await _transport.UnbindAsync(ct).ConfigureAwait(false);
        await _broadcaster.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    // ── Accept loop ────────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(ChannelReader<Stream> acceptReader, CancellationToken ct)
    {
        await foreach (var stream in acceptReader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            _ = HandleClientAsync(stream, ct);
        }
    }

    // ── Per-client request loop ────────────────────────────────────────────

    private async Task HandleClientAsync(Stream stream, CancellationToken ct)
    {
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var session = new ClientSession(
            stream,
            new SemaphoreSlim(1, 1),
            connectionCts,
            $"c-{Interlocked.Increment(ref _connectionSequence):x8}",
            _expectedPsk is null);

        try
        {
            _logger.LogInformation("Client connected");
            while (!session.ConnectionCts.IsCancellationRequested)
            {
                FrameReadResult? read = await ReadRequestAsync(session).ConfigureAwait(false);
                if (read is null)
                {
                    return;
                }

                FrameDecision frame = HandleFrameOutcome(read);
                if (frame == FrameDecision.Close)
                {
                    return;
                }
                if (frame == FrameDecision.Skip)
                {
                    continue;
                }

                var request = read.Request!;

                switch (await ApplyPskGateAsync(session, request).ConfigureAwait(false))
                {
                    case GateDecision.Close:
                        return;
                    case GateDecision.Consumed:
                        continue;
                    default:
                        break;
                }

                if (!await DispatchRequestAsync(session, request).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Server shutting down — expected.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected client-handler failure; connection closed gracefully");
        }
        finally
        {
            await TeardownAsync(session).ConfigureAwait(false);
        }
    }

    /// <summary>Per-connection handler state: stream, write lock, prompt-run registry, identity, auth.</summary>
    private sealed class ClientSession(
        Stream stream,
        SemaphoreSlim writeLock,
        CancellationTokenSource connectionCts,
        string clientId,
        bool authenticated)
    {
        public readonly Stream Stream = stream;
        public readonly SemaphoreSlim WriteLock = writeLock;
        public readonly CancellationTokenSource ConnectionCts = connectionCts;
        public readonly string ClientId = clientId;

        // PSK gate: connections to a gated listener are unauthenticated
        // until a valid PskAuthRequest passes; everything before that gets
        // the structured PSK_REQUIRED error and nothing else.
        public bool Authenticated = authenticated;

        // D1: one frame reader per connection — it owns that connection's
        // frame-size / outstanding-bytes budgets.
        public readonly ResilientFrameReader FrameReader = new();

        // D1: registry of in-flight prompt runs on this connection.
        public readonly Dictionary<Guid, PromptRun> Runs = new();
        public readonly Lock RunsLock = new();

        public CancellationToken ConnectionCt => ConnectionCts.Token;
    }

    /// <summary>How the read loop should proceed after inspecting a frame.</summary>
    private enum FrameDecision : byte
    {
        Dispatch,
        Skip,
        Close,
    }

    /// <summary>How the read loop should proceed after the PSK gate.</summary>
    private enum GateDecision : byte
    {
        Proceed,
        Consumed,
        Close,
    }

    /// <summary>Reads one frame; returns <see langword="null" /> when the connection must close.</summary>
    private async Task<FrameReadResult?> ReadRequestAsync(ClientSession session)
    {
        try
        {
            return await session.FrameReader.ReadRequestAsync(session.Stream, session.ConnectionCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stream read failed; closing client connection");
            return null;
        }
    }

    /// <summary>Classifies a frame read: dispatch the request, skip the frame, or close the connection.</summary>
    private FrameDecision HandleFrameOutcome(FrameReadResult read)
    {
        switch (read.Outcome)
        {
            case FrameReadOutcome.Request:
                return FrameDecision.Dispatch;
            case FrameReadOutcome.StreamEnded:
                _logger.LogDebug("Client stream ended");
                return FrameDecision.Close;
            case FrameReadOutcome.EmptyFrame:
                _logger.LogDebug("Skipping zero-length frame; keeping connection alive");
                return FrameDecision.Skip;
            case FrameReadOutcome.UndecodableFrame:
                _logger.LogWarning(read.Error, "Skipping undecodable frame; keeping connection alive");
                return FrameDecision.Skip;
            case FrameReadOutcome.OversizedFrame:
                _logger.LogWarning(
                    read.Error, "Incoming frame violates the size/buffer budget; closing client connection");
                return FrameDecision.Close;
            default:
                _logger.LogWarning("Unknown frame outcome {Outcome}; closing client connection", read.Outcome);
                return FrameDecision.Close;
        }
    }

    /// <summary>
    ///     PSK gate (fail-closed): before authentication the only request
    ///     honored is <see cref="PskAuthRequest" />. A wrong key closes the
    ///     connection immediately; a missing key gets the structured error
    ///     and another chance to authenticate.
    /// </summary>
    private async Task<GateDecision> ApplyPskGateAsync(ClientSession session, HarborRequest request)
    {
        if (session.Authenticated)
        {
            return GateDecision.Proceed;
        }

        if (request is PskAuthRequest pskRequest)
        {
            if (!PskStore.Matches(pskRequest.Psk, _expectedPsk!))
            {
                _logger.LogWarning("PSK auth failed; closing connection");
                await TryWriteErrorResponse(
                    session.Stream, session.WriteLock, pskRequest.RequestId, "PSK_AUTH_FAILED", session.ConnectionCt)
                    .ConfigureAwait(false);
                return GateDecision.Close;
            }

            session.Authenticated = true;
            _logger.LogInformation("PSK auth succeeded");
            await WriteResponseAsync(
                session.Stream, session.WriteLock, new OkResponse { RequestId = pskRequest.RequestId }, session.ConnectionCt)
                .ConfigureAwait(false);
            return GateDecision.Consumed;
        }

        await TryWriteErrorResponse(
            session.Stream, session.WriteLock, request.RequestId,
            "PSK_REQUIRED: this listener requires pre-shared-key authentication first",
            session.ConnectionCt).ConfigureAwait(false);
        return GateDecision.Consumed;
    }

    /// <summary>
    ///     Routes one request: prompts run as tracked background tasks,
    ///     aborts cancel local runs then flow through the dispatcher,
    ///     everything else dispatches inline. Returns false when the
    ///     connection must close.
    /// </summary>
    private async Task<bool> DispatchRequestAsync(ClientSession session, HarborRequest request)
    {
        // D1: prompts run as tracked background tasks so the read loop
        // keeps reading — this is what keeps AbortAgentRequest reachable
        // during an in-flight run.
        if (request is SendPromptRequest promptRequest)
        {
            StartPromptRun(
                promptRequest, session.Stream, session.WriteLock, session.Runs, session.RunsLock,
                session.ConnectionCts, session.ConnectionCt, session.ClientId);
            return true;
        }

        try
        {
            // D1: an abort must cancel THIS connection's registered runs
            // directly (the wire request carries no session id), then flow
            // through the dispatcher so the agent's global abort source
            // fires as well. Handled inline — abort latency must not depend
            // on anything else in flight.
            if (request is AbortAgentRequest abortRequest)
            {
                CancelRegisteredRuns(session.Runs, session.RunsLock);

                var abortResponse = await _dispatcher
                    .DispatchAsync(abortRequest, null, null, session.ConnectionCt, session.ClientId)
                    .ConfigureAwait(false);
                await WriteResponseAsync(session.Stream, session.WriteLock, abortResponse, session.ConnectionCt).ConfigureAwait(false);
                return true;
            }

            // SubscribeToEventsRequest needs the reply stream AND the
            // per-client write lock so the broadcaster can push
            // out-of-band frames to this client without interleaving
            // with our direct response frames. Everything else is quick
            // and order-sensitive (CreateSession before SendPrompt, etc.)
            // — handled inline to preserve strict per-connection order.
            var replyStream = request is SubscribeToEventsRequest ? session.Stream : null;
            var replyWriteLock = request is SubscribeToEventsRequest ? session.WriteLock : null;

            var response = await _dispatcher
                .DispatchAsync(request, replyStream, replyWriteLock, session.ConnectionCt, session.ClientId)
                .ConfigureAwait(false);

            await WriteResponseAsync(session.Stream, session.WriteLock, response, session.ConnectionCt).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Request processing failed; closing client connection");
            return false;
        }
    }

    /// <summary>
    ///     Orderly connection teardown: cancel every in-flight run, await
    ///     them, release session leases, unregister from the broadcaster,
    ///     and dispose the stream + write lock.
    /// </summary>
    private async Task TeardownAsync(ClientSession session)
    {
        // D1: orderly shutdown — cancel every in-flight run, then await its
        // task so nothing outlives the connection.
        Task[] pendingTasks;
        lock (session.RunsLock)
        {
            foreach (var pair in session.Runs)
            {
                pair.Value.Cts.Cancel();
            }
            pendingTasks = new Task[session.Runs.Count];
            int i = 0;
            foreach (var pair in session.Runs)
            {
                pendingTasks[i++] = pair.Value.Worker;
            }
        }

        foreach (var pending in pendingTasks)
        {
            try { await pending.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* cancelled by the drain above */ }
            catch (Exception drainEx)
            {
                _logger.LogDebug(drainEx, "Suppressed fault from prompt task during connection drain");
            }
        }

        // A3 teardown: this connection's session leases die with it, so a
        // disconnected owner cannot wedge a session busy forever.
        _dispatcher.ReleaseClientLeases(session.ClientId);
        await _broadcaster.UnregisterAsync(session.Stream).ConfigureAwait(false);
        try { await session.Stream.DisposeAsync().ConfigureAwait(false); }
        catch (Exception disposeEx) { _logger.LogDebug(disposeEx, "Suppress stream dispose error"); }
        session.WriteLock.Dispose();
        _logger.LogInformation("Client disconnected");
    }

    // ── Background prompt dispatch (D1) ────────────────────────────────────

    private void StartPromptRun(
        SendPromptRequest request,
        Stream stream,
        SemaphoreSlim writeLock,
        Dictionary<Guid, PromptRun> runs,
        Lock runsLock,
        CancellationTokenSource connectionCts,
        CancellationToken connectionCt,
        string clientId)
    {
        var requestId = request.RequestId;
        var runCts = CancellationTokenSource.CreateLinkedTokenSource(connectionCt);

        // Schedule AND register under the same lock: AbortAgentRequest and
        // connection cleanup take <paramref name="runsLock" /> to find runs,
        // so the run is reachable from the moment it exists — no gap between
        // scheduling and registration. The runner task itself becomes the
        // tracked Worker (cleanup awaits it); it completes on every path and
        // never faults, so it is always observed.
        lock (runsLock)
        {
            Task worker = Task.Run(async () =>
            {
                try
                {
                    var response = await _dispatcher
                        .DispatchAsync(request, null, null, runCts.Token, clientId)
                        .ConfigureAwait(false);

                    await WriteResponseAsync(stream, writeLock, response, runCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Run aborted or connection/server shutting down — the client
                    // either already got an abort-driven failure from the agent or
                    // is gone; there is no meaningful response to deliver.
                }
                catch (Exception ex)
                {
                    // The dispatcher converts expected failures into ErrorResponse,
                    // so reaching this point means the connection is broken or a
                    // bug surfaced. Tell the client once, then tear the connection
                    // down via the shared token so the read loop exits cleanly.
                    _logger.LogError(ex, "Background prompt dispatch failed");
                    await TryWriteErrorResponse(stream, writeLock, requestId, ex.Message, connectionCt).ConfigureAwait(false);
                    connectionCts.Cancel();
                }
                finally
                {
                    lock (runsLock)
                    {
                        runs.Remove(requestId);
                    }
                    runCts.Dispose();
                }
            }, CancellationToken.None);

            runs[requestId] = new PromptRun(runCts, worker);
        }
    }

    private static void CancelRegisteredRuns(Dictionary<Guid, PromptRun> runs, Lock runsLock)
    {
        lock (runsLock)
        {
            foreach (var pair in runs)
            {
                pair.Value.Cts.Cancel();
            }
        }
    }

    private async Task WriteResponseAsync(Stream stream, SemaphoreSlim writeLock, HarborResponse response, CancellationToken ct)
    {
        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WireCodec.WriteResponseAsync(stream, response, ct).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private async Task TryWriteErrorResponse(
        Stream stream,
        SemaphoreSlim writeLock,
        Guid requestId,
        string message,
        CancellationToken ct)
    {
        try
        {
            var response = new ErrorResponse { RequestId = requestId, Message = message };
            await WriteResponseAsync(stream, writeLock, response, ct).ConfigureAwait(false);
        }
        catch (Exception writeEx)
        {
            _logger.LogDebug(writeEx, "Could not deliver error response; connection likely broken");
        }
    }

    /// <summary>
    ///     A tracked in-flight prompt run: its cancellation source plus a task
    ///     that completes when the runner finishes (always observed during
    ///     connection cleanup).
    /// </summary>
    private sealed record PromptRun(CancellationTokenSource Cts, Task Worker);
}
