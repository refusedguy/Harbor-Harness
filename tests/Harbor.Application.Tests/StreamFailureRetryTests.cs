using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Application.Resilience;
using Harbor.TestKit;
using TUnit.Assertions;
using TestSessionContext = Harbor.TestKit.TestSessionContext;

namespace Harbor.Application.Tests;

/// <summary>
///     #1514: the provider stream's terminal failure is a VALUE now. The
///     retry policy's value-shaped path decides by reading the failure's
///     transport classification, and the failure that ends the run carries
///     the same kind, message and details the thrown exception used to.
/// </summary>
/// <remarks>
///     The behaviour the turn runner had before the change is pinned by the
///     suites that already exist — <see cref="RateLimitRetryTests" /> (the
///     transient storm degrades, the fatal error fails fast) and
///     <see cref="ErrorRouting259Tests" /> (one collapsed error per storm,
///     none on recovery). These tests pin the mechanism underneath them: the
///     verdict is a value, and the value keeps the failure's identity.
/// </remarks>
public class StreamFailureRetryTests
{
    private static StreamFailure Failure(
        ProviderErrorKind kind,
        string message = "slow down",
        string? details = "provider body") => new(kind, message, details);

    private static RetryOptions Opts(int max, int? rateLimitMax = null) =>
        new(max, TimeSpan.FromMilliseconds(1), UseJitter: false, MaxRateLimitAttempts: rateLimitMax);

    // ── the value-shaped retry path ──

    [Test]
    public async Task TransientFailure_IsRetried_AndTheExhaustedFailureKeepsItsKindMessageAndDetails()
    {
        var policy = new RetryPolicy(new ManualTimeProvider(fireTimerOnCreate: true));
        int calls = 0;

        Result<string, StreamFailure> outcome = await policy.ExecuteValueAsync<string>(
            _ =>
            {
                calls++;
                return Task.FromResult(Result.Failure<string, StreamFailure>(
                    Failure(ProviderErrorKind.ServerError, "503 from the provider")));
            },
            Opts(max: 3),
            null,
            CancellationToken.None);

        // The budget is spent (3 attempts), and the failure that comes back
        // is the one the attempt reported — same kind, same message, same
        // provider body.
        await Assert.That(calls).IsEqualTo(3);
        await Assert.That(outcome.IsFailure).IsTrue();
        await Assert.That(outcome.Error.Kind).IsEqualTo(ProviderErrorKind.ServerError);
        await Assert.That(outcome.Error.Message).IsEqualTo("503 from the provider");
        await Assert.That(outcome.Error.Details).IsEqualTo("provider body");
    }

    [Test]
    public async Task FatalFailure_IsNotRetried_AndTheFailureIsReturnedUnchanged()
    {
        var policy = new RetryPolicy(new ManualTimeProvider(fireTimerOnCreate: true));
        int calls = 0;

        Result<string, StreamFailure> outcome = await policy.ExecuteValueAsync<string>(
            _ =>
            {
                calls++;
                return Task.FromResult(Result.Failure<string, StreamFailure>(
                    Failure(ProviderErrorKind.Auth, "bad key", details: null)));
            },
            Opts(max: 5),
            null,
            CancellationToken.None);

        // Auth failures are fatal: one attempt, no retry, the failure as-is.
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(outcome.IsFailure).IsTrue();
        await Assert.That(outcome.Error.Kind).IsEqualTo(ProviderErrorKind.Auth);
        await Assert.That(outcome.Error.Message).IsEqualTo("bad key");
        await Assert.That(outcome.Error.Details).IsNull();
    }

    [Test]
    public async Task RateLimitFailure_DrawsFromTheRateLimitBudget_NotTheGeneralOne()
    {
        var policy = new RetryPolicy(new ManualTimeProvider(fireTimerOnCreate: true));
        int rateCalls = 0;
        int serverCalls = 0;

        // A 429 survives on its own minutes-scale budget (4 attempts)…
        await policy.ExecuteValueAsync<string>(
            _ =>
            {
                rateCalls++;
                return Task.FromResult(Result.Failure<string, StreamFailure>(Failure(ProviderErrorKind.RateLimit)));
            },
            Opts(max: 2, rateLimitMax: 4),
            null,
            CancellationToken.None);

        // …while a 5xx still dies on the general one (2 attempts).
        await policy.ExecuteValueAsync<string>(
            _ =>
            {
                serverCalls++;
                return Task.FromResult(Result.Failure<string, StreamFailure>(Failure(ProviderErrorKind.ServerError)));
            },
            Opts(max: 2, rateLimitMax: 4),
            null,
            CancellationToken.None);

        await Assert.That(rateCalls).IsEqualTo(4);
        await Assert.That(serverCalls).IsEqualTo(2);
    }

