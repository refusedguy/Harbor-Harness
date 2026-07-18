namespace Harbor.Ipc;

/// <summary>
///     Out-of-process <see cref="IHarborClient" /> implementation. Talks to
///     a remote <c>HarborIpcServer</c> via MessagePack-over-pipe (Windows)
///     or MessagePack-over-Unix-domain-socket (Linux/Mac).
/// </summary>
/// <remarks>
///     <para>
///         This is the implementation used when <c>HARBOR_MODE=ipc-client</c>.
///         The UI layer gets the exact same <see cref="IHarborClient" />
///         surface as the in-process variant — switching modes is a
///         one-line DI change.
///     </para>
///     <para>
///         <b>Architecture:</b> this client never instantiates
///         <c>IAgent</c>, <c>ISessionStore</c>, <c>IProviderRegistry</c>,
///         <c>IToolRegistry</c>, or <c>IEventBus</c>. It only knows the wire
///         protocol. That means a UI process can be ~5 MB instead of
///         ~150 MB — the entire Harbor.Application / Harbor.Registries /
///         Harbor.Tools.Builtin / Harbor.Providers.* graph stays in the
///         server process.
///     </para>
/// </remarks>
public sealed class IpcHarborClient : IHarborClient
{
    private readonly EventSubscription _eventSubscription;
    private readonly ILogger<IpcHarborClient> _logger;
    private readonly MessagePackRpcClient _rpc;
    private readonly ClientPipeTransport _transport;
    private int _disposed;
    private int _connected;

    /// <summary>
    ///     Construct an IPC client targeting the given pipe / socket.
    /// </summary>
    /// <param name="pipeName">Pipe name (Windows) or socket basename (Unix).</param>
    /// <param name="logger">Logger.</param>
    public IpcHarborClient(string pipeName, ILogger<IpcHarborClient> logger)
    {
        _logger = logger;
        _transport = new ClientPipeTransport(pipeName, logger);
        _rpc = new MessagePackRpcClient(_transport, logger);
        _eventSubscription = new EventSubscription(_rpc);
    }

    /// <summary>
    ///     Construct an IPC client with a pre-built transport (advanced — for tests).
    /// </summary>
    public IpcHarborClient(ClientPipeTransport transport, ILogger<IpcHarborClient> logger)
    {
        _logger = logger;
        _transport = transport;
        _rpc = new MessagePackRpcClient(_transport, logger);
        _eventSubscription = new EventSubscription(_rpc);
    }

