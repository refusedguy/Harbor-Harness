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
///         <b>Clean shutdown:</b> the accept loop is cancelled via the
///         <see cref="StopAsync" />-provided cancellation token. In-flight
///         request tasks observe their per-request cancellation tokens and
///         drain. The server never throws on shutdown — it logs and returns.
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

        try
        {
            _logger.LogInformation("Client connected");
            while (!ct.IsCancellationRequested)
            {
                HarborRequest? request;
                try
                {
                    request = await WireCodec.ReadRequestAsync(stream, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (EndOfStreamException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Read failed; closing client");
                    break;
                }

                if (request is null) break;

                // SubscribeToEventsRequest needs the reply stream AND the
                // per-client write lock so the broadcaster can push
                // out-of-band frames to this client without interleaving
                // with our direct response frames.
                var replyStream = request is SubscribeToEventsRequest ? stream : null;
                var replyWriteLock = request is SubscribeToEventsRequest ? writeLock : null;

                var response = await _dispatcher
                    .DispatchAsync(request, replyStream, replyWriteLock, ct)
                    .ConfigureAwait(false);

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
        }
        finally
        {
            _broadcaster.Unregister(stream);
            try { await stream.DisposeAsync().ConfigureAwait(false); }
            catch (Exception disposeEx) { _logger.LogDebug(disposeEx, "Suppress stream dispose error"); }
            writeLock.Dispose();
            _logger.LogInformation("Client disconnected");
        }
    }
}
