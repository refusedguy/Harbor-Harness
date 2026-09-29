namespace Harbor.Ipc.Protocol;

/// <summary>
///     Server-side dispatcher: takes a <see cref="HarborRequest" /> and
///     produces a <see cref="HarborResponse" /> by handing it to the
///     <see cref="IRequestHandler{TRequest}" /> registered for that exact
///     request type.
/// </summary>
/// <remarks>
///     <para>
///         <b>No switch, and no service location.</b> The 14-arm
///         <c>switch</c> this class used to hold (#485) made the protocol
///         surface one method in one already-large class: adding a request type
///         meant editing the switch AND adding a private method beside it, and
///         the <c>_ =&gt;</c> arm answered anything the author forgot with
///         <c>ErrorResponse { Message = "Unknown request type: X" }</c> — a
///         renamed or mistyped request compiled, shipped, and failed at the
///         client as an opaque string. Dispatch is now a dictionary lookup
///         against <see cref="RequestHandlerRegistry" />, which is composed
///         from explicitly-injected singletons (#63: no runtime service
///         location — every dependency arrives through the registry's
///         <c>CreateDefault</c>).
///     </para>
///     <para>
///         <b>Stateless per request:</b> the registry's handlers are ctor-injected
///         singletons shared across calls. The <see cref="IAgent" /> is a
///         singleton (single-flight runner), so concurrent
///         <see cref="SendPromptRequest" />s will get the agent's "already
///         running" failure rather than clobber each other.
///     </para>
///     <para>
///         <b>Two boundaries, both loud:</b> an unregistered request type is
///         rejected when the dispatcher is CONSTRUCTED (see the ctor), and a
///         request that somehow reaches dispatch with no handler is logged,
///         counted, and answered explicitly. Neither is silence.
///     </para>
/// </remarks>
public sealed class RequestDispatcher
{
    private readonly RequestHandlerRegistry _handlers;
    private readonly SessionLeaseRegistry _leases;
    private readonly ILogger _logger;
    private long _unhandledRequests;

    /// <summary>
    ///     Construct a dispatcher over an explicit handler registry.
    /// </summary>
    /// <param name="handlers">The request-type → handler table.</param>
    /// <param name="leases">
    ///     Session-lease registry. Kept here (rather than inside the
    ///     <see cref="StartAgentRequest" /> handler) because the RPC server
    ///     releases a connection's leases through this object on teardown.
    /// </param>
    /// <param name="logger">Logger for the unhandled-request boundary.</param>
    /// <exception cref="InvalidOperationException">
    ///     <b>The startup boundary.</b> At least one member of the
    ///     <see cref="HarborRequest" /> union has neither a handler here nor an
    ///     owner upstream in <see cref="HarborRequestTypes.HandledBeforeDispatch" />.
    ///     A server that would answer such a request with a string must not
    ///     start — the omission is a composition error, reported here, with the
    ///     missing types named, instead of an opaque runtime failure.
    /// </exception>
    public RequestDispatcher(
        RequestHandlerRegistry handlers,
        SessionLeaseRegistry leases,
        ILogger<RequestDispatcher> logger)
    {
        IReadOnlyList<Type> unhandled = handlers.UnhandledRequestTypes();
        if (unhandled.Count > 0)
        {
            throw new InvalidOperationException(
                $"The IPC request handler registry is missing "
                + $"{unhandled.Count} of the {HarborRequestTypes.All.Count} "
                + $"{nameof(HarborRequest)} members: "
                + string.Join(", ", unhandled.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal))
                + ". Add a handler for each and one line to "
                + "RequestHandlerRegistry.CreateDefault, or — if the type is answered before "
                + "dispatch, the way PskAuthRequest is at the PSK gate — name it in "
                + "HarborRequestTypes.HandledBeforeDispatch with a comment saying so.");
        }

        _handlers = handlers;
        _leases = leases;
        _logger = logger;
    }

    /// <summary>
    ///     Requests that reached dispatch with no registered handler, since
    ///     construction. Zero in any healthy server: the constructor already
    ///     refused to start one that could not answer the whole union. The
    ///     counter exists so this boundary is observable rather than a silent
    ///     branch, per docs/PATTERNS.md §7 rule 2.
    /// </summary>
    public long UnhandledRequestCount => Interlocked.Read(ref _unhandledRequests);

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
    /// <param name="clientId">
    ///     The calling connection's id — required for session-lease
    ///     acquisition (StartAgent) and addressed subscription.
    /// </param>
    public async Task<HarborResponse> DispatchAsync(
        HarborRequest request,
        Stream? replyStream,
        SemaphoreSlim? replyWriteLock,
        CancellationToken ct = default,
        string? clientId = null)
    {
        try
        {
            if (!_handlers.TryGetEntry(request.GetType(), out RequestHandlerEntry entry))
            {
                return Unhandled(request);
            }

            var context = new RequestContext(clientId, replyStream, replyWriteLock);
            return await entry.Invoke(request, context, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ErrorResponse { RequestId = request.RequestId, Message = ex.Message };
        }
    }

    /// <summary>Release all session leases owned by this connection (teardown).</summary>
    public void ReleaseClientLeases(string clientId) => _leases.ReleaseAll(clientId);

    /// <summary>The owner of a leased session (diagnostics/tests).</summary>
    public string? GetLeaseOwner(string sessionId) => _leases.GetOwner(sessionId);

    /// <summary>
    ///     The wire boundary, for the one case the constructor cannot rule
    ///     out: a request type that is not part of the tagged union at all.
    ///     The old code answered it with the bare string "Unknown request type:
    ///     X" and moved on, which is indistinguishable from a request the
    ///     server answered wrongly. This one names the type, the machine-
    ///     parsable prefix, the reason, and where to fix it.
    /// </summary>
    private ErrorResponse Unhandled(HarborRequest request)
    {
        Interlocked.Increment(ref _unhandledRequests);

        string requestType = request.GetType().Name;
        _logger.LogError(
            "No IPC request handler is registered for {RequestType}. It is not a member of the "
            + "tagged {HarborRequest} union, so no handler can be registered for it; the server "
            + "declined it rather than answering with a placeholder. This should be unreachable — "
            + "if it fires, the wire union and HarborRequestTypes.All have drifted apart.",
            requestType,
            nameof(HarborRequest));

        return new ErrorResponse
        {
            RequestId = request.RequestId,
            Message = $"NO_HANDLER:{requestType}: this request type is not part of the Harbor IPC "
                      + "protocol union, so this server cannot serve it. Upgrade the server, or use a "
                      + "request type the server implements (see HarborRequestTypes.All for the union)."
        };
    }
}
