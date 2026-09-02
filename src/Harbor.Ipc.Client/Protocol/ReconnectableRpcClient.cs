using System.Runtime.CompilerServices;

namespace Harbor.Ipc.Protocol;

/// <summary>
///     Self-healing RPC client (sprint 6 A1): wraps dialing, the raw
///     <see cref="MessagePackRpcClient"/>, and a reconnecting event stream.
/// </summary>
/// <remarks>
///     <para>
///         <b>Reconnect protocol</b> (reference: t3code ws.ts): exponential
///         backoff 0.5 s → 30 s cap with ±20 % jitter; on every redial the
///         client re-subscribes FIRST, presenting its last processed
///         <see cref="EventEnvelope.Sequence"/>; the server then replays the
///         missed range in order (<see cref="EventBroadcaster.MaxReplayEnvelopes"/>)
///         or answers <c>ResyncRequired</c>.
///     </para>
///     <para>
///         <b>Subscribe-before-snapshot:</b> when a (re)sync is needed the
///         subscription is established BEFORE the consumer's snapshot loader
///         runs, so events fired during the snapshot fetch sit in the server
///         queue — zero loss window. Frames at or below the ack's
///         <c>ServerSequence</c> are dropped as snapshot-covered; the client
///         resumes normal sequence-dedup from there.
///     </para>
/// </remarks>
public sealed class ReconnectableRpcClient : IAsyncDisposable
{
    private const double BaseDelayMs = 500;
    private const double MaxDelayMs = 30_000;

    private readonly object _lock = new();
    private readonly Func<CancellationToken, Task<IIpcClientTransport>> _transportFactory;
    private readonly ILogger _logger;
    private readonly string? _psk;
    private readonly Random _jitter = Random.Shared;
    private int _disposed;

    private MessagePackRpcClient? _current;
    private IIpcClientTransport? _currentTransport;
    private ulong _lastSeen;
    private bool _hasSeen;
    private int _attempt;

    /// <summary>Raised every time a new underlying connection is up (tests/diagnostics).</summary>
    public event EventHandler? Connected = delegate { };

    public ReconnectableRpcClient(
        Func<CancellationToken, Task<IIpcClientTransport>> transportFactory,
        ILogger logger,
        string? psk = null)
    {
        _transportFactory = transportFactory;
        _logger = logger;
        _psk = psk;
    }

    /// <summary>Backoff for the Nth consecutive failed attempt (1-based), with jitter.</summary>
    public TimeSpan NextBackoffDelay()
    {
        lock (_lock)
        {
            int shift = Math.Min(_attempt, 7);
            double baseDelay = Math.Min(BaseDelayMs * Math.Pow(2, shift), MaxDelayMs);
            double jittered = baseDelay * (0.8 + 0.4 * _jitter.NextDouble());
            _attempt++;
            return TimeSpan.FromMilliseconds(jittered);
        }
    }

    /// <summary>Reset the backoff ladder after a successful exchange.</summary>
    private void NoteSuccess()
    {
        lock (_lock) { _attempt = 0; }
    }

    /// <summary>Dial once and expose the inner client for request/response calls.</summary>
    public async Task<MessagePackRpcClient> ConnectAsync(CancellationToken ct = default)
    {
        var (inner, _) = await DialAsync(ct).ConfigureAwait(false);
        return inner;
    }

