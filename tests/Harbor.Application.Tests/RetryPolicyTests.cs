using System.Net;
using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Core.Agents;
using Harbor.Core.Resilience;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     A6 (sprint 5): first coverage for <see cref="RetryPolicy" /> —
///     transient/fatal classification, attempt accounting, backoff timing,
///     jitter bounds, and Retry-After honouring.
/// </summary>
public class RetryPolicyTests
{
    private static RetryOptions Opts(int max, int delayMs = 20, bool jitter = false) =>
        new(max, TimeSpan.FromMilliseconds(delayMs), jitter);

    // ── classification ──

    [Test]
    public async Task IsTransient_TimeoutCancellation_IsTransient()
    {
        // Timeout = TaskCanceledException WITHOUT caller cancellation.
        await Assert.That(RetryPolicy.IsTransient(new TaskCanceledException(), out _)).IsTrue();
    }

    [Test]
    public async Task IsTransient_CallerCancellation_IsFatal()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var oce = new OperationCanceledException(cts.Token);

        await Assert.That(RetryPolicy.IsTransient(oce, out _)).IsFalse();
    }

    [Test]
    public async Task IsTransient_HttpWithoutStatus_IsTransient()
    {
        // DNS / connection reset / TLS failures carry no status code.
        await Assert.That(
            RetryPolicy.IsTransient(new HttpRequestException("boom", inner: null), out _)).IsTrue();
    }

    [Test]
    public async Task IsTransient_StatusCodes_Classified()
    {
        static HttpRequestException WithStatus(HttpStatusCode code)
            => new($"status {code}", inner: null, code);

        await Assert.That(RetryPolicy.IsTransient(WithStatus(HttpStatusCode.TooManyRequests), out _)).IsTrue();
        await Assert.That(RetryPolicy.IsTransient(WithStatus(HttpStatusCode.RequestTimeout), out _)).IsTrue();
        await Assert.That(RetryPolicy.IsTransient(WithStatus(HttpStatusCode.InternalServerError), out _)).IsTrue();
        await Assert.That(RetryPolicy.IsTransient(WithStatus(HttpStatusCode.BadGateway), out _)).IsTrue();

        await Assert.That(RetryPolicy.IsTransient(WithStatus(HttpStatusCode.Unauthorized), out _)).IsFalse();
        await Assert.That(RetryPolicy.IsTransient(WithStatus(HttpStatusCode.NotFound), out _)).IsFalse();
        await Assert.That(RetryPolicy.IsTransient(WithStatus(HttpStatusCode.BadRequest), out _)).IsFalse();
    }

    [Test]
    public async Task IsTransient_AnyOtherException_IsFatal()
    {
        await Assert.That(RetryPolicy.IsTransient(new InvalidOperationException(), out _)).IsFalse();
        await Assert.That(RetryPolicy.IsTransient(new JsonException(), out _)).IsFalse();
    }

    // ── ROP-A ПР.5: typed provider error kinds ──

    [Test]
    public async Task IsTransient_Kind_TransientKindsRetry()
    {
        await Assert.That(RetryPolicy.IsTransient(ProviderErrorKind.RateLimit)).IsTrue();
        await Assert.That(RetryPolicy.IsTransient(ProviderErrorKind.Timeout)).IsTrue();
        await Assert.That(RetryPolicy.IsTransient(ProviderErrorKind.Network)).IsTrue();
        await Assert.That(RetryPolicy.IsTransient(ProviderErrorKind.ServerError)).IsTrue();
    }

    [Test]
    public async Task IsTransient_Kind_FatalKindsDoNotRetry()
    {
        await Assert.That(RetryPolicy.IsTransient(ProviderErrorKind.Auth)).IsFalse();
        await Assert.That(RetryPolicy.IsTransient(ProviderErrorKind.Malformed)).IsFalse();
        await Assert.That(RetryPolicy.IsTransient(ProviderErrorKind.Cancelled)).IsFalse();
        await Assert.That(RetryPolicy.IsTransient(ProviderErrorKind.Unknown)).IsFalse();
    }

    [Test]
    public async Task LlmStreamErrorException_CarriesKindAndStatus()
    {
        var err = new ErrorEvent("OpenAI API error 429: slow down", Kind: ProviderErrorKind.RateLimit, StatusCode: 429);
        var ex = new AgentLoop.LlmStreamErrorException(err);

        await Assert.That(ex.Kind).IsEqualTo(ProviderErrorKind.RateLimit);
        await Assert.That(ex.StatusCode).IsEqualTo(429);
        await Assert.That(ex.Message).IsEqualTo("OpenAI API error 429: slow down");
    }

    [Test]
    public async Task IsTypedStreamErrorException_RateLimit_IsTransient()
    {
        var ex = new AgentLoop.LlmStreamErrorException(
            new ErrorEvent("API error 503", Kind: ProviderErrorKind.ServerError, StatusCode: 503));

        await Assert.That(RetryPolicy.IsTransient(ex, out _)).IsTrue();
    }

    [Test]
    public async Task IsTypedStreamErrorException_Auth_IsFatal()
    {
        var ex = new AgentLoop.LlmStreamErrorException(
            new ErrorEvent("Auth failed: set $OPENAI_API_KEY", Kind: ProviderErrorKind.Auth));

        await Assert.That(RetryPolicy.IsTransient(ex, out _)).IsFalse();
    }

    [Test]
    public async Task ExecuteAsync_RateLimitedStreamError_IsRetried()
    {
        var policy = new RetryPolicy();
        int calls = 0;

        await Assert.ThrowsAsync<AgentLoop.LlmStreamErrorException>(async () => await policy.ExecuteAsync<int>(
            _ =>
            {
                calls++;
                throw new AgentLoop.LlmStreamErrorException(
                    new ErrorEvent("429", Kind: ProviderErrorKind.RateLimit, StatusCode: 429));
            },
            Opts(max: 3, delayMs: 1), CancellationToken.None));

        await Assert.That(calls).IsEqualTo(3);
    }

    // ── attempt behaviour ──

    [Test]
    public async Task ExecuteAsync_TransientThenSuccess_ReturnsResult()
    {
        var policy = new RetryPolicy();
        int calls = 0;

        int result = await policy.ExecuteAsync(
            _ =>
            {
                calls++;
                return calls < 3
                    ? throw new HttpRequestException("connection reset")
                    : Task.FromResult(42);
            },
            Opts(max: 5), CancellationToken.None);

        await Assert.That(result).IsEqualTo(42);
        await Assert.That(calls).IsEqualTo(3);
    }

    [Test]
    public async Task ExecuteAsync_FatalException_PropagatesImmediately()
    {
        var policy = new RetryPolicy();
        int calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await policy.ExecuteAsync<int>(
            _ =>
            {
                calls++;
                throw new InvalidOperationException();
            },
            Opts(max: 5), CancellationToken.None));

        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteAsync_ExhaustsAttempts_ThrowsLast()
    {
        var policy = new RetryPolicy();
        int calls = 0;

        await Assert.ThrowsAsync<HttpRequestException>(async () => await policy.ExecuteAsync<HttpResponseMessage>(
            _ =>
            {
                calls++;
                throw new HttpRequestException("reset", inner: null, HttpStatusCode.BadGateway);
            },
            Opts(max: 3), CancellationToken.None));

        await Assert.That(calls).IsEqualTo(3);
    }

    [Test]
    public async Task ExecuteAsync_OnRetryCallback_SequenceAndExceptions()
    {
        var policy = new RetryPolicy();
        var seen = new List<(int Attempt, string Kind)>();

        await Assert.ThrowsAsync<TaskCanceledException>(async () => await policy.ExecuteAsync<HttpResponseMessage>(
            _ => throw new TaskCanceledException(),
            Opts(max: 3),
            onRetry: (ex, attempt) => seen.Add((attempt, ex.GetType().Name)),
            CancellationToken.None));

        await Assert.That(seen.Count).IsEqualTo(2);
        await Assert.That(seen[0].Attempt).IsEqualTo(1);
        await Assert.That(seen[1].Attempt).IsEqualTo(2);
    }

    [Test]
    public async Task ExecuteAsync_Jitter_DelayNeverExceedsBaseDelay()
    {
        var policy = new RetryPolicy();
        var options = Opts(max: 4, delayMs: 40, jitter: true);
        var delays = new List<double>();
        long lastTicks = 0;

        try
        {
            await policy.ExecuteAsync<HttpResponseMessage>(
                _ => throw new HttpRequestException("reset", inner: null, HttpStatusCode.ServiceUnavailable),
                options,
                onRetry: (_, _) =>
                {
                    long now = Environment.TickCount64;
                    if (lastTicks != 0)
                    {
                        delays.Add(now - lastTicks);
                    }
                    lastTicks = now;
                },
                CancellationToken.None);
        }
        catch (HttpRequestException)
        {
            // expected exhaustion
        }

        // Every observed inter-retry gap must stay under baseDelay + slack
        // (jitter draws from [0, BaseDelay)); scheduling adds a little slack.
        foreach (double ms in delays)
        {
            await Assert.That(ms).IsLessThanOrEqualTo(40 * 2 + 60);
        }
        await Assert.That(delays.Count).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task ExecuteAsync_NoJitter_DelayApproximatesBaseDelay()
    {
        var policy = new RetryPolicy();
        var gaps = new List<long>();
        long last = 0;

        try
        {
            await policy.ExecuteAsync<HttpResponseMessage>(
                _ => throw new HttpRequestException("timeout", inner: null, HttpStatusCode.RequestTimeout),
                Opts(max: 3, delayMs: 80),
                onRetry: (_, _) =>
                {
                    long now = Environment.TickCount64;
                    if (last != 0) gaps.Add(now - last);
                    last = now;
                },
                CancellationToken.None);
        }
        catch (HttpRequestException) { /* exhausted */ }

        // Without jitter the delay is exactly BaseDelay; timer granularity is
        // coarse but never shorter and rarely more than +50ms.
        foreach (long g in gaps)
        {
            await Assert.That(g).IsGreaterThanOrEqualTo(75);
            await Assert.That(g).IsLessThan(200);
        }
        // max=3 attempts → 2 retries → the FIRST callback primes the clock,
        // only the SECOND yields a measurable gap.
        await Assert.That(gaps.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Cancellation_BetweenAttempts_StopsRetrying()
    {
        var policy = new RetryPolicy();
        using var cts = new CancellationTokenSource();
        int calls = 0;

        // The retry loop refuses to retry once ct is cancelled: the ORIGINAL
        // exception propagates (no synthetic OperationCanceledException), and
        // exactly one attempt was made.
        await Assert.ThrowsAsync<HttpRequestException>(async () => await policy.ExecuteAsync<HttpResponseMessage>(
            _ =>
            {
                calls++;
                cts.Cancel(); // cancel while "deciding"
                throw new HttpRequestException("reset", inner: null, HttpStatusCode.ServiceUnavailable);
            },
            Opts(max: 5),
            ct: cts.Token));

        await Assert.That(calls).IsEqualTo(1);
    }
}

/// <summary>
///     ROP-B П.21: ExecuteSafeAsync is the seam between the exception-based
///     retry policy and railway code — exceptions become Failure, retries
///     still fire for transient errors, cancellation propagates.
/// </summary>
public class RetryPolicySafeAdapterTests
{
    private static RetryOptions Opts(int max, int delayMs = 1) =>
        new(max, TimeSpan.FromMilliseconds(delayMs), UseJitter: false);

    [Test]
    public async Task ExecuteSafeAsync_Success_ReturnsValue()
    {
        var result = await new RetryPolicy().ExecuteSafeAsync(
            _ => Task.FromResult(42), Opts(3), CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo(42);
    }

    [Test]
    public async Task ExecuteSafeAsync_TransientThenSuccess_RetriesAndSucceeds()
    {
        int calls = 0;

        var result = await new RetryPolicy().ExecuteSafeAsync<object?>(
            _ =>
            {
                calls++;
                return calls < 3
                    ? throw new HttpRequestException("503", inner: null, HttpStatusCode.ServiceUnavailable)
                    : Task.FromResult<object?>(new object());
            },
            Opts(max: 5), CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(calls).IsEqualTo(3);
    }

    [Test]
    public async Task ExecuteSafeAsync_FatalError_BecomesFailureWithoutRetry()
    {
        int calls = 0;

        var result = await new RetryPolicy().ExecuteSafeAsync<string>(
            _ =>
            {
                calls++;
                throw new HttpRequestException("401", inner: null, HttpStatusCode.Unauthorized);
            },
            Opts(max: 5), CancellationToken.None);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).IsEqualTo("401");
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteSafeAsync_CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.That(async () => await new RetryPolicy().ExecuteSafeAsync<string>(
            _ => Task.FromResult("unused"), Opts(3), cts.Token)
        ).Throws<OperationCanceledException>();
    }
}
