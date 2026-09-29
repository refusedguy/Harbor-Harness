namespace Harbor.Ipc.Protocol;

/// <summary>
///     Per-connection state a request handler is allowed to look at,
///     assembled by <see cref="RequestDispatcher.DispatchAsync" /> out of the
///     five loose parameters the dispatcher used to pass to fourteen private
///     methods.
/// </summary>
/// <param name="ClientId">
///     The calling connection's id, or null for a host that dispatches
///     in-process. Required for session-lease acquisition
///     (<see cref="StartAgentRequest" />) and addressed event delivery.
/// </param>
/// <param name="ReplyStream">
///     The client's reply stream. Non-null ONLY for
///     <see cref="SubscribeToEventsRequest" />, whose handler hands it to
///     <see cref="EventBroadcaster" /> so events can be pushed out of band.
/// </param>
/// <param name="ReplyWriteLock">
///     The per-client write lock that serializes every write to
///     <paramref name="ReplyStream" />; the broadcaster needs it so its
///     <see cref="EventEnvelope" /> frames cannot interleave with the
///     dispatcher's response frames.
/// </param>
public sealed record RequestContext(
    string? ClientId,
    Stream? ReplyStream,
    SemaphoreSlim? ReplyWriteLock);

/// <summary>
///     Handles exactly one <see cref="HarborRequest" /> subtype — the unit
///     <see cref="RequestHandlerRegistry" /> composes.
/// </summary>
/// <typeparam name="TRequest">
///     The request subtype this handler owns. The type parameter IS the
///     registration: <see cref="RequestHandlerRegistry" /> reads the registry
///     key off the closed interface, so a handler cannot claim one request type
///     and be looked up under another.
/// </typeparam>
/// <remarks>
///     <para>
///         <b>Why a type parameter and not <c>CanHandle</c>.</b> The
///         renderer-side twin of this shape
///         (<c>src/Harbor.Terminal.Abstractions/Renderers/AgentEventHandler.cs</c>)
///         asks <c>bool CanHandle(AgentEvent)</c>, and #485 proposed copying
///         that. Copying it here would re-create the very defect the issue is
///         about: with a <c>Dictionary&lt;Type, …&gt;</c> in play, "which type
///         do I claim" gets stated twice — as the key and as the predicate — and
///         a handler whose <c>CanHandle</c> names a different type than the key
///         it is filed under compiles, ships, and answers the wrong request.
///         The type parameter leaves exactly one declaration, and the compiler
///         owns it.
///     </para>
///     <para>
///         <b>Not an extension axis.</b> Nothing scans for implementations and
///         no host can contribute one: the set is the array literal in
///         <see cref="RequestHandlerRegistry.CreateDefault" /> and the startup
///         census rejects a union member that array forgot. The feature freeze
///         (#555) is why that is deliberate.
///     </para>
/// </remarks>
public interface IRequestHandler<in TRequest>
    where TRequest : HarborRequest
{
    /// <summary>Handle one request and produce the response to write back.</summary>
    /// <param name="request">The typed request.</param>
    /// <param name="context">Per-connection state; see <see cref="RequestContext" />.</param>
    /// <param name="ct">Cancellation token for this connection.</param>
    /// <returns>
    ///     The response. Expected failures come back as
    ///     <see cref="ErrorResponse" /> — the dispatcher also converts an
    ///     escaping exception into one, so a handler never has to.
    /// </returns>
    Task<HarborResponse> HandleAsync(TRequest request, RequestContext context, CancellationToken ct);
}