    /// <summary>Send a request over the current connection (dialing if needed).</summary>
    public async Task<HarborResponse> SendAsync(HarborRequest request, CancellationToken ct = default)
    {
        var (inner, _) = await DialAsync(ct).ConfigureAwait(false);
        return await inner.SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     The reconnecting event stream. Enumerated frames are exactly-once
    ///     per server sequence (duplicates dropped), ordered, and survive
    ///     arbitrary connection loss. <paramref name="loadSnapshotAsync"/>
    ///     is invoked only after a subscription exists AND the server asked
    ///     for a resync (or on first subscribe).
    /// </summary>
    public async IAsyncEnumerable<EventFrame> SubscribeWithReconnectAsync(
        Func<CancellationToken, Task> loadSnapshotAsync,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ulong resyncBaseline = 0;
        bool holdingResyncBaseline = false;

        while (!ct.IsCancellationRequested)
        {
            MessagePackRpcClient inner;
            try
            {
                (inner, _) = await DialAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await DelayBackoffAsync(ct).ConfigureAwait(false);
                continue;
            }

            SubscribeToEventsRequest subscribeRequest = _lastSeen > 0
                ? new SubscribeToEventsRequest(_lastSeen)
                : new SubscribeToEventsRequest();

            HarborResponse response;
            try
            {
                response = await inner.SendAsync(subscribeRequest, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Subscribe failed; reconnecting");
                await DropConnectionAsync().ConfigureAwait(false);
                await DelayBackoffAsync(ct).ConfigureAwait(false);
                continue;
            }

            NoteSuccess();
            if (response is not OkResponse ok || ok.Payload is null)
            {
                // Protocol-level refusal (PSK gate, unknown request…) — not
                // transient; fail loudly instead of spinning.
                string message = response is ErrorResponse err ? err.Message : "empty subscribe ack";
                throw new InvalidOperationException($"Event subscription rejected: {message}");
            }

            var ack = WireCodec.DeserializeDomain<SubscriptionAck>(ok.Payload, ct)!;

            // Subscribe is ALREADY established here: anything published during
            // the snapshot fetch is buffered server-side / queued in-band —
            // zero loss window (subscribe-before-snapshot, TZ A1).
            //
            // Baseline hold afterwards drops frames ≤ ServerSequence: they are
            // covered by the freshly loaded snapshot, so re-applying them
            // would duplicate state.
            if (!_hasSeen || ack.ResyncRequired)
            {
                await loadSnapshotAsync(ct).ConfigureAwait(false);
                resyncBaseline = ack.ServerSequence;
                holdingResyncBaseline = true;
            }

            // Pump until the connection dies, then loop around and re-subscribe.
            // (No yield inside try/catch — C# iterators forbid it — so reads go
            // through a failure-reporting helper instead.)
            bool died = false;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var lost = RegisterLost(inner, () => linked.Cancel());
            try
            {
                while (!linked.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    (bool frameOk, bool connDied, EventFrame frame) =
                        await ReadOneFrameAsync(inner.EventFrames, linked.Token).ConfigureAwait(false);
                    if (!frameOk)
                    {
                        died = connDied;
                        if (died) break;
                        continue;
                    }

                    if (holdingResyncBaseline)
                    {
                        if (frame.Sequence <= resyncBaseline) continue; // covered by the snapshot
                        holdingResyncBaseline = false;
                    }
                    else if (_hasSeen && frame.Sequence <= _lastSeen)
                    {
                        continue; // duplicate (dedup by monotonic sequence)
                    }

                    _lastSeen = frame.Sequence;
                    _hasSeen = true;
                    yield return frame;
                }
            }
            finally
            {
                lost.Dispose();
            }

            if (ct.IsCancellationRequested) break;
            if (died)
            {
                await DropConnectionAsync().ConfigureAwait(false);
                await DelayBackoffAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await DropConnectionAsync().ConfigureAwait(false);
    }

    // ── Plumbing ───────────────────────────────────────────────────────────

    /// <summary>Read one frame; reports connection death instead of throwing.</summary>
    private static async Task<(bool Ok, bool ConnectionDied, EventFrame Frame)> ReadOneFrameAsync(
        ChannelReader<EventFrame> reader, CancellationToken ct)
    {
        try
        {
            EventFrame frame = await reader.ReadAsync(ct).ConfigureAwait(false);
            return (true, false, frame);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (false, true, default);
        }
        catch (Exception)
        {
            return (false, true, default);
        }
    }

    private async Task<(MessagePackRpcClient Inner, IIpcClientTransport Transport)> DialAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_current is not null && _currentTransport is not null)
            {
                return (_current, _currentTransport);
            }
        }

        IIpcClientTransport transport = await _transportFactory(ct).ConfigureAwait(false);
        var inner = new MessagePackRpcClient(transport, _logger, _psk);
        try
        {
            await inner.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        lock (_lock)
        {
            _current = inner;
            _currentTransport = transport;
        }

        Connected?.Invoke(this, EventArgs.Empty);
        return (inner, transport);
    }

    private async Task DropConnectionAsync()
    {
        MessagePackRpcClient? inner;
        IIpcClientTransport? transport;
        lock (_lock)
        {
            inner = _current;
            transport = _currentTransport;
            _current = null;
            _currentTransport = null;
        }

        if (inner is not null) await inner.DisposeAsync().ConfigureAwait(false);
        if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);
    }

    private async Task DelayBackoffAsync(CancellationToken ct)
    {
        TimeSpan delay = NextBackoffDelay();
        _logger.LogInformation("Reconnecting in {Delay}ms", delay.TotalMilliseconds);
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce)
        {
            // Shutdown while waiting — the caller's loop checks ct next.
            _ = oce;
        }
    }

    /// <summary>Test hook: sever the current underlying connection (network-cut simulation).</summary>
    public async Task CutCurrentConnectionForTestAsync()
    {
        // Disposes the inner client and transport AND clears the cached
        // generation, so the next DialAsync opens a genuinely fresh socket.
        // The disposed inner's read loop dies → the pump treats this
        // generation as dead → backoff → re-dial → re-subscribe.
        await DropConnectionAsync().ConfigureAwait(false);
    }

    private static IDisposable RegisterLost(MessagePackRpcClient inner, Action onLost)
        => new LostSubscription(inner, onLost);

    private sealed class LostSubscription : IDisposable
    {
        private readonly MessagePackRpcClient _inner;
        private readonly Action _onLost;

        public LostSubscription(MessagePackRpcClient inner, Action onLost)
        {
            _inner = inner;
            _onLost = onLost;
            _inner.ConnectionLost += Handler;
        }

        private void Handler(object? sender, EventArgs e) => _onLost();

        public void Dispose() => _inner.ConnectionLost -= Handler;
    }
}
