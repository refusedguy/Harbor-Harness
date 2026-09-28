using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TestSessionContext = Harbor.Application.Tests.Fakes.TestSessionContext;

namespace Harbor.Application.Tests;

/// <summary>
///     #259: raw provider blobs must surface once (never N duplicates from
///     per-attempt publishes) and stay bounded. CellForge already renders a
///     lone <see cref="AgentErrorEvent" /> as a collapsed card — these tests
///     pin the ingestion side: exactly one error per failed run, zero on
///     recovery.
/// </summary>
public class ErrorRouting259Tests
{
    private sealed class InstantTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;

        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            callback(state);
            return NoopTimer.Instance;
        }

        private sealed class NoopTimer : ITimer
        {
            public static readonly NoopTimer Instance = new();
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() { }
            public ValueTask DisposeAsync() => default;
        }
    }

    private static AgentLoop LoopWithClock(ScriptedLlmClient client, FakeEventBus bus, TimeProvider clock)
    {
        var agent = TestAgents.AllowAll();
        var agents = new FakeAgentRegistry(agent);
        return new AgentLoop(
            new FakeProviderRegistry(client),
            new FakeToolRegistry(),
            agents,
            new StubSystemPromptBuilder(),
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(clock),
            bus,
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance);
    }

    [Test]
    public async Task RateLimitStorm_PublishesSingleAgentError_InsteadOfOnePerAttempt()
    {
        var client = new ScriptedLlmClient([
            new TextDeltaEvent("t", "partial-"),
            new ErrorEvent("slow down", Kind: ProviderErrorKind.RateLimit, StatusCode: 429)]);
        var bus = new FakeEventBus();
        var loop = LoopWithClock(client, bus, new InstantTimeProvider());
        var session = new TestSessionContext(
            Session.Create("/tmp/harbor-259-storm", "code", "test", "test-model"));

        var result = await loop.RunAsync(session, TestAgents.AllowAll());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(client.StreamCalls).IsGreaterThan(3);
        await Assert.That(bus.Events.OfType<AgentErrorEvent>().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task FatalStreamError_PublishesSingleAgentError()
    {
        var client = new ScriptedLlmClient([
            new ErrorEvent("bad key", Kind: ProviderErrorKind.Auth, StatusCode: 401)]);
        var bus = new FakeEventBus();
        var loop = LoopWithClock(client, bus, new InstantTimeProvider());
        var session = new TestSessionContext(
            Session.Create("/tmp/harbor-259-fatal", "code", "test", "test-model"));

        var result = await loop.RunAsync(session, TestAgents.AllowAll());

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(client.StreamCalls).IsEqualTo(1);
        await Assert.That(bus.Events.OfType<AgentErrorEvent>().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task TransientThenRecovery_PublishesNoAgentError()
    {
        var client = new ScriptedLlmClient(
            [new ErrorEvent("slow down", Kind: ProviderErrorKind.RateLimit, StatusCode: 429)],
            [new TextDeltaEvent("t", "recovered"), new StepFinishEvent(0, "stop", new Usage(1, 1))]);
        var bus = new FakeEventBus();
        var loop = LoopWithClock(client, bus, new InstantTimeProvider());
        var session = new TestSessionContext(
            Session.Create("/tmp/harbor-259-recovery", "code", "test", "test-model"));

        var result = await loop.RunAsync(session, TestAgents.AllowAll());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(client.StreamCalls).IsEqualTo(2);
        await Assert.That(bus.Events.OfType<AgentErrorEvent>().Count()).IsEqualTo(0);
    }
}
