using Harbor.Abstractions.Permissions;

namespace Harbor.Ipc.Protocol;

// ── Agent ─────────────────────────────────────────────────────────────────────

/// <summary>Handles <see cref="StartAgentRequest" />: resolve the agent, take the lease, bind it.</summary>
internal sealed class StartAgentRequestHandler(
    IAgent agent,
    IAgentRegistry agents,
    ISessionStore sessions,
    SessionLeaseRegistry leases) : IRequestHandler<StartAgentRequest>
{
    public async Task<HarborResponse> HandleAsync(StartAgentRequest request, RequestContext context, CancellationToken ct)
    {
        // ROP boundary #101: shared TryCreate → GetAgent preamble (same as InProcessHarborClient).
        var agentDefResult = agents.ResolveAgent(request.AgentName);
        if (agentDefResult.IsFailure)
            return new ErrorResponse { RequestId = request.RequestId, Message = agentDefResult.Error };

        var sessionResult = await sessions.GetAsync(request.SessionId, ct).ConfigureAwait(false);
        if (sessionResult.IsFailure)
            return new ErrorResponse { RequestId = request.RequestId, Message = sessionResult.Error };

        // A3: the second client may not re-initialize the agent mid-run —
        // an owned session refuses with a structured, machine-parsable error.
        if (!string.IsNullOrEmpty(context.ClientId) && !leases.TryAcquire(request.SessionId, context.ClientId!))
        {
            return new ErrorResponse
            {
                RequestId = request.RequestId,
                Message = $"SESSION_BUSY:{request.SessionId}:owner={leases.GetOwner(request.SessionId)}"
            };
        }

        agent.Initialize(sessionResult.Value, agentDefResult.Value);
        return new OkResponse { RequestId = request.RequestId };
    }
}

/// <summary>Handles <see cref="AbortAgentRequest" /> through the single cancellation ingress.</summary>
internal sealed class AbortAgentRequestHandler(
    IAgent agent,
    IApprovalCoordinator? coordinator) : IRequestHandler<AbortAgentRequest>
{
    public Task<HarborResponse> HandleAsync(AbortAgentRequest request, RequestContext context, CancellationToken ct)
    {
        // #49 PR1: single cancellation ingress (null = minimal host without
        // the coordinator; direct cancel as before).
        if (coordinator is not null)
        {
            coordinator.RequestCancel(agent);
        }
        else
        {
            agent.RequestAbort();
        }

        return Task.FromResult<HarborResponse>(new OkResponse { RequestId = request.RequestId });
    }
}

/// <summary>Handles <see cref="SendPromptRequest" />: run the agent loop to completion.</summary>
internal sealed class SendPromptRequestHandler(IAgent agent) : IRequestHandler<SendPromptRequest>
{
    public async Task<HarborResponse> HandleAsync(SendPromptRequest request, RequestContext context, CancellationToken ct)
    {
        var result = await agent.PromptAsync(request.Prompt, ct).ConfigureAwait(false);
        return result.IsSuccess
            ? new OkResponse { RequestId = request.RequestId }
            : new ErrorResponse { RequestId = request.RequestId, Message = result.Error };
    }
}

// ── Sessions ──────────────────────────────────────────────────────────────────

/// <summary>Handles <see cref="CreateSessionRequest" />.</summary>
internal sealed class CreateSessionRequestHandler(ISessionStore sessions) : IRequestHandler<CreateSessionRequest>
{
    public async Task<HarborResponse> HandleAsync(CreateSessionRequest request, RequestContext context, CancellationToken ct)
    {
        var result = await sessions
            .CreateAsync(request.Directory, request.Agent, request.Provider, request.Model, ct)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? new OkResponse { RequestId = request.RequestId, Payload = WireCodec.SerializeDomain(result.Value, ct) }
            : new ErrorResponse { RequestId = request.RequestId, Message = result.Error };
    }
}

/// <summary>Handles <see cref="ListSessionsRequest" />.</summary>
internal sealed class ListSessionsRequestHandler(ISessionStore sessions) : IRequestHandler<ListSessionsRequest>
{
    public async Task<HarborResponse> HandleAsync(ListSessionsRequest request, RequestContext context, CancellationToken ct)
    {
        var result = await sessions.ListAsync(null, ct).ConfigureAwait(false);
        return result.IsSuccess
            ? new OkResponse { RequestId = request.RequestId, Payload = WireCodec.SerializeDomain(result.Value, ct) }
            : new ErrorResponse { RequestId = request.RequestId, Message = result.Error };
    }
}

/// <summary>Handles <see cref="GetSessionRequest" />.</summary>
internal sealed class GetSessionRequestHandler(ISessionStore sessions) : IRequestHandler<GetSessionRequest>
{
    public async Task<HarborResponse> HandleAsync(GetSessionRequest request, RequestContext context, CancellationToken ct)
    {
        var result = await sessions.GetAsync(request.SessionId, ct).ConfigureAwait(false);
        return result.IsSuccess
            ? new OkResponse { RequestId = request.RequestId, Payload = WireCodec.SerializeDomain(result.Value, ct) }
            : new ErrorResponse { RequestId = request.RequestId, Message = result.Error };
    }
}

/// <summary>Handles <see cref="DeleteSessionRequest" />.</summary>
internal sealed class DeleteSessionRequestHandler(ISessionStore sessions) : IRequestHandler<DeleteSessionRequest>
{
    public async Task<HarborResponse> HandleAsync(DeleteSessionRequest request, RequestContext context, CancellationToken ct)
    {
        var result = await sessions.DeleteAsync(request.SessionId, ct).ConfigureAwait(false);
        return result.IsSuccess
            ? new OkResponse { RequestId = request.RequestId }
            : new ErrorResponse { RequestId = request.RequestId, Message = result.Error };
    }
}

