using System.Reflection;

namespace Harbor.Ipc.Protocol;

/// <summary>
///     The <see cref="HarborRequest" /> union's member census, read by
///     reflection, and the one member that is deliberately NOT handled by the
///     dispatcher.
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
///         <b>Reflection, not the MessagePack <c>[Union(n, typeof(T))]</c>
///         tags.</b> The tags are the hand-maintained list
///         docs/PATTERNS.md §7 rule 3 is about, and they are worth keeping
///         honest — but <c>MessagePack.UnionAttribute</c> exposes only
///         <c>Key</c> as a public member in 3.1.x, so the <c>typeof(T)</c>
///         argument is not readable and a tag census yields integers and no
///         names. The type system is also the stronger source: it names a
///         subtype added WITHOUT a matching tag, which the tag list cannot.
///         Tag coverage is owned elsewhere — MessagePack's own analyzer, plus
///         <c>ProtocolSerializationTests</c>, which round-trips every subtype
///         through the abstract base and so fails on an untagged member.
///     </para>
///     <para>
///         <b>Single home.</b> <c>tests/Harbor.Architecture.Tests/ExhaustiveUnionSwitchRule.cs</c>
///         registers the same union for the wildcard-arm scan, but it derives
///         its own member set from its own reflection pass. Two censuses, two
///         chances to disagree — this one is the one the runtime obeys, and
///         both read the same closed set from the same assembly, so a subtype
///         neither can see is a subtype neither exists.
///     </para>
/// </remarks>
public static class HarborRequestTypes
{
    /// <summary>
    ///     Every concrete <see cref="HarborRequest" /> subtype in the assembly
    ///     that declares the union. Computed once.
    /// </summary>
    /// <remarks>
    ///     Scoped to <c>typeof(HarborRequest).Assembly</c> deliberately: the
    ///     whole wire union lives in <c>Harbor.Ipc.Abstractions</c> because
    ///     MessagePack's static resolver is generated per assembly, so a request
    ///     type declared elsewhere could not be tagged, let alone serialized.
    /// </remarks>
    public static IReadOnlyList<Type> All { get; } = BuildCensus();

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

    private static IReadOnlyList<Type> BuildCensus()
    {
        Type[] types;
        try
        {
            types = typeof(HarborRequest).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Degrade to whatever loaded rather than throwing. A partial census
            // can only ever make the guard report a SHORTER member list, and
            // the error text below names the union — an empty census, by
            // contrast, would make every dispatcher look complete.
            types = ex.Types
                .Where(t => t is not null)
                .Select(t => t!)
                .ToArray();
        }

        return
        [
            .. types
                .Where(t => !t.IsAbstract && !t.IsInterface && typeof(HarborRequest).IsAssignableFrom(t))
                .OrderBy(t => t.Name, StringComparer.Ordinal),
        ];
    }
}
