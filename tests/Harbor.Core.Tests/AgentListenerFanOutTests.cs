// AgentListenerFanOutTests.cs — what IAgent.Subscribe promises after #1561.
//
// BEFORE
// ------
// The agent kept a SECOND observer mechanism next to the event bus: a hand-rolled
// ListenerRegistry (List<Func<AgentEvent, CancellationToken, ValueTask>> + a lock +
// a snapshot + its own dispatch loop with a per-listener try/catch). Two fan-outs
// meant two places where the call order, the backpressure and the fate of a failing
// subscriber were decided, and a fix in one of them never reached the other.
//
// AFTER
// -----
// IAgent.Subscribe returns an IEventBus subscription: the bus is the only fan-out in
// the codebase. The agent's wrapper keeps the two promises the registry made — a
// listener that throws is logged and skipped and STAYS subscribed (the bus alone
// would evict it), and a disposed agent stops delivering.
//
// The tests below run against the REAL bus (InMemoryEventBus) on purpose: the bus
// IS the mechanism now, so a fake would only prove that a fake behaves like a fake.
// They pin what a caller may rely on — delivery, unsubscription, call order, the
// isolation of a failing listener, and the stop after Dispose.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Agents;
using Harbor.Registries.Events;
using Harbor.TestKit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Core.Tests;

/// <summary>
///     #1561: the agent's listener fan-out is the event bus. These are the promises
///     the deleted listener registry made and the bus subscription now makes.
/// </summary>
public sealed class AgentListenerFanOutTests
{
    private static Session NewSession() =>
        Session.Create(TestTempDirs.NewDirectory("harbor-1561-fanout"), "code", "test", "test-model");

    private static DefaultAgent NewAgent(
        IEventBus bus,
        ILogger<DefaultAgent>? logger = null,
        Session? session = null) =>
        new(
            new FakeSessionStore(session ?? NewSession()),
            new NoopAgentLoop(),
            bus,
            logger ?? NullLogger<DefaultAgent>.Instance);

    [Test]
    public async Task Subscribe_ThenPublish_ReceivesTheEvent()
    {
        var bus = new InMemoryEventBus();
        using DefaultAgent agent = NewAgent(bus);
        var received = new List<AgentEvent>();

        using IDisposable subscription = agent.Subscribe((evt, _) =>
        {
            received.Add(evt);
            return ValueTask.CompletedTask;
        });

        var published = TestEvents.TurnStart();
        await bus.PublishAsync(published);

        await Assert.That(received.Count).IsEqualTo(1)
            .Because("A subscribed listener is invoked for the event published on the bus.");
        await Assert.That(received[0]).IsEqualTo(published)
            .Because("The listener receives the published event itself, not a copy of it.");
    }