    /// <inheritdoc />
    public bool IsConnected => Volatile.Read(ref _connected) == 1 && _transport.IsBound;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _connected, 1) == 1) return;
        await _rpc.ConnectAsync(ct).ConfigureAwait(false);
        // Send a ConnectRequest handshake so the server can confirm protocol version.
        await _rpc.SendAsync(new ConnectRequest(), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _connected, 0) == 0) return;
        try { await _rpc.SendAsync(new DisconnectRequest(), ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Disconnect handshake failed"); }
        await _transport.DisconnectAsync(ct).ConfigureAwait(false);
    }

    // ── Agent ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<Result> StartAgentAsync(string sessionId, string agentName, CancellationToken ct = default)
    {
        var resp = await _rpc.SendAsync(new StartAgentRequest(sessionId, agentName), ct).ConfigureAwait(false);
        return resp is ErrorResponse err
            ? Result.Failure(err.Message)
            : Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> AbortAgentAsync(CancellationToken ct = default)
    {
        var resp = await _rpc.SendAsync(new AbortAgentRequest(), ct).ConfigureAwait(false);
        return resp is ErrorResponse err
            ? Result.Failure(err.Message)
            : Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> SendPromptAsync(string prompt, CancellationToken ct = default)
    {
        var resp = await _rpc.SendAsync(new SendPromptRequest(prompt), ct).ConfigureAwait(false);
        return resp is ErrorResponse err
            ? Result.Failure(err.Message)
            : Result.Success();
    }

    // ── Sessions ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<Result<Session>> CreateSessionAsync(
        string dir, string agent, string provider, string model, CancellationToken ct = default)
    {
        var resp = await _rpc.SendAsync(new CreateSessionRequest(dir, agent, provider, model), ct).ConfigureAwait(false);
        return resp is ErrorResponse err
            ? Result.Failure<Session>(err.Message)
            : Result.Success(WireCodec.DeserializeDomain<Session>(resp.AsOk().Payload, ct)!);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<Session>>> ListSessionsAsync(CancellationToken ct = default)
    {
        var resp = await _rpc.SendAsync(new ListSessionsRequest(), ct).ConfigureAwait(false);
        return resp is ErrorResponse err
            ? Result.Failure<IReadOnlyList<Session>>(err.Message)
            : Result.Success(
                WireCodec.DeserializeDomain<IReadOnlyList<Session>>(resp.AsOk().Payload, ct)
                ?? Array.Empty<Session>());
    }

    /// <inheritdoc />
    public async Task<Result<Session>> GetSessionAsync(string sessionId, CancellationToken ct = default)
    {
        var resp = await _rpc.SendAsync(new GetSessionRequest(sessionId), ct).ConfigureAwait(false);
        return resp is ErrorResponse err
            ? Result.Failure<Session>(err.Message)
            : Result.Success(WireCodec.DeserializeDomain<Session>(resp.AsOk().Payload, ct)!);
    }

    /// <inheritdoc />
    public async Task<Result> DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        var resp = await _rpc.SendAsync(new DeleteSessionRequest(sessionId), ct).ConfigureAwait(false);
        return resp is ErrorResponse err
            ? Result.Failure(err.Message)
            : Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(
        string sessionId, CancellationToken ct = default)
    {
        var resp = await _rpc.SendAsync(new GetMessagesRequest(sessionId), ct).ConfigureAwait(false);
        return resp is ErrorResponse err
            ? Result.Failure<IReadOnlyList<AgentMessage>>(err.Message)
            : Result.Success(
                WireCodec.DeserializeDomain<IReadOnlyList<AgentMessage>>(resp.AsOk().Payload, ct)
                ?? Array.Empty<AgentMessage>());
    }

    // ── Providers ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ProviderId>>> ListProvidersAsync(CancellationToken ct = default)
    {
        var resp = await _rpc.SendAsync(new ListProvidersRequest(), ct).ConfigureAwait(false);
        return resp is ErrorResponse err
            ? Result.Failure<IReadOnlyList<ProviderId>>(err.Message)
            : Result.Success(
                WireCodec.DeserializeDomain<IReadOnlyList<ProviderId>>(resp.AsOk().Payload, ct)
                ?? Array.Empty<ProviderId>());
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ModelInfo>>> ListModelsAsync(
        string? providerId = null, CancellationToken ct = default)
    {
        var resp = await _rpc.SendAsync(new ListModelsRequest(providerId), ct).ConfigureAwait(false);
        return resp is ErrorResponse err
            ? Result.Failure<IReadOnlyList<ModelInfo>>(err.Message)
            : Result.Success(
                WireCodec.DeserializeDomain<IReadOnlyList<ModelInfo>>(resp.AsOk().Payload, ct)
                ?? Array.Empty<ModelInfo>());
    }

    // ── Tools ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ToolDescriptor>>> ListToolsAsync(CancellationToken ct = default)
    {
        var resp = await _rpc.SendAsync(new ListToolsRequest(), ct).ConfigureAwait(false);
        return resp is ErrorResponse err
            ? Result.Failure<IReadOnlyList<ToolDescriptor>>(err.Message)
            : Result.Success(
                WireCodec.DeserializeDomain<IReadOnlyList<ToolDescriptor>>(resp.AsOk().Payload, ct)
                ?? Array.Empty<ToolDescriptor>());
    }

    // ── Streaming events ───────────────────────────────────────────────────

    /// <inheritdoc />
    public async IAsyncEnumerable<HarborEvent> SubscribeToEventsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Tell the server to start pushing events for this client.
        var resp = await _rpc.SendAsync(new SubscribeToEventsRequest(), ct).ConfigureAwait(false);
        if (resp is ErrorResponse err)
        {
            _logger.LogWarning("Event subscription rejected: {Error}", err.Message);
            yield break;
        }

        await foreach (var evt in _eventSubscription.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _rpc.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
///     Helpers for casting <see cref="HarborResponse" /> to its concrete
///     <see cref="OkResponse" /> shape. Throws if the response is an error —
///     callers should check via <c>is ErrorResponse</c> first.
/// </summary>
internal static class HarborResponseExtensions
{
    /// <summary>Cast to <see cref="OkResponse" />, throwing if it's not.</summary>
    public static OkResponse AsOk(this HarborResponse response)
        => response as OkResponse
            ?? throw new InvalidOperationException(
                $"Expected OkResponse, got {response.GetType().Name}");
}
