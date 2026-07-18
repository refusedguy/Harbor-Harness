namespace Harbor.Ipc.Protocol;

/// <summary>
///     Server-side dispatcher: takes a <see cref="HarborRequest" /> and
///     produces a <see cref="HarborResponse" /> by calling the in-process
///     <c>IAgent</c>, <c>ISessionStore</c>, <c>IProviderRegistry</c>,
///     <c>IToolRegistry</c>, <c>IAgentRegistry</c> resolved from the host's
///     <see cref="IServiceProvider" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Stateless per request:</b> every call resolves its services
///         afresh from the host's DI container. The
///         <see cref="IAgent" /> is a singleton (single-flight runner), so
///         concurrent <see cref="SendPromptRequest" />s will get the agent's
///         "already running" failure rather than clobber each other.
///     </para>
///     <para>
///         <b>Event subscription:</b> a <see cref="SubscribeToEventsRequest" />
///         returns an <see cref="OkResponse" /> ack immediately and registers
///         the calling client's stream-writer with the
///         <see cref="EventBroadcaster" />. Subsequent
///         <see cref="HarborEvent" />s are pushed out-of-band via
///         <see cref="EventEnvelope" /> frames.
///     </para>
/// </remarks>
public sealed class RequestDispatcher
{
    private readonly EventBroadcaster _broadcaster;
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    ///     Construct a dispatcher backed by the host's service provider.
    /// </summary>
    public RequestDispatcher(IServiceProvider serviceProvider, EventBroadcaster broadcaster)
    {
        _serviceProvider = serviceProvider;
        _broadcaster = broadcaster;
    }

