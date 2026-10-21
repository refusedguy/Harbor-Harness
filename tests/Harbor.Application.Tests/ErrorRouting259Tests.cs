using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TestSessionContext = Harbor.TestKit.TestSessionContext;

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
    [Test]
    public async Task RateLimitStorm_PublishesSingleAgentError_InsteadOfOnePerAttempt()
    {
        var client = new ScriptedLlmClient([
            TestEvents.TextDelta(delta: "partial-"),
            TestEvents.LlmError("slow down", kind: ProviderErrorKind.RateLimit, statusCode: 429)]);
        var bus = new FakeEventBus();
        var loop = AgentLoopFixtures.LoopWithClock(client, bus, new ManualTimeProvider(fireTimerOnCreate: true));
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
            TestEvents.LlmError("bad key", kind: ProviderErrorKind.Auth, statusCode: 401)]);
        var bus = new FakeEventBus();
        var loop = AgentLoopFixtures.LoopWithClock(client, bus, new ManualTimeProvider(fireTimerOnCreate: true));
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
            [TestEvents.LlmError("slow down", kind: ProviderErrorKind.RateLimit, statusCode: 429)],
            [TestEvents.TextDelta(delta: "recovered"), TestEvents.StepFinish(usage: new Usage(1, 1))]);
        var bus = new FakeEventBus();
        var loop = AgentLoopFixtures.LoopWithClock(client, bus, new ManualTimeProvider(fireTimerOnCreate: true));
        var session = new TestSessionContext(
            Session.Create("/tmp/harbor-259-recovery", "code", "test", "test-model"));

        var result = await loop.RunAsync(session, TestAgents.AllowAll());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(client.StreamCalls).IsEqualTo(2);
        await Assert.That(bus.Events.OfType<AgentErrorEvent>().Count()).IsEqualTo(0);
    }
}
