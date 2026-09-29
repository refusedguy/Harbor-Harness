using System.Collections.Frozen;
using Harbor.Abstractions.Permissions;

namespace Harbor.Ipc.Protocol;

/// <summary>
///     One <see cref="HarborRequest" /> subtype bound to the handler that owns
///     it, plus a pre-built invoke delegate so dispatch never re-derives the
///     cast.
/// </summary>
/// <param name="RequestType">
///     The key. Always <c>typeof(TRequest)</c> of the handler it came from —
///     there is no way to file a handler under a type it does not implement.
/// </param>
/// <param name="Invoke">
///     Erases the type parameter once, here, at composition time. The cast it
///     performs is sound because the registry only reaches this entry by an
///     EXACT <c>request.GetType()</c> match, which makes
///     <c>(TRequest)request</c> the identity.
/// </param>
internal readonly record struct RequestHandlerEntry(
    Type RequestType,
    Func<HarborRequest, RequestContext, CancellationToken, Task<HarborResponse>> Invoke);

/// <summary>
///     The immutable request-type → handler table a
///     <see cref="RequestDispatcher" /> dispatches through.
/// </summary>
/// <remarks>
///     <para>
///         <b>One declaration of the protocol surface.</b>
///         <c>CreateDefault</c> below is the only place a handler is named, the
///         way <c>TuiBackendRegistry</c> is the only place a renderer backend is
///         named and <c>SessionStoreRegistry</c> the only place a store is. A
///         new request type is one handler class plus one line in that array —
///         and forgetting the line does not compile into a silent server, it
///         throws at startup, because <see cref="UnhandledRequestTypes" /> is
///         checked by the dispatcher's constructor against the reflection
///         census in <see cref="HarborRequestTypes" />.
///     </para>
///     <para>
///         <b>Not an extension axis (#555).</b> There is no discovery, no
///         scanning for implementations, and no way for a host to add one. That
///         is the point: a protocol surface that grows by editing one array
///         fails loudly, and one that grows by whatever happens to be in the
///         DI container does not fail at all.
///     </para>
/// </remarks>
public sealed class RequestHandlerRegistry
{
    private readonly FrozenDictionary<Type, RequestHandlerEntry> _entries;

    private RequestHandlerRegistry(IEnumerable<RequestHandlerEntry> entries)
    {
        var byType = new Dictionary<Type, RequestHandlerEntry>();
        foreach (RequestHandlerEntry entry in entries)
        {
            // A duplicate is a real defect — two handlers claiming one request
            // type means the winner depends on enumeration order — and a
            // Dictionary ctor would surface it as a bare ArgumentException.
            if (!byType.TryAdd(entry.RequestType, entry))
            {
                throw new InvalidOperationException(
                    $"Two IPC request handlers are registered for '{entry.RequestType.Name}'. "
                    + "Each request type has exactly one handler; remove the duplicate from "
                    + "RequestHandlerRegistry.CreateDefault.");
            }
        }

        // EqualityComparer<Type>.Default is reference equality — Type does not
        // override Equals — which is the identity the exact-type lookup wants.
        _entries = byType.ToFrozenDictionary(EqualityComparer<Type>.Default);
    }

    /// <summary>Request types this registry can answer.</summary>
    public IReadOnlyCollection<Type> HandledRequestTypes => _entries.Keys;

    /// <summary>
    ///     Union members with no handler here and no upstream owner — i.e. the
    ///     request types this registry would silently refuse if the startup
    ///     census did not exist. Empty is the only acceptable value.
    /// </summary>
    public IReadOnlyList<Type> UnhandledRequestTypes()
        =>
        [
            .. HarborRequestTypes.All
                .Where(t => !_entries.ContainsKey(t) && !HarborRequestTypes.HandledBeforeDispatch.Contains(t)),
        ];

