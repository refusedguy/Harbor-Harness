using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Ipc.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Ipc.Tests;

/// <summary>
///     #485 — the <see cref="HarborRequest" /> union is answered by a
///     <see cref="RequestHandlerRegistry" />, and both ends of that boundary
///     are pinned here:
///     <list type="bullet">
///         <item>
///             CONSTRUCTION. A union member with no handler is a composition
///             error: <see cref="RequestDispatcher" /> throws, naming the type,
///             instead of starting a server that would answer it with a string.
///         </item>
///         <item>
///             DISPATCH. A request whose type is not in the tagged union at all
///             is logged, counted, and refused with a parsable error — the
///             "unknown request" test #485 asked for.
///         </item>
///     </list>
/// </summary>
[NotInParallel("ipc")]
public class RequestHandlerCoverageTests
{
    /// <summary>
    ///     A <see cref="HarborRequest" /> that is deliberately NOT a member of
    ///     the wire union.
    /// </summary>
    /// <remarks>
    ///     It carries no MessagePack <c>[Union(n, typeof(T))]</c> tag and lives
    ///     in this assembly, so <see cref="HarborRequestTypes.All" /> — which
    ///     scans <c>typeof(HarborRequest).Assembly</c> — cannot see it, and no
    ///     handler can be registered for it. That is the honest way to reach
    ///     the dispatch-time branch: a client speaking a protocol the server
    ///     does not have.
    /// </remarks>
    private sealed record UntaggedProbeRequest : HarborRequest;

    // ── Construction: the composition boundary ─────────────────────────────

    /// <summary>
    ///     THE test #485 was filed for. Before the refactor, a
    ///     <c>HarborRequest</c> subtype with no arm in the dispatch switch
    ///     compiled, shipped, and was answered at runtime with
    ///     <c>ErrorResponse { Message = "Unknown request type: X" }</c> — a
    ///     string indistinguishable from a real answer. Now the same omission
    ///     stops the server from starting, and says which type is missing.
    /// </summary>
    [Test]
    public void Dispatcher_RegistryMissingAUnionMember_ThrowsAtConstruction()
    {
        var sp = TestHost.Build();
        RequestHandlerRegistry incomplete = Registry(sp).Without(typeof(ListToolsRequest));

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => NewDispatcher(sp, incomplete));