    /// <summary>
    ///     Dispatch a single request and produce a response.
    /// </summary>
    /// <param name="request">The incoming request.</param>
    /// <param name="replyStream">
    ///     The client's reply stream — only needed for
    ///     <see cref="SubscribeToEventsRequest" /> so the broadcaster can
    ///     push out-of-band frames to this client.
    /// </param>
    /// <param name="replyWriteLock">
    ///     The per-client write lock that serializes all writes to
    ///     <paramref name="replyStream" />. The broadcaster needs this so
    ///     its pushed <see cref="EventEnvelope" /> frames don't interleave
    ///     with the dispatcher's response frames on the same stream.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<HarborResponse> DispatchAsync(
        HarborRequest request,
        Stream? replyStream,
        SemaphoreSlim? replyWriteLock,
        CancellationToken ct = default)
    {
        try
        {
            return request switch
            {
                StartAgentRequest r => await HandleStartAgentAsync(r, ct).ConfigureAwait(false),
                AbortAgentRequest r => HandleAbortAgent(r),
                SendPromptRequest r => await HandleSendPromptAsync(r, ct).ConfigureAwait(false),
                CreateSessionRequest r => await HandleCreateSessionAsync(r, ct).ConfigureAwait(false),
                ListSessionsRequest r => await HandleListSessionsAsync(r, ct).ConfigureAwait(false),
                GetSessionRequest r => await HandleGetSessionAsync(r, ct).ConfigureAwait(false),
                DeleteSessionRequest r => await HandleDeleteSessionAsync(r, ct).ConfigureAwait(false),
                GetMessagesRequest r => await HandleGetMessagesAsync(r, ct).ConfigureAwait(false),
                ListProvidersRequest r => await HandleListProvidersAsync(r, ct).ConfigureAwait(false),
                ListModelsRequest r => await HandleListModelsAsync(r, ct).ConfigureAwait(false),
                ListToolsRequest r => await HandleListToolsAsync(r, ct).ConfigureAwait(false),
                SubscribeToEventsRequest r => HandleSubscribeToEvents(r, replyStream, replyWriteLock),
                ConnectRequest r => new OkResponse { RequestId = r.RequestId },
                DisconnectRequest r => new OkResponse { RequestId = r.RequestId },
                _ => new ErrorResponse { RequestId = request.RequestId, Message = $"Unknown request type: {request.GetType().Name}" }
            };
        }
        catch (Exception ex)
        {
            return new ErrorResponse { RequestId = request.RequestId, Message = ex.Message };
        }
    }

    // ── Agent ──────────────────────────────────────────────────────────────

    private async Task<HarborResponse> HandleStartAgentAsync(StartAgentRequest r, CancellationToken ct)
    {
        var agent = _serviceProvider.GetRequiredService<IAgent>();
        var agents = _serviceProvider.GetRequiredService<IAgentRegistry>();
        var sessions = _serviceProvider.GetRequiredService<ISessionStore>();

        var nameResult = AgentName.TryCreate(r.AgentName);
        if (nameResult.IsFailure)
            return new ErrorResponse { RequestId = r.RequestId, Message = nameResult.Error };

        var agentDefResult = agents.GetAgent(nameResult.Value);
        if (agentDefResult.IsFailure)
            return new ErrorResponse { RequestId = r.RequestId, Message = agentDefResult.Error };

        var sessionResult = await sessions.GetAsync(r.SessionId, ct).ConfigureAwait(false);
        if (sessionResult.IsFailure)
            return new ErrorResponse { RequestId = r.RequestId, Message = sessionResult.Error };

        agent.Initialize(sessionResult.Value, agentDefResult.Value);
        return new OkResponse { RequestId = r.RequestId };
    }

    private HarborResponse HandleAbortAgent(AbortAgentRequest r)
    {
        var agent = _serviceProvider.GetRequiredService<IAgent>();
        agent.AbortSource.Cancel();
        return new OkResponse { RequestId = r.RequestId };
    }

    private async Task<HarborResponse> HandleSendPromptAsync(SendPromptRequest r, CancellationToken ct)
    {
        var agent = _serviceProvider.GetRequiredService<IAgent>();
        var result = await agent.PromptAsync(r.Prompt, ct).ConfigureAwait(false);
        return result.IsSuccess
            ? new OkResponse { RequestId = r.RequestId }
            : new ErrorResponse { RequestId = r.RequestId, Message = result.Error };
    }

    // ── Sessions ───────────────────────────────────────────────────────────

    private async Task<HarborResponse> HandleCreateSessionAsync(CreateSessionRequest r, CancellationToken ct)
    {
        var sessions = _serviceProvider.GetRequiredService<ISessionStore>();
        var result = await sessions.CreateAsync(r.Directory, r.Agent, r.Provider, r.Model, ct).ConfigureAwait(false);
        return result.IsSuccess
            ? new OkResponse { RequestId = r.RequestId, Payload = WireCodec.SerializeDomain(result.Value, ct) }
            : new ErrorResponse { RequestId = r.RequestId, Message = result.Error };
    }

    private async Task<HarborResponse> HandleListSessionsAsync(ListSessionsRequest r, CancellationToken ct)
    {
        var sessions = _serviceProvider.GetRequiredService<ISessionStore>();
        var result = await sessions.ListAsync(null, ct).ConfigureAwait(false);
        return result.IsSuccess
            ? new OkResponse { RequestId = r.RequestId, Payload = WireCodec.SerializeDomain(result.Value, ct) }
            : new ErrorResponse { RequestId = r.RequestId, Message = result.Error };
    }

    private async Task<HarborResponse> HandleGetSessionAsync(GetSessionRequest r, CancellationToken ct)
    {
        var sessions = _serviceProvider.GetRequiredService<ISessionStore>();
        var result = await sessions.GetAsync(r.SessionId, ct).ConfigureAwait(false);
        return result.IsSuccess
            ? new OkResponse { RequestId = r.RequestId, Payload = WireCodec.SerializeDomain(result.Value, ct) }
            : new ErrorResponse { RequestId = r.RequestId, Message = result.Error };
    }

    private async Task<HarborResponse> HandleDeleteSessionAsync(DeleteSessionRequest r, CancellationToken ct)
    {
        var sessions = _serviceProvider.GetRequiredService<ISessionStore>();
        var result = await sessions.DeleteAsync(r.SessionId, ct).ConfigureAwait(false);
        return result.IsSuccess
            ? new OkResponse { RequestId = r.RequestId }
            : new ErrorResponse { RequestId = r.RequestId, Message = result.Error };
    }

    private async Task<HarborResponse> HandleGetMessagesAsync(GetMessagesRequest r, CancellationToken ct)
    {
        var sessions = _serviceProvider.GetRequiredService<ISessionStore>();
        var result = await sessions.GetMessagesAsync(r.SessionId, ct).ConfigureAwait(false);
        return result.IsSuccess
            ? new OkResponse { RequestId = r.RequestId, Payload = WireCodec.SerializeDomain(result.Value, ct) }
            : new ErrorResponse { RequestId = r.RequestId, Message = result.Error };
    }

    // ── Providers ──────────────────────────────────────────────────────────

    private Task<HarborResponse> HandleListProvidersAsync(ListProvidersRequest r, CancellationToken ct)
    {
        var providers = _serviceProvider.GetRequiredService<IProviderRegistry>();
        var ids = providers.GetRegisteredProviderIds();
        return Task.FromResult<HarborResponse>(
            new OkResponse { RequestId = r.RequestId, Payload = WireCodec.SerializeDomain(ids, ct) });
    }

    private async Task<HarborResponse> HandleListModelsAsync(ListModelsRequest r, CancellationToken ct)
    {
        var providers = _serviceProvider.GetRequiredService<IProviderRegistry>();
        Result<IReadOnlyList<ModelInfo>> result;
        if (string.IsNullOrEmpty(r.ProviderId))
        {
            result = await providers.GetAllModelsAsync(ct).ConfigureAwait(false);
        }
        else
        {
            var pidResult = ProviderId.TryCreate(r.ProviderId!);
            if (pidResult.IsFailure)
                return new ErrorResponse { RequestId = r.RequestId, Message = pidResult.Error };

            var clientResult = providers.GetClient(pidResult.Value);
            if (clientResult.IsFailure)
                return new ErrorResponse { RequestId = r.RequestId, Message = clientResult.Error };

            result = await clientResult.Value.GetModelsAsync(ct).ConfigureAwait(false);
        }

        return result.IsSuccess
            ? new OkResponse { RequestId = r.RequestId, Payload = WireCodec.SerializeDomain(result.Value, ct) }
            : new ErrorResponse { RequestId = r.RequestId, Message = result.Error };
    }

    // ── Tools ──────────────────────────────────────────────────────────────

    private Task<HarborResponse> HandleListToolsAsync(ListToolsRequest r, CancellationToken ct)
    {
        var tools = _serviceProvider.GetRequiredService<IToolRegistry>();
        var list = tools.GetAllTools();
        return Task.FromResult<HarborResponse>(
            new OkResponse { RequestId = r.RequestId, Payload = WireCodec.SerializeDomain(list, ct) });
    }

    // ── Streaming events ───────────────────────────────────────────────────

    private HarborResponse HandleSubscribeToEvents(
        SubscribeToEventsRequest r,
        Stream? replyStream,
        SemaphoreSlim? replyWriteLock)
    {
        if (replyStream is null || replyWriteLock is null)
        {
            return new ErrorResponse { RequestId = r.RequestId, Message = "Cannot subscribe: no reply stream / write lock" };
        }

        _broadcaster.Register(replyStream, replyWriteLock);
        return new OkResponse { RequestId = r.RequestId };
    }
}