    [Test]
    public async Task SubscriptionDisposed_StopsDelivery()
    {
        var bus = new InMemoryEventBus();
        using DefaultAgent agent = NewAgent(bus);
        var received = new List<AgentEvent>();

        IDisposable subscription = agent.Subscribe((evt, _) =>
        {
            received.Add(evt);
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(TestEvents.TurnStart());
        subscription.Dispose();
        await bus.PublishAsync(TestEvents.TurnStart(2));

        await Assert.That(received.Count).IsEqualTo(1)
            .Because(
                "The returned handle is the unsubscribe: disposing it removes the listener, so the "
                + "second publish has nobody left to call. This is the contract IAgent.Subscribe states.");
    }

    [Test]
    public async Task Listeners_AreInvokedInSubscriptionOrder()
    {
        var bus = new InMemoryEventBus();
        using DefaultAgent agent = NewAgent(bus);
        var calls = new List<string>();

        using IDisposable first = agent.Subscribe((_, _) =>
        {
            calls.Add("first");
            return ValueTask.CompletedTask;
        });
        using IDisposable second = agent.Subscribe((_, _) =>
        {
            calls.Add("second");
            return ValueTask.CompletedTask;
        });
        using IDisposable third = agent.Subscribe((_, _) =>
        {
            calls.Add("third");
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(TestEvents.TurnStart());

        await Assert.That(calls.Count).IsEqualTo(3)
            .Because("Every subscribed listener is invoked for the event.");
        await Assert.That(string.Join(",", calls)).IsEqualTo("first,second,third")
            .Because(
                "The order is the subscription order, one listener at a time. The registry this replaced "
                + "promised the same thing, and the bus dispatches its snapshot in the order the "
                + "subscriptions were taken.");
    }

    [Test]
    public async Task ThrowingListener_IsLoggedAndSkipped_AndStaysSubscribed()
    {
        var bus = new InMemoryEventBus();
        Session session = NewSession();
        var logger = new CapturingLogger<DefaultAgent>();
        using DefaultAgent agent = NewAgent(bus, logger, session);
        agent.Initialize(session, TestAgents.AllowAll());
        var survivors = new List<AgentEvent>();

        // The throw expression is the lambda's whole body: the listener fails on
        // every event it is given, which is what makes it a failing listener.
        using IDisposable faulty = agent.Subscribe((_, _) => throw new InvalidOperationException("listener blew up"));
        using IDisposable healthy = agent.Subscribe((evt, _) =>
        {
            survivors.Add(evt);
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(TestEvents.TurnStart());
        await bus.PublishAsync(TestEvents.TurnStart(2));

        await Assert.That(survivors.Count).IsEqualTo(2)
            .Because(
                "A listener that throws must not break delivery to the rest: the healthy listener saw "
                + "both events. That was the registry's promise and it is still the agent's.");
        await Assert.That(logger.Errors.Count).IsEqualTo(2)
            .Because(
                "One error line per failed delivery, and TWO of them: the second publish reached the "
                + "failing listener again, so it STAYS subscribed. The bus alone evicts a subscriber "
                + "whose handler throws, so the agent's wrapper catches the exception first — otherwise "
                + "a listener that had one bad day would silently stop receiving events. The exception "
                + "rides along with the line, attached by the BCL [LoggerMessage] delegate.");
        await Assert.That(logger.Contains("Listener failed")).IsTrue()
            .Because(
                "The log line keeps the shape the deleted registry produced (\"Listener failed: "
                + "session={SessionId}\"), so an operator reading the logs sees the same text as before.");
        await Assert.That(logger.Contains(session.Id)).IsTrue()
            .Because(
                "The line names the session the agent is bound to, which is the part of the old log "
                + "that made a failure diagnosable.");
    }

    [Test]
    public async Task DisposedAgent_StopsDelivery()
    {
        var bus = new InMemoryEventBus();
        DefaultAgent agent = NewAgent(bus);
        var received = new List<AgentEvent>();

        using IDisposable subscription = agent.Subscribe((evt, _) =>
        {
            received.Add(evt);
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(TestEvents.TurnStart());
        agent.Dispose();
        await bus.PublishAsync(TestEvents.TurnStart(2));

        await Assert.That(received.Count).IsEqualTo(1)
            .Because(
                "A disposed agent delivers nothing. The registry this replaced stopped for the same "
                + "reason — the agent's own bus subscription was disposed with it, and the registry it "
                + "fed stopped receiving.");
    }

    [Test]
    public async Task Agent_WithoutListeners_LeavesTheBusWithoutSubscribers()
    {
        var bus = new InMemoryEventBus();
        using DefaultAgent agent = NewAgent(bus);

        await bus.PublishAsync(TestEvents.TurnStart());

        await Assert.That(bus.FastPathCount).IsEqualTo(1L)
            .Because(
                "The agent holds no bus subscription of its own any more: it used to subscribe in its "
                + "constructor and forward every event into its private registry, which made every "
                + "publish take the bus's slow path even when nobody listened. With the fan-out on the "
                + "bus, an agent with no listeners leaves the bus with no subscribers and the publish "
                + "takes the fast path.");
        await Assert.That(bus.PublishedCount).IsEqualTo(0L)
            .Because("A publish nobody can observe is a fast-path publish — the slow path is the one that counts.");
    }

    [Test]
    public void Subscribe_NullListener_FailsFastAtTheBoundary()
    {
        var bus = new InMemoryEventBus();
        using DefaultAgent agent = NewAgent(bus);

        // A null listener is a programming error, and the boundary is where it belongs.
        // The registry this replaced accepted the null and failed later, inside the
        // dispatch loop, as a logged NullReferenceException.
        Assert.Throws<ArgumentNullException>(() => { agent.Subscribe(null!); });
    }

    /// <summary>An agent loop that never runs — the fan-out tests publish on the bus directly.</summary>
    private sealed class NoopAgentLoop : IAgentLoop
    {
        public Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default)
            => Task.FromResult(Result.Success());
    }
}