    [Test]
    public async Task Success_PassesThrough_WithoutARetry()
    {
        var policy = new RetryPolicy(new ManualTimeProvider(fireTimerOnCreate: true));
        int calls = 0;

        Result<string, StreamFailure> outcome = await policy.ExecuteValueAsync<string>(
            _ =>
            {
                calls++;
                return Task.FromResult(Result.Success<string, StreamFailure>("streamed"));
            },
            Opts(max: 3),
            null,
            CancellationToken.None);

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(outcome.IsSuccess).IsTrue();
        await Assert.That(outcome.Value).IsEqualTo("streamed");
    }

    [Test]
    public async Task OnRetry_ReceivesTheFailureValueAndTheAttemptNumber()
    {
        var policy = new RetryPolicy(new ManualTimeProvider(fireTimerOnCreate: true));
        var seen = new List<(int Attempt, ProviderErrorKind Kind, string Message)>();

        await policy.ExecuteValueAsync<string>(
            _ => Task.FromResult(Result.Failure<string, StreamFailure>(
                Failure(ProviderErrorKind.Network, "socket closed"))),
            Opts(max: 3),
            (failure, attempt) => seen.Add((attempt, failure.Kind, failure.Message)),
            CancellationToken.None);

        // Two retries before the budget is spent on the third attempt.
        await Assert.That(seen.Count).IsEqualTo(2);
        await Assert.That(seen[0].Attempt).IsEqualTo(1);
        await Assert.That(seen[1].Attempt).IsEqualTo(2);
        await Assert.That(seen[0].Kind).IsEqualTo(ProviderErrorKind.Network);
        await Assert.That(seen[0].Message).IsEqualTo("socket closed");
    }

    [Test]
    public async Task CancelledRun_IsNotRetried_TheFailureComesBackAsIs()
    {
        var policy = new RetryPolicy(new ManualTimeProvider(fireTimerOnCreate: true));
        using var cts = new CancellationTokenSource();
        int calls = 0;

        Result<string, StreamFailure> outcome = await policy.ExecuteValueAsync<string>(
            _ =>
            {
                calls++;
                cts.Cancel(); // cancel while the verdict is being read
                return Task.FromResult(Result.Failure<string, StreamFailure>(Failure(ProviderErrorKind.ServerError)));
            },
            Opts(max: 5),
            null,
            cts.Token);

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(outcome.IsFailure).IsTrue();
        await Assert.That(outcome.Error.Kind).IsEqualTo(ProviderErrorKind.ServerError);
    }

    // ── the turn runner, through the loop ──

    [Test]
    public async Task FatalStreamError_FailsTheRunWithTheProviderMessage()
    {
        var client = new ScriptedLlmClient([
            TestEvents.LlmError("bad key: the API key is invalid", kind: ProviderErrorKind.Auth, statusCode: 401)]);
        var bus = new FakeEventBus();
        var loop = AgentLoopFixtures.LoopWithClock(client, bus, new ManualTimeProvider(fireTimerOnCreate: true));
        var session = new TestSessionContext(
            Session.Create("/tmp/harbor-1514-fatal", "code", "test", "test-model"));

        var result = await loop.RunAsync(session, TestAgents.AllowAll());

        // The failure travels back as a value and the run fails with the
        // provider's own message — the text the thrown exception carried.
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).IsEqualTo("bad key: the API key is invalid");
        await Assert.That(client.StreamCalls).IsEqualTo(1);
        await Assert.That(bus.Events.OfType<AgentErrorEvent>().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task SuccessfulStream_IsUnchanged_TheAssistantMessageIsPersisted()
    {
        var client = new ScriptedLlmClient([
            TestEvents.TextDelta(delta: "hello"),
            TestEvents.StepFinish(usage: new Usage(1, 1))]);
        var bus = new FakeEventBus();
        var loop = AgentLoopFixtures.LoopWithClock(client, bus, new ManualTimeProvider(fireTimerOnCreate: true));
        var session = new TestSessionContext(
            Session.Create("/tmp/harbor-1514-happy", "code", "test", "test-model"));

        var result = await loop.RunAsync(session, TestAgents.AllowAll());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(client.StreamCalls).IsEqualTo(1);
        var assistant = session.Messages.OfType<AssistantMessage>().Single();
        await Assert.That(assistant.Parts.OfType<TextPart>().Single().Text).IsEqualTo("hello");
        await Assert.That(bus.Events.OfType<AgentErrorEvent>().Count()).IsEqualTo(0);
        await Assert.That(bus.Events.OfType<MessageEndEvent>().Count()).IsEqualTo(1);
        await Assert.That(bus.Events.OfType<TurnEndEvent>().Count()).IsEqualTo(1);
    }
}
