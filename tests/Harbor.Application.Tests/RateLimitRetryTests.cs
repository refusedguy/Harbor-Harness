using System.Net;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
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
///     #270 (429-storm resilience): server Retry-After honoring, the separate
///     minutes-scale rate-limit budget, and degraded partial results instead
///     of terminal death. Hermetic clock throughout (#54 pattern): delays are
///     recorded by the fake and complete instantly, so no wall-clock assertion
///     can flake on loaded runners.
/// </summary>
public class RateLimitRetryTests
{
    private sealed class RecordingTimeProvider : TimeProvider
    {
        public List<TimeSpan> Delays { get; } = [];

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;

        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Delays.Add(dueTime);
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

    private static HttpRequestException RateLimited(Dictionary<string, object?>? data = null)
    {
        var ex = new HttpRequestException("429 slow down", inner: null, HttpStatusCode.TooManyRequests);
        if (data is not null)
        {
            foreach ((string key, object? value) in data)
                ex.Data[key] = value;
        }
        return ex;
    }

    // ── Retry-After honoring ──

    [Test]
    public async Task RetryAfter_DataHint_HonoredInsteadOfBackoff()
    {
        var time = new RecordingTimeProvider();
        var policy = new RetryPolicy(time);
        int calls = 0;

        int result = await policy.ExecuteAsync(
            _ =>
            {
                calls++;
                if (calls < 3)
                    throw RateLimited(new Dictionary<string, object?> { ["RetryAfter"] = TimeSpan.FromSeconds(30) });
                return Task.FromResult(42);
            },
            new RetryOptions(3, TimeSpan.FromMilliseconds(20), UseJitter: false),
            CancellationToken.None);

        await Assert.That(result).IsEqualTo(42);
        await Assert.That(time.Delays.Count).IsEqualTo(2);
        await Assert.That(time.Delays[0]).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert.That(time.Delays[1]).IsEqualTo(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task RetryAfter_RogueHint_ClampedToMax()
    {
        var time = new RecordingTimeProvider();
        var policy = new RetryPolicy(time);

        try
        {
            await policy.ExecuteAsync<HttpResponseMessage>(
                _ => throw RateLimited(new Dictionary<string, object?> { ["RetryAfter"] = TimeSpan.FromHours(10) }),
                new RetryOptions(3, TimeSpan.FromMilliseconds(20), UseJitter: false, MaxRetryAfter: TimeSpan.FromSeconds(60)),
                CancellationToken.None);
            await Assert.That(false).IsTrue();
        }
        catch (HttpRequestException)
        {
            // expected exhaustion
        }

        await Assert.That(time.Delays.Count).IsEqualTo(2);
        await Assert.That(time.Delays[0]).IsEqualTo(TimeSpan.FromSeconds(60));
        await Assert.That(time.Delays[1]).IsEqualTo(TimeSpan.FromSeconds(60));
    }

    [Test]
    public async Task RetryAfter_StringSecondsHint_Honored()
    {
        var time = new RecordingTimeProvider();
        var policy = new RetryPolicy(time);

        try
        {
            await policy.ExecuteAsync<HttpResponseMessage>(
                _ => throw RateLimited(new Dictionary<string, object?> { ["Retry-After"] = "45" }),
                new RetryOptions(2, TimeSpan.FromMilliseconds(20), UseJitter: false),
                CancellationToken.None);
            await Assert.That(false).IsTrue();
        }
        catch (HttpRequestException)
        {
            // expected exhaustion
        }

        await Assert.That(time.Delays.Count).IsEqualTo(1);
        await Assert.That(time.Delays[0]).IsEqualTo(TimeSpan.FromSeconds(45));
    }

    [Test]
    public async Task IsTransient_SurfacesDataHint()
    {
        var ex = RateLimited(new Dictionary<string, object?> { ["RetryAfter"] = TimeSpan.FromSeconds(12) });

        await Assert.That(RetryPolicy.IsTransient(ex, out TimeSpan? hint)).IsTrue();
        await Assert.That(hint).IsEqualTo(TimeSpan.FromSeconds(12));
    }

    [Test]
    public async Task TryGetRetryAfter_NegativeOrGarbage_Ignored()
    {
        var negative = new HttpRequestException("429", inner: null, HttpStatusCode.TooManyRequests);
        negative.Data["RetryAfter"] = TimeSpan.FromSeconds(-5);
        await Assert.That(RetryPolicy.TryGetRetryAfter(negative, out _)).IsFalse();

        var garbage = new HttpRequestException("429", inner: null, HttpStatusCode.TooManyRequests);
        garbage.Data["RetryAfter"] = "not-a-delay";
        await Assert.That(RetryPolicy.TryGetRetryAfter(garbage, out _)).IsFalse();

        await Assert.That(RetryPolicy.TryGetRetryAfter(
            new HttpRequestException("429", inner: null, HttpStatusCode.TooManyRequests), out _)).IsFalse();
    }

    private sealed class HintedException(TimeSpan hint) : HttpRequestException("429", inner: null, HttpStatusCode.TooManyRequests), IRetryAfterHint
    {
        public TimeSpan? RetryAfter { get; } = hint;
    }

    [Test]
    public async Task RetryAfter_TypedHintInterface_Honored()
    {
        await Assert.That(RetryPolicy.TryGetRetryAfter(new HintedException(TimeSpan.FromSeconds(7)), out TimeSpan hint)).IsTrue();
        await Assert.That(hint).IsEqualTo(TimeSpan.FromSeconds(7));
        await Assert.That(RetryPolicy.TryGetRetryAfter(new HintedException(TimeSpan.FromSeconds(-1)), out _)).IsFalse();
    }

    // ── separate rate-limit budget ──

    [Test]
    public async Task IsRateLimit_Classification()
    {
        await Assert.That(RetryPolicy.IsRateLimit(
            new HttpRequestException("429", inner: null, HttpStatusCode.TooManyRequests))).IsTrue();
        await Assert.That(RetryPolicy.IsRateLimit(
            new HttpRequestException("503", inner: null, HttpStatusCode.ServiceUnavailable))).IsFalse();
        await Assert.That(RetryPolicy.IsRateLimit(new LlmStreamErrorException(
            new ErrorEvent("slow down", Kind: ProviderErrorKind.RateLimit, StatusCode: 429)))).IsTrue();
        await Assert.That(RetryPolicy.IsRateLimit(new LlmStreamErrorException(
            new ErrorEvent("bad key", Kind: ProviderErrorKind.Auth, StatusCode: 401)))).IsFalse();
        await Assert.That(RetryPolicy.IsRateLimit(new InvalidOperationException())).IsFalse();
    }

    [Test]
    public async Task RateLimitBudget_ExceedsGeneralBudget()
    {
        var policy = new RetryPolicy(new RecordingTimeProvider());
        var options = new RetryOptions(2, TimeSpan.FromMilliseconds(1), UseJitter: false, MaxRateLimitAttempts: 4);
        int rateCalls = 0;
        int serverCalls = 0;

        await Assert.ThrowsAsync<HttpRequestException>(async () => await policy.ExecuteAsync<HttpResponseMessage>(
            _ =>
            {
                rateCalls++;
                throw RateLimited();
            },
            options, CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(async () => await policy.ExecuteAsync<HttpResponseMessage>(
            _ =>
            {
                serverCalls++;
                throw new HttpRequestException("503", inner: null, HttpStatusCode.ServiceUnavailable);
            },
            options, CancellationToken.None));

        // 429s survive on their own budget; 5xx still dies on the general one.
        await Assert.That(rateCalls).IsEqualTo(4);
        await Assert.That(serverCalls).IsEqualTo(2);
    }

    [Test]
    public async Task RateLimitBudget_DefaultsToGeneralBudget()
    {
        var policy = new RetryPolicy(new RecordingTimeProvider());
        int calls = 0;

        await Assert.ThrowsAsync<HttpRequestException>(async () => await policy.ExecuteAsync<HttpResponseMessage>(
            _ =>
            {
                calls++;
                throw RateLimited();
            },
            new RetryOptions(3, TimeSpan.FromMilliseconds(1), UseJitter: false),
            CancellationToken.None));

        // No opt-in → legacy behaviour: 3 attempts, as before #270.
        await Assert.That(calls).IsEqualTo(3);
    }

    [Test]
    public async Task ComputeRateLimitDelay_GrowsFromRateLimitRoot_AndCapsAtTwoMinutes()
    {
        var options = new RetryOptions(
            3, TimeSpan.FromSeconds(1), UseJitter: false,
            MaxRateLimitAttempts: 10, RateLimitBaseDelay: TimeSpan.FromSeconds(5));

        await Assert.That(RetryPolicy.ComputeRateLimitDelay(options, 1)).IsEqualTo(TimeSpan.FromSeconds(5));
        await Assert.That(RetryPolicy.ComputeRateLimitDelay(options, 2)).IsEqualTo(TimeSpan.FromSeconds(10));
        await Assert.That(RetryPolicy.ComputeRateLimitDelay(options, 3)).IsEqualTo(TimeSpan.FromSeconds(20));
        await Assert.That(RetryPolicy.ComputeRateLimitDelay(options, 10)).IsEqualTo(TimeSpan.FromMinutes(2));
    }

    // ── degraded partial results ──

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
    public async Task RateLimitStorm_DegradesWithPartialOutput_InsteadOfTerminalDeath()
    {
        // Every attempt streams one text delta, then the provider reports 429.
        // ScriptedLlmClient repeats the last script, so the storm never lets up.
        var client = new ScriptedLlmClient([
            new TextDeltaEvent("t", "partial-"),
            new ErrorEvent("slow down", Kind: ProviderErrorKind.RateLimit, StatusCode: 429)]);
        var bus = new FakeEventBus();
        var loop = LoopWithClock(client, bus, new RecordingTimeProvider());
        var session = new TestSessionContext(
            Session.Create("/tmp/harbor-rate-limit-degrade-tests", "code", "test", "test-model"));

        var result = await loop.RunAsync(session, TestAgents.AllowAll());

        // Degraded success: the run ends gracefully, not with terminal death…
        await Assert.That(result.IsSuccess).IsTrue();
        // …after outliving the old 3-attempt budget on the rate-limit budget…
        await Assert.That(client.StreamCalls).IsGreaterThan(3);
        // …with the streamed partial persisted for resumption…
        var degraded = session.Messages.OfType<AssistantMessage>().ToList();
        await Assert.That(degraded.Count).IsEqualTo(1);
        await Assert.That(degraded[0].StopReason).IsEqualTo(StopReason.Error);
        await Assert.That(degraded[0].Parts.OfType<TextPart>().Single().Text).Contains("partial-");
        // …and a well-formed event tail (turn end + agent end, no dangling turn).
        await Assert.That(bus.Events.OfType<TurnEndEvent>().Count()).IsEqualTo(1);
        await Assert.That(bus.Events.OfType<MessageEndEvent>().Count()).IsEqualTo(1);
        var agentEnd = bus.Events.OfType<AgentEndEvent>().Single();
        await Assert.That(agentEnd.Cancelled).IsFalse();
    }

    [Test]
    public async Task FatalStreamError_StillFailsTerminally()
    {
        var client = new ScriptedLlmClient([
            new ErrorEvent("bad key", Kind: ProviderErrorKind.Auth, StatusCode: 401)]);
        var bus = new FakeEventBus();
        var loop = LoopWithClock(client, bus, new RecordingTimeProvider());
        var session = new TestSessionContext(
            Session.Create("/tmp/harbor-rate-limit-fatal-tests", "code", "test", "test-model"));

        var result = await loop.RunAsync(session, TestAgents.AllowAll());

        // Auth failures are fatal: no retry, no degrade — one attempt, terminal failure.
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(client.StreamCalls).IsEqualTo(1);
        await Assert.That(session.Messages.OfType<AssistantMessage>().Count()).IsEqualTo(0);
    }

    // ── sub-agent degrade ──

    private sealed class FailingLoop(CSharpFunctionalExtensions.Result outcome, params AgentMessage[] replies) : IAgentLoop
    {
        public async Task<CSharpFunctionalExtensions.Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default)
        {
            foreach (AgentMessage message in replies)
                await session.AppendMessageAsync(message, ct).ConfigureAwait(false);
            return outcome;
        }
    }

    private static AgentDefinition SubAgent(string name = "explore") => new(
        AgentName.Create(name),
        name,
        name,
        "test-model",
        "test",
        PermissionRuleset.Empty,
        20,
        IsSubAgent: true);

    private static AssistantMessage Assistant(string text) => new(
        Guid.NewGuid().ToString("N"),
        "session-1",
        DateTimeOffset.UtcNow,
        [new TextPart(text)],
        StopReason.Stop,
        new Usage(0, 0),
        "test-model");

    private static Session NewSession() =>
        Session.Create(Harbor.TestKit.TestTempDirs.NewDirectory("harbor-subagent-degrade"), "code", "test", "test-model");

    [Test]
    public async Task SubAgentFailure_SurfacesDegradedPartialOutput()
    {
        var store = new FakeSessionStore(NewSession());
        var loop = new FailingLoop(
            CSharpFunctionalExtensions.Result.Failure("model exploded"),
            Assistant("partial work done before the failure"));
        var runner = new SubAgentRunner(store, loop, NullLogger<SubAgentRunner>.Instance);

        var result = await runner.RunAsync(SubAgent(), new SubAgentRunRequest("go"));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("Partial output (degraded): partial work done before the failure");
    }

    [Test]
    public async Task SubAgentFailure_WithoutPartialOutput_KeepsLegacyMessage()
    {
        var store = new FakeSessionStore(NewSession());
        var loop = new FailingLoop(CSharpFunctionalExtensions.Result.Failure("model exploded"));
        var runner = new SubAgentRunner(store, loop, NullLogger<SubAgentRunner>.Instance);

        var result = await runner.RunAsync(SubAgent(), new SubAgentRunRequest("go"));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("failed: model exploded");
        await Assert.That(result.Error.Contains("Partial output")).IsFalse();
    }
}
