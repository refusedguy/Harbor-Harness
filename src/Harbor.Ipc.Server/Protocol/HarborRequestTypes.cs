namespace Harbor.Ipc.Protocol;

/// <summary>
///     The <see cref="HarborRequest" /> union's member census and the one
///     member that is deliberately NOT handled by the dispatcher.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists.</b> #485's finding was that
///         <c>RequestDispatcher.DispatchAsync</c> held a 14-arm switch with a
///         <c>_ =&gt;</c> arm answering
///         <c>ErrorResponse { Message = "Unknown request type: X" }</c>. A
///         request type the server did not implement therefore looked exactly
///         like a request type it did. It compiled, shipped, and failed at the
///         client as an opaque string. The census is what lets the dispatcher
///         refuse to start in that state instead.
///     </para>
///     <para>
///         <b>Compile time, not runtime reflection.</b> <see cref="All" /> is
///         emitted by <c>Harbor.CodeGen.HarborRequestCensusGenerator</c> while
///         this assembly compiles. The generator walks the type system of the
///         declaring assembly and writes one <c>typeof(T)</c> per concrete
///         subtype. There is no <c>Assembly.GetTypes()</c> and no metadata
///         enumeration in the running server, so trimming cannot shorten the
///         census into an apparently complete one. A subtype added without a
///         handler still fails the dispatcher's constructor at startup.
///     </para>
///     <para>
///         <b>The wire format is untouched.</b> The <c>[Union(n, typeof(T))]</c>
///         tags in <c>Harbor.Ipc.Abstractions</c> stay as they are. The generated
///         census is read from the same closed set of subtypes, and each emitted
///         <c>typeof</c> keeps its type reachable under NativeAOT. MessagePack's
///         own analyzer and <c>ProtocolSerializationTests</c> still own tag
///         coverage: they fail on a subtype that cannot round-trip on the wire.
///     </para>
///     <para>
///         <b>Two censuses, different jobs.</b>
///         <c>tests/Harbor.Architecture.Tests/ExhaustiveUnionSwitchRule.cs</c>
///         derives its own member set by reflection for the wildcard-arm scan.
///         Reflection is allowed in tests and the scan grades source, not the
///         running server. The generated census here is the one the runtime
///         obeys. A subtype neither can see cannot cross the wire either.
///     </para>
/// </remarks>
public static partial class HarborRequestTypes
{
    /// <summary>
    ///     Union members answered BEFORE dispatch reaches the dispatcher.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Exactly one member, <see cref="PskAuthRequest" />, and it is not
    ///         an exception anybody chose: <c>MessagePackRpcServer.ApplyPskGateAsync</c>
    ///         consumes it at the connection gate. It authenticates the
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