        Assert.That(thrown.Message).Contains("ListToolsRequest").Because(
            "the message must name the missing request type — an operator who cannot see which "
            + "one is missing cannot fix the composition");
    }

    /// <summary>
    ///     A handler cannot be filed under a request type it does not
    ///     implement, and <see cref="RequestHandlerRegistry.Without" /> cannot be
    ///     used as a silent no-op on a type the registry never had. The registry
    ///     key is read off the closed generic interface, so there is no second
    ///     place to get it wrong.
    /// </summary>
    [Test]
    public void Registry_WithoutATypeItDoesNotHandle_Throws()
    {
        var sp = TestHost.Build();
        RequestHandlerRegistry registry = Registry(sp);

        Assert.Throws<InvalidOperationException>(
            () => registry.Without(typeof(UntaggedProbeRequest)));
    }

    /// <summary>
    ///     The census is total, not "the switch has 14 arms". Every member of
    ///     <see cref="HarborRequestTypes.All" /> is either answered by the
    ///     registry or owned upstream, and nothing else is.
    /// </summary>
    [Test]
    public async Task DefaultRegistry_AnswersEveryUnionMember_OrNamesTheUpstreamOwner()
    {
        var sp = TestHost.Build();
        RequestHandlerRegistry registry = Registry(sp);

        await Assert.That(registry.UnhandledRequestTypes().ToArray()).IsEmpty().Because(
            "RequestDispatcher's constructor throws on a non-empty list, so a non-empty result "
            + "here means the shipped registry is incomplete");

        // The equation, asserted rather than assumed: 15 members, 14 handlers,
        // 1 consumed by the PSK gate before dispatch ever reaches the dispatcher.
        await Assert.That(HarborRequestTypes.All.Count).IsEqualTo(15).Because(
            "src/Harbor.Ipc.Abstractions/Protocol/HarborRequest.cs declares 15 [Union(n, typeof(T))] "
            + "tags, 0..14. If this count moved, the union moved, and the registry plus "
            + "HarborRequestTypes.HandledBeforeDispatch must move with it");

        await Assert.That(registry.HandledRequestTypes.Count).IsEqualTo(14);

        await Assert.That(HarborRequestTypes.HandledBeforeDispatch.ToArray())
            .IsEquivalentTo([typeof(PskAuthRequest)]).Because(
            "PskAuthRequest is consumed by MessagePackRpcServer.ApplyPskGateAsync and never reaches "
            + "the dispatcher. A SECOND upstream-handled member would mean the union has another "
            + "seam the dispatcher does not own, and it has to be named here with a comment rather "
            + "than discovered at runtime");
    }

    /// <summary>
    ///     Non-vacuity of the census: it reads the real union, not an empty or
    ///     stale set. An empty census would make the constructor's check pass
    ///     for free, which is the same failure mode as a source rule grading
    ///     against a non-existent assembly.
    /// </summary>
    [Test]
    public async Task RequestTypeCensus_IsLive()
    {
        await Assert.That(HarborRequestTypes.All.Count).IsGreaterThan(0).Because(
            "typeof(HarborRequest).Assembly must be loadable and must contain the union's members; "
            + "an empty census makes every registry look complete");

        List<string> names = [.. HarborRequestTypes.All.Select(t => t.Name)];

        foreach (string expected in new[]
                 {
                     "StartAgentRequest", "SendPromptRequest", "ListSessionsRequest",
                     "ListToolsRequest", "SubscribeToEventsRequest", "PskAuthRequest",
                 })
        {
            await Assert.That(names).Contains(expected).Because(
                "the census is derived from the type system, so a member it does not name does not "
                + "exist as far as every boundary check is concerned");
        }

        List<bool> memberIsAbstract = [.. HarborRequestTypes.All.Select(t => t.IsAbstract)];
        await Assert.That(memberIsAbstract).Contains(false).Because(
            "the census enumerates concrete subtypes; an abstract member would mean the filter "
            + "regressed and a base type was counted as its own answer");
    }

    // ── Dispatch: the wire boundary ────────────────────────────────────────

    /// <summary>
    ///     The unknown-request test. A request type the server has no handler
    ///     for is refused explicitly, with the type name in the message and a
    ///     counter an operator can watch — never a bare "Unknown request type:
    ///     X" that reads like a valid answer.
    /// </summary>
    [Test]
    public async Task Dispatch_UnknownRequestType_RefusesExplicitlyAndCounts()
    {
        var sp = TestHost.Build();
        RequestDispatcher dispatcher = NewDispatcher(sp, Registry(sp));

        await Assert.That(dispatcher.UnhandledRequestCount).IsEqualTo(0L).Because(
            "a healthy server has refused nothing yet, so a non-zero baseline here would make the "
            + "increment below meaningless");

        HarborResponse response = await dispatcher.DispatchAsync(new UntaggedProbeRequest(), null, null);

        var error = response as ErrorResponse;
        await Assert.That(error).IsNotNull().Because(
            "an unanswerable request must not be answered with an OkResponse");

        await Assert.That(error!.Message).StartsWith("NO_HANDLER:UntaggedProbeRequest:").Because(
            "the machine-parsable prefix plus the offending type name is what a client and an "
            + "operator both need; the old wildcard arm returned a bare \"Unknown request type: X\" "
            + "that named nothing actionable");

        await Assert.That(error.Message).Contains(nameof(HarborRequestTypes)).Because(
            "the message must point at the seam, not just at the symptom");

        await Assert.That(dispatcher.UnhandledRequestCount).IsEqualTo(1L).Because(
            "docs/PATTERNS.md §7 rule 2: a default kept for forward compatibility with input from "
            + "outside the process must not be silent — it logs AND counts");
    }

    /// <summary>
    ///     A known request still reaches its handler after the refactor, and the
    ///     unknown-request path did not become a latch that swallows real work:
    ///     the counter is per-event.
    /// </summary>
    [Test]
    public async Task Dispatch_KnownRequest_ReachesItsHandler_AndDoesNotCount()
    {
        var sp = TestHost.Build();
        RequestDispatcher dispatcher = NewDispatcher(sp, Registry(sp));

        HarborResponse response = await dispatcher.DispatchAsync(new ListToolsRequest(), null, null);

        await Assert.That(response as OkResponse).IsNotNull().Because(
            "ListToolsRequest is in the registry, so it must be answered normally");

        await Assert.That(dispatcher.UnhandledRequestCount).IsEqualTo(0L);
    }

    /// <summary>
    ///     The request id is echoed even on the refusal, so a client waiting on
    ///     that id gets a reply instead of hanging until its timeout.
    /// </summary>
    [Test]
    public async Task Dispatch_UnknownRequestType_EchoesTheRequestId()
    {
        var sp = TestHost.Build();
        RequestDispatcher dispatcher = NewDispatcher(sp, Registry(sp));

        var request = new UntaggedProbeRequest { RequestId = Guid.NewGuid() };
        HarborResponse response = await dispatcher.DispatchAsync(request, null, null);

        await Assert.That(response.RequestId).IsEqualTo(request.RequestId).Because(
            "the client matches responses to requests by id; a refusal that dropped it would turn a "
            + "clear error into a hang");
    }

    // ── Fixtures ───────────────────────────────────────────────────────────

    /// <summary>The registry the shipped composition root builds.</summary>
    private static RequestHandlerRegistry Registry(IServiceProvider sp)
    {
        var leases = new SessionLeaseRegistry();
        var broadcaster = new EventBroadcaster(
            sp.GetRequiredService<IEventBus>(),
            new LoggerFactory().CreateLogger<EventBroadcaster>(),
            leases);

        return RequestHandlerRegistry.CreateDefault(
            sp.GetRequiredService<IAgent>(),
            sp.GetRequiredService<IAgentRegistry>(),
            sp.GetRequiredService<ISessionStore>(),
            sp.GetRequiredService<IProviderRegistry>(),
            sp.GetRequiredService<IToolRegistry>(),
            broadcaster,
            leases);
    }

    private static RequestDispatcher NewDispatcher(IServiceProvider sp, RequestHandlerRegistry registry)
        => new(
            registry,
            new SessionLeaseRegistry(),
            new LoggerFactory().CreateLogger<RequestDispatcher>());
}