    /// <summary>
    ///     A copy of this registry minus one request type. The construction
    ///     seam for a deliberately incomplete table — the only way to exercise
    ///     the dispatcher's startup refusal, and therefore the only way a test
    ///     can prove the refusal exists.
    /// </summary>
    /// <param name="requestType">The request type to drop.</param>
    /// <exception cref="InvalidOperationException">
    ///     The registry does not handle <paramref name="requestType" /> at all,
    ///     which would make this a silent no-op and a confusing test.
    /// </exception>
    public RequestHandlerRegistry Without(Type requestType)
    {
        if (!_entries.ContainsKey(requestType))
        {
            throw new InvalidOperationException(
                $"This registry has no handler for '{requestType.Name}', so there is nothing to remove. "
                + "Handled: " + string.Join(", ", _entries.Keys.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal)) + ".");
        }

        return new RequestHandlerRegistry(
            _entries.Where(e => e.Key != requestType).Select(e => e.Value));
    }

    /// <summary>
    ///     Build the canonical registry. The array below is the whole protocol
    ///     surface — one entry per <see cref="HarborRequest" /> member that the
    ///     dispatcher owns (<see cref="HarborRequestTypes.HandledBeforeDispatch" />
    ///     is deliberately absent).
    /// </summary>
    /// <param name="agent">The single-flight agent runner.</param>
    /// <param name="agents">Agent definitions, for <see cref="StartAgentRequest" />.</param>
    /// <param name="sessions">Session store.</param>
    /// <param name="providers">Provider registry.</param>
    /// <param name="tools">Tool registry.</param>
    /// <param name="broadcaster">Event stream, for <see cref="SubscribeToEventsRequest" />.</param>
    /// <param name="leases">Session-lease registry, for <see cref="StartAgentRequest" />.</param>
    /// <param name="coordinator">
    ///     Approval coordinator. Nullable: minimal/test hosts may not register
    ///     it, and <see cref="AbortAgentRequest" /> falls back to direct cancel.
    /// </param>
    public static RequestHandlerRegistry CreateDefault(
        IAgent agent,
        IAgentRegistry agents,
        ISessionStore sessions,
        IProviderRegistry providers,
        IToolRegistry tools,
        EventBroadcaster broadcaster,
        SessionLeaseRegistry leases,
        IApprovalCoordinator? coordinator = null)
    {
        RequestHandlerEntry[] entries =
        [
            Entry(new StartAgentRequestHandler(agent, agents, sessions, leases)),
            Entry(new AbortAgentRequestHandler(agent, coordinator)),
            Entry(new SendPromptRequestHandler(agent)),
            Entry(new CreateSessionRequestHandler(sessions)),
            Entry(new ListSessionsRequestHandler(sessions)),
            Entry(new GetSessionRequestHandler(sessions)),
            Entry(new DeleteSessionRequestHandler(sessions)),
            Entry(new GetMessagesRequestHandler(sessions)),
            Entry(new ListProvidersRequestHandler(providers)),
            Entry(new ListModelsRequestHandler(providers)),
            Entry(new ListToolsRequestHandler(tools)),
            Entry(new SubscribeToEventsRequestHandler(broadcaster)),
            Entry(new ConnectRequestHandler()),
            Entry(new DisconnectRequestHandler()),
        ];

        return new RequestHandlerRegistry(entries);
    }

    /// <summary>
    ///     Erases the handler's type parameter, deriving the registry key from
    ///     the very same generic instantiation. This is what removes the
    ///     "key says one type, <c>CanHandle</c> says another" hole.
    /// </summary>
    private static RequestHandlerEntry Entry<TRequest>(IRequestHandler<TRequest> handler)
        where TRequest : HarborRequest
        => new(
            typeof(TRequest),
            (request, context, ct) => handler.HandleAsync((TRequest)request, context, ct));

    /// <summary>
    ///     Exact-type lookup. Deliberately not "is assignable": every wire
    ///     request is a <c>sealed record</c>, and a subclass that is not itself
    ///     a tagged union member has no business being answered by its parent's
    ///     handler. A miss is the loud path in <see cref="RequestDispatcher" />,
    ///     never a silent walk up the hierarchy.
    /// </summary>
    internal bool TryGetEntry(Type requestType, out RequestHandlerEntry entry)
        => _entries.TryGetValue(requestType, out entry);
}
