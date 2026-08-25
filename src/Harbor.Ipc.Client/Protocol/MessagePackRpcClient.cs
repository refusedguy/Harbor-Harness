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
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<EventFrame> _frameChannel =
        Channel.CreateBounded<EventFrame>(new BoundedChannelOptions(4096)
        {
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private readonly ILogger _logger;
    private readonly string? _psk;

    private readonly Dictionary<Guid, TaskCompletionSource<HarborResponse>> _pending = new();
    private readonly Lock _pendingLock = new();
    private readonly IIpcClientTransport _transport;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _disposed;
    private Task? _readLoopTask;
    private Stream? _stream;

    /// <summary>
    ///     Construct an RPC client over the given transport.
    /// </summary>
    /// <param name="transport">Transport to dial through.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="psk">
    ///     Optional pre-shared key: sent as a <see cref="PskAuthRequest" />
    ///     immediately after the stream opens. Required against PSK-gated
    ///     (TCP/tailscale) listeners; ignored by ungated ones.
    /// </param>
    public MessagePackRpcClient(IIpcClientTransport transport, ILogger logger, string? psk = null)
    {
        _transport = transport;
        _logger = logger;
        _psk = psk;
    }

    /// <summary>
    ///     Reader side of the event channel. Frames carry the server-assigned
    ///     envelope sequence so reconnecting clients can dedup and bookkeep.
    /// </summary>
    public ChannelReader<EventFrame> EventFrames => _frameChannel.Reader;

    /// <summary>
    ///     Raised when the read loop dies from EOF/IO error (NOT on Dispose).
    ///     Reconnecting callers use this as the "dial again" trigger.
    /// </summary>
    public event EventHandler ConnectionLost = delegate { };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _cts.Cancel();
        _frameChannel.Writer.TryComplete();

        if (_readLoopTask is not null)
        {
            try { await _readLoopTask.ConfigureAwait(false); }
            catch (OperationCanceledException)
            { /* expected on shutdown */
            }
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

    /// <summary>
    ///     Connect to the server and start the background read loop.
    ///     Idempotent — calling twice is a no-op.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_stream is not null) return;
        _stream = await _transport.ConnectAsync(ct).ConfigureAwait(false);
        _readLoopTask = ReadLoopAsync(_cts.Token);

        if (_psk is not null)
        {
            HarborResponse authResponse = await SendAsync(new PskAuthRequest(_psk), ct).ConfigureAwait(false);
            if (authResponse is ErrorResponse authError)
            {
                throw new IOException($"PSK authentication failed: {authError.Message}");
            }
        }
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

        bool written = false;
        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await WireCodec.WriteRequestAsync(_stream, request, ct).ConfigureAwait(false);
                written = true;
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch
        {
            if (!written)
            {
                // The request never made it to the wire — remove the TCS NOW,
                // otherwise it leaks in _pending forever (ipc-deep §2).
                lock (_pendingLock) { _pending.Remove(request.RequestId); }
            }

            throw;
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

        // Stream ended — fail all pending requests so callers don't hang,
        // reset dial state so ConnectAsync can re-open a fresh connection,
        // and signal reconnecting callers (ipc-deep §2: "_stream is not null"
        // used to make every later call write into a dead pipe forever).
        lock (_pendingLock)
        {
            foreach (var tcs in _pending.Values)
            {
                tcs.TrySetException(new IOException("Server closed the connection"));
            }
            _pending.Clear();
        }

        _stream = null;
        try { await _transport.DisconnectAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Suppress transport disconnect after read-loop death"); }

        if (Volatile.Read(ref _disposed) == 0)
        {
            ConnectionLost.Invoke(this, EventArgs.Empty);
        }
    }

    private void HandleEventEnvelope(EventEnvelope envelope)
    {
        if (envelope.EventBytes is null) return;

        try
        {
            var data = MessagePackSerializer.Deserialize<HarborEventData>(envelope.EventBytes);
            var evt = HarborEventMapping.FromData(data);
            // Honestly bounded (A4): DropOldest keeps memory flat when a UI
            // consumer stalls — the old "channel full" branch was dead code
            // over an unbounded channel.
            _frameChannel.Writer.TryWrite(new EventFrame(envelope.Sequence, evt));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize event envelope");
        }
    }
}

/// <summary>
///     One received event plus its server-assigned delivery sequence.
/// </summary>
public readonly record struct EventFrame(ulong Sequence, HarborEvent Event);