/// <summary>Handles <see cref="GetMessagesRequest" />.</summary>
internal sealed class GetMessagesRequestHandler(ISessionStore sessions) : IRequestHandler<GetMessagesRequest>
{
    public async Task<HarborResponse> HandleAsync(GetMessagesRequest request, RequestContext context, CancellationToken ct)
    {
        var result = await sessions.GetMessagesAsync(request.SessionId, ct).ConfigureAwait(false);
        return result.IsSuccess
            ? new OkResponse { RequestId = request.RequestId, Payload = WireCodec.SerializeDomain(result.Value, ct) }
            : new ErrorResponse { RequestId = request.RequestId, Message = result.Error };
    }
}

// ── Providers ─────────────────────────────────────────────────────────────────

/// <summary>Handles <see cref="ListProvidersRequest" />.</summary>
internal sealed class ListProvidersRequestHandler(IProviderRegistry providers) : IRequestHandler<ListProvidersRequest>
{
    public Task<HarborResponse> HandleAsync(ListProvidersRequest request, RequestContext context, CancellationToken ct)
    {
        var ids = providers.GetRegisteredProviderIds();
        return Task.FromResult<HarborResponse>(
            new OkResponse { RequestId = request.RequestId, Payload = WireCodec.SerializeDomain(ids, ct) });
    }
}

/// <summary>Handles <see cref="ListModelsRequest" />, optionally filtered by provider.</summary>
internal sealed class ListModelsRequestHandler(IProviderRegistry providers) : IRequestHandler<ListModelsRequest>
{
    public async Task<HarborResponse> HandleAsync(ListModelsRequest request, RequestContext context, CancellationToken ct)
    {
        Result<IReadOnlyList<ModelInfo>> result;
        if (string.IsNullOrEmpty(request.ProviderId))
        {
            result = await providers.GetAllModelsAsync(ct).ConfigureAwait(false);
        }
        else
        {
            // ROP boundary #101: shared TryCreate → GetClient preamble (same as InProcessHarborClient).
            var clientResult = providers.ResolveClient(request.ProviderId!);
            if (clientResult.IsFailure)
                return new ErrorResponse { RequestId = request.RequestId, Message = clientResult.Error };

            result = await clientResult.Value.GetModelsAsync(ct).ConfigureAwait(false);
        }

        return result.IsSuccess
            ? new OkResponse { RequestId = request.RequestId, Payload = WireCodec.SerializeDomain(result.Value, ct) }
            : new ErrorResponse { RequestId = request.RequestId, Message = result.Error };
    }
}

// ── Tools ─────────────────────────────────────────────────────────────────────

/// <summary>Handles <see cref="ListToolsRequest" />.</summary>
internal sealed class ListToolsRequestHandler(IToolRegistry tools) : IRequestHandler<ListToolsRequest>
{
    public Task<HarborResponse> HandleAsync(ListToolsRequest request, RequestContext context, CancellationToken ct)
    {
        var list = tools.GetAllTools();
        return Task.FromResult<HarborResponse>(
            new OkResponse { RequestId = request.RequestId, Payload = WireCodec.SerializeDomain(list, ct) });
    }
}

// ── Streaming events ──────────────────────────────────────────────────────────

/// <summary>
///     Handles <see cref="SubscribeToEventsRequest" />: the one request that
///     needs <see cref="RequestContext.ReplyStream" /> and
///     <see cref="RequestContext.ReplyWriteLock" />, because its ack is
///     followed by out-of-band <see cref="EventEnvelope" /> frames on the same
///     stream.
/// </summary>
internal sealed class SubscribeToEventsRequestHandler(EventBroadcaster broadcaster) : IRequestHandler<SubscribeToEventsRequest>
{
    public async Task<HarborResponse> HandleAsync(
        SubscribeToEventsRequest request,
        RequestContext context,
        CancellationToken ct)
    {
        if (context.ReplyStream is null || context.ReplyWriteLock is null)
        {
            return new ErrorResponse { RequestId = request.RequestId, Message = "Cannot subscribe: no reply stream / write lock" };
        }

        EventBroadcaster.SubscriptionAckData ack = await broadcaster
            .RegisterAsync(context.ReplyStream, context.ReplyWriteLock, request.LastSequence, context.ClientId ?? "anonymous")
            .ConfigureAwait(false);

        return new OkResponse
        {
            RequestId = request.RequestId,
            Payload = WireCodec.SerializeDomain(
                new SubscriptionAck { ServerSequence = ack.ServerSequence, ResyncRequired = ack.ResyncRequired })
        };
    }
}

// ── Handshake ─────────────────────────────────────────────────────────────────

/// <summary>Handles <see cref="ConnectRequest" /> — the ack only; the transport owns the connection.</summary>
internal sealed class ConnectRequestHandler : IRequestHandler<ConnectRequest>
{
    public Task<HarborResponse> HandleAsync(ConnectRequest request, RequestContext context, CancellationToken ct)
        => Task.FromResult<HarborResponse>(new OkResponse { RequestId = request.RequestId });
}

/// <summary>Handles <see cref="DisconnectRequest" /> — the ack only; teardown is the RPC server's.</summary>
internal sealed class DisconnectRequestHandler : IRequestHandler<DisconnectRequest>
{
    public Task<HarborResponse> HandleAsync(DisconnectRequest request, RequestContext context, CancellationToken ct)
        => Task.FromResult<HarborResponse>(new OkResponse { RequestId = request.RequestId });
}
