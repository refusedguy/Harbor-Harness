namespace Harbor.Ipc.Protocol;

/// <summary>
///     The <see cref="HarborRequest" /> union's member census, declared once,
///     and the one member that is deliberately NOT handled by the dispatcher.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists.</b> #485's finding was that
///         <c>RequestDispatcher.DispatchAsync</c> held a 14-arm switch with a
///         <c>_ =&gt;</c> arm answering
///         <c>ErrorResponse { Message = "Unknown request type: X" }</c>. A
///         request type the server did not implement therefore looked exactly
///         like a request type it did — it compiled, shipped, and failed at the
///         client as an opaque string. The census is what lets the dispatcher
///         refuse to start in that state instead.
///     </para>
///     <para>
///         <b>Explicit list, not runtime reflection.</b> The census used to call
///         <c>typeof(HarborRequest).Assembly.GetTypes()</c> at startup. That call
///         is annotated <c>[RequiresUnreferencedCode]</c> and was the last
///         first-party IL2026 in the NativeAOT publish. A closed wire union does
///         not need metadata discovery at run time: every member is named in
///         <see cref="All" />. <c>RequestHandlerCoverageTests.RequestTypeCensus_MatchesTheDeclaredUnion</c>
///         derives the real subtype set by reflection in the TEST assembly,
///         where reflection is allowed, and asserts it equals this list. A new
///         <see cref="HarborRequest" /> subtype that is not added here turns that
///         test red instead of shipping as a request the server cannot answer.
///     </para>
///     <para>
///         <b>Not the MessagePack <c>[Union(n, typeof(T))]</c> tags.</b> The tags
///         are the hand-maintained list docs/PATTERNS.md §7 rule 3 is about, and
///         they are worth keeping honest — but <c>MessagePack.UnionAttribute</c>
///         exposes only <c>Key</c> as a public member in 3.1.x, so the
///         <c>typeof(T)</c> argument is not readable and a tag census yields
///         integers and no names. MessagePack's own analyzer plus
///         <c>ProtocolSerializationTests</c> keep the tags honest; the equality
///         test above keeps this list honest against the type system.
///     </para>
///     <para>
///         <b>Single home.</b> <c>tests/Harbor.Architecture.Tests/ExhaustiveUnionSwitchRule.cs</c>
///         registers the same union for the wildcard-arm scan and derives its
///         own member set from its own reflection pass. Three lists now describe
///         one closed set, so the equality test is what keeps the product list
///         and the test-only reflection passes in step.
///     </para>
/// </remarks>
public static class HarborRequestTypes
{
    /// <summary>
    ///     Every concrete <see cref="HarborRequest" /> subtype in the wire
    ///     union, ordered by type name. Kept explicit so NativeAOT never has to
    ///     discover wire types from assembly metadata.
    /// </summary>
    /// <remarks>
    ///     The union lives in <c>Harbor.Ipc.Abstractions</c> because MessagePack's
    ///     static resolver is generated per assembly, so a request type declared
    ///     elsewhere could not be tagged, let alone serialized. Every type below
    ///     is one of the 15 <c>[Union(n, typeof(T))]</c> members of
    ///     <c>HarborRequest.cs</c>. The equality test named in the type comment
    ///     fails CI if this list drifts from the declared union.
    /// </remarks>
    public static IReadOnlyList<Type> All { get; } =
    [
        typeof(AbortAgentRequest),
        typeof(ConnectRequest),
        typeof(CreateSessionRequest),
        typeof(DeleteSessionRequest),
        typeof(DisconnectRequest),
        typeof(GetMessagesRequest),
        typeof(GetSessionRequest),
        typeof(ListModelsRequest),
        typeof(ListProvidersRequest),
        typeof(ListSessionsRequest),
        typeof(ListToolsRequest),
        typeof(PskAuthRequest),
        typeof(SendPromptRequest),
        typeof(StartAgentRequest),
        typeof(SubscribeToEventsRequest),
    ];

    /// <summary>
    ///     Union members answered BEFORE dispatch reaches the dispatcher.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Exactly one member, <see cref="PskAuthRequest" />, and it is not
    ///         an exception anybody chose: <c>MessagePackRpcServer.ApplyPskGateAsync</c>
    ///         consumes it at the connection gate — it authenticates the
    ///         connection and returns <c>GateDecision.Consumed</c>, so it never
    ///         reaches <see cref="RequestDispatcher.DispatchAsync" />. A handler
    ///         for it in the registry would be dead code behind a fail-closed
    ///         gate.
    ///     </para>
    ///     <para>
    ///         It is listed rather than special-cased so the coverage equation
    ///         <c>All == handled + HandledBeforeDispatch</c> can be asserted,
    ///         and so a SECOND member answered upstream has to be named here.
    ///         <c>RequestHandlerCoverageTests</c> pins this list to the current
    ///         single element on purpose.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<Type> HandledBeforeDispatch { get; } = [typeof(PskAuthRequest)];
}
