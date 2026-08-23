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
    private readonly ServerPipeTransport _transport;
    private Task? _acceptTask;
    private int _disposed;

    /// <summary>
    ///     Construct an RPC server bound to the given transport.
    /// </summary>
    public MessagePackRpcServer(
        ServerPipeTransport transport,
        RequestDispatcher dispatcher,
        EventBroadcaster broadcaster,
        ILogger<MessagePackRpcServer> logger)
    {
        _transport = transport;
        _dispatcher = dispatcher;
        _broadcaster = broadcaster;
        _logger = logger;
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
        // Per-stream write lock so dispatcher responses and broadcaster
        // events never interleave half-frames on the wire.
        var writeLock = new SemaphoreSlim(1, 1);

        // D1: one frame reader per connection — it owns that connection's
        // frame-size / outstanding-bytes budgets.
        var frameReader = new ResilientFrameReader();

        // D1: registry of in-flight prompt runs on this connection, plus a
        // connection-scoped token that tears the whole handler down when the
        // server stops or a fatal write failure occurs.
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationToken connectionCt = connectionCts.Token;
        var runs = new Dictionary<Guid, PromptRun>();
        var runsLock = new Lock();

        try
        {
            _logger.LogInformation("Client connected");
            while (!connectionCt.IsCancellationRequested)
            {
                FrameReadResult read;
                try
                {
                    read = await frameReader.ReadRequestAsync(stream, connectionCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Stream read failed; closing client connection");
                    return;
                }

                switch (read.Outcome)
                {
                    case FrameReadOutcome.Request:
                        break;
                    case FrameReadOutcome.StreamEnded:
                        _logger.LogDebug("Client stream ended");
                        return;
                    case FrameReadOutcome.EmptyFrame:
                        _logger.LogDebug("Skipping zero-length frame; keeping connection alive");
                        continue;
                    case FrameReadOutcome.UndecodableFrame:
                        _logger.LogWarning(read.Error, "Skipping undecodable frame; keeping connection alive");
                        continue;
                    case FrameReadOutcome.OversizedFrame:
                        _logger.LogWarning(
                            read.Error, "Incoming frame violates the size/buffer budget; closing client connection");
                        return;
                    default:
                        _logger.LogWarning("Unknown frame outcome {Outcome}; closing client connection", read.Outcome);
                        return;
                }

                var request = read.Request!;

                // D1: prompts run as tracked background tasks so the read loop
                // keeps reading — this is what keeps AbortAgentRequest reachable
                // during an in-flight run.
                if (request is SendPromptRequest promptRequest)
                {
                    StartPromptRun(promptRequest, stream, writeLock, runs, runsLock, connectionCts, connectionCt);
                    continue;
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
                        CancelRegisteredRuns(runs, runsLock);

                        var abortResponse = await _dispatcher
                            .DispatchAsync(abortRequest, null, null, connectionCt)
                            .ConfigureAwait(false);
                        await WriteResponseAsync(stream, writeLock, abortResponse, connectionCt).ConfigureAwait(false);
                        continue;
                    }

                    // SubscribeToEventsRequest needs the reply stream AND the
                    // per-client write lock so the broadcaster can push
                    // out-of-band frames to this client without interleaving
                    // with our direct response frames. Everything else is quick
                    // and order-sensitive (CreateSession before SendPrompt, etc.)
                    // — handled inline to preserve strict per-connection order.
                    var replyStream = request is SubscribeToEventsRequest ? stream : null;
                    var replyWriteLock = request is SubscribeToEventsRequest ? writeLock : null;

                    var response = await _dispatcher
                        .DispatchAsync(request, replyStream, replyWriteLock, connectionCt)
                        .ConfigureAwait(false);

                    await WriteResponseAsync(stream, writeLock, response, connectionCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Request processing failed; closing client connection");
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
            // D1: orderly shutdown — cancel every in-flight run, then await its
            // task so nothing outlives the connection.
            Task[] pendingTasks;
            lock (runsLock)
            {
                foreach (var pair in runs)
                {
                    pair.Value.Cts.Cancel();
                }
                pendingTasks = new Task[runs.Count];
                int i = 0;
                foreach (var pair in runs)
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

            _broadcaster.Unregister(stream);
            try { await stream.DisposeAsync().ConfigureAwait(false); }
            catch (Exception disposeEx) { _logger.LogDebug(disposeEx, "Suppress stream dispose error"); }
            writeLock.Dispose();
            _logger.LogInformation("Client disconnected");
        }
    }

    // ── Background prompt dispatch (D1) ────────────────────────────────────

    private void StartPromptRun(
        SendPromptRequest request,
        Stream stream,
        SemaphoreSlim writeLock,
        Dictionary<Guid, PromptRun> runs,
        Lock runsLock,
        CancellationTokenSource connectionCts,
        CancellationToken connectionCt)
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
                        .DispatchAsync(request, null, null, runCts.Token)
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
