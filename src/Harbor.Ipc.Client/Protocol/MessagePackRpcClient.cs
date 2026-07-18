namespace Harbor.Ipc.Protocol;

/// <summary>
///     MessagePack RPC client. Sends <see cref="HarborRequest" />s over a
///     <see cref="ClientPipeTransport" /> stream and awaits the matching
///     <see cref="HarborResponse" /> (matched by <see cref="HarborResponse.RequestId" />).
///     Out-of-band <see cref="EventEnvelope" /> frames are demultiplexed
///     into a per-client event channel.
/// </summary>
/// <remarks>
///     <para>
///         <b>Pipeline:</b> a single read loop drains the stream and routes
///         each incoming frame. <see cref="OkResponse" /> /
///         <see cref="ErrorResponse" /> complete the pending request's
///         <see cref="TaskCompletionSource{HarborResponse}" />;
///         <see cref="EventEnvelope" /> frames are pushed to the event
///         channel for the <see cref="EventSubscription" /> to enumerate.
///     </para>
///     <para>
///         <b>Concurrency:</b> a single writer serializes request frames
///         through a <see cref="SemaphoreSlim" />; multiple awaiters can be
///         in-flight at once, each waiting on its own TCS.
///     </para>
/// </remarks>
public sealed class MessagePackRpcClient : IAsyncDisposable
{
    private readonly Channel<HarborEvent> _eventChannel =
        Channel.CreateUnbounded<HarborEvent>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true
        });

    private readonly Dictionary<Guid, TaskCompletionSource<HarborResponse>> _pending = new();
    private readonly Lock _pendingLock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ClientPipeTransport _transport;
    private readonly ILogger _logger;
    private Stream? _stream;
    private Task? _readLoopTask;
    private CancellationTokenSource _cts = new();
    private int _disposed;

    /// <summary>
    ///     Construct an RPC client over the given transport.
    /// </summary>
    public MessagePackRpcClient(ClientPipeTransport transport, ILogger logger)
    {
        _transport = transport;
        _logger = logger;
    }

    /// <summary>
    ///     Reader side of the event channel. The <see cref="EventSubscription" />
    ///     enumerates this.
    /// </summary>
    public ChannelReader<HarborEvent> EventReader => _eventChannel.Reader;

    /// <summary>
    ///     Connect to the server and start the background read loop.
    ///     Idempotent — calling twice is a no-op.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_stream is not null) return;
        _stream = await _transport.ConnectAsync(ct).ConfigureAwait(false);
        _readLoopTask = ReadLoopAsync(_cts.Token);
    }

    /// <summary>
    ///     Send a request and await the matching response.
    /// </summary>
    public async Task<HarborResponse> SendAsync(HarborRequest request, CancellationToken ct = default)
    {
        if (_stream is null)
            throw new InvalidOperationException("Client is not connected. Call ConnectAsync first.");

        var tcs = new TaskCompletionSource<HarborResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock)
        {
            _pending[request.RequestId] = tcs;
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WireCodec.WriteRequestAsync(_stream, request, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        // Unregister the TCS on cancellation so the read loop doesn't try
        // to complete a dead TCS later.
        using var registration = ct.Register(() =>
        {
            lock (_pendingLock) { _pending.Remove(request.RequestId); }
            tcs.TrySetCanceled(ct);
        });

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _cts.Cancel();
        _eventChannel.Writer.TryComplete();

        if (_readLoopTask is not null)
        {
            try { await _readLoopTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected on shutdown */ }
            catch (Exception ex) { _logger.LogDebug(ex, "Read loop ended with error during dispose"); }
        }

        // Cancel any pending request TCSs so their awaiters don't hang.
        lock (_pendingLock)
        {
            foreach (var tcs in _pending.Values)
            {
                tcs.TrySetCanceled();
            }
            _pending.Clear();
        }

        await _transport.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
        _writeLock.Dispose();
    }

    // ── Read loop ──────────────────────────────────────────────────────────

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _stream is not null)
        {
            HarborResponse? response;
            try
            {
                response = await WireCodec.ReadResponseAsync(_stream, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (EndOfStreamException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Read failed; closing client");
                break;
            }

            if (response is null) break;

            if (response is EventEnvelope envelope)
            {
                HandleEventEnvelope(envelope);
                continue;
            }

            // Regular response — complete the matching TCS.
            TaskCompletionSource<HarborResponse>? tcs = null;
            lock (_pendingLock)
            {
                if (_pending.TryGetValue(response.RequestId, out tcs))
                {
                    _pending.Remove(response.RequestId);
                }
            }

            tcs?.TrySetResult(response);
        }

        // Stream ended — fail all pending requests so callers don't hang.
        lock (_pendingLock)
        {
            foreach (var tcs in _pending.Values)
            {
                tcs.TrySetException(new IOException("Server closed the connection"));
            }
            _pending.Clear();
        }
    }

    private void HandleEventEnvelope(EventEnvelope envelope)
    {
        if (envelope.EventBytes is null) return;

        try
        {
            HarborEventData data = MessagePackSerializer.Deserialize<HarborEventData>(envelope.EventBytes);
            HarborEvent evt = HarborEventMapping.FromData(data);
            if (!_eventChannel.Writer.TryWrite(evt))
            {
                _logger.LogDebug("Event channel full; dropping event {Kind}", evt.Kind);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize event envelope");
        }
    }
}
