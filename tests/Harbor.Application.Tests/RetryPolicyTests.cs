using System.Net;
using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Application.Agents;
using Harbor.Application.Resilience;
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

    /// <summary>
    ///     Hermetic clock (#54): records requested delays, completes
    ///     immediately. No wall-clock is ever observed, so loaded runners
    ///     cannot perturb assertions. BCL only, no new packages.
    /// </summary>
    private sealed class RecordingTimeProvider : TimeProvider
    {
        public List<TimeSpan> Delays { get; } = [];

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;

        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Delays.Add(dueTime);
            // Complete synchronously: Task.Delay's callback sets the task
            // result, so the policy observes an instantly-elapsed delay.
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
        var ex = new LlmStreamErrorException(err);

        await Assert.That(ex.Kind).IsEqualTo(ProviderErrorKind.RateLimit);
        await Assert.That(ex.StatusCode).IsEqualTo(429);
        await Assert.That(ex.Message).IsEqualTo("OpenAI API error 429: slow down");
    }

    [Test]
    public async Task IsTypedStreamErrorException_RateLimit_IsTransient()
    {
        var ex = new LlmStreamErrorException(
            new ErrorEvent("API error 503", Kind: ProviderErrorKind.ServerError, StatusCode: 503));

        await Assert.That(RetryPolicy.IsTransient(ex, out _)).IsTrue();
    }

    [Test]
    public async Task IsTypedStreamErrorException_Auth_IsFatal()
    {
        var ex = new LlmStreamErrorException(
            new ErrorEvent("Auth failed: set $OPENAI_API_KEY", Kind: ProviderErrorKind.Auth));

        await Assert.That(RetryPolicy.IsTransient(ex, out _)).IsFalse();
    }

    [Test]
    public async Task ExecuteAsync_RateLimitedStreamError_IsRetried()
    {
        var policy = new RetryPolicy();
        int calls = 0;

        await Assert.ThrowsAsync<LlmStreamErrorException>(async () => await policy.ExecuteAsync<int>(
            _ =>
            {
                calls++;
                throw new LlmStreamErrorException(
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

    // Hermetic since #54-fix: delays are recorded by the fake clock, so no
    // wall-clock bound can overshoot on loaded runners. [Retry] removed.
    // NOTE: Task.Delay(TimeSpan, TimeProvider, ct) truncates sub-millisecond
    // delays to zero and skips the timer entirely — a jitter draw < 1ms is
    // simply not recorded. Retry COUNT is therefore proven via onRetry
    // (deterministic), delay VALUES via ComputeDelay unit tests below.
    [Test]
    public async Task ExecuteAsync_Jitter_DelayNeverExceedsBaseDelay()
    {
        var time = new RecordingTimeProvider();
        var policy = new RetryPolicy(time);
        var attempts = new List<int>();

        try
        {
            await policy.ExecuteAsync<HttpResponseMessage>(
                _ => throw new HttpRequestException("reset", inner: null, HttpStatusCode.ServiceUnavailable),
                Opts(max: 4, delayMs: 40, jitter: true),
                onRetry: (_, attempt) => attempts.Add(attempt),
                CancellationToken.None);
        }
        catch (HttpRequestException)
        {
            // expected exhaustion
        }

        // max=4 attempts → 3 retries, in order, regardless of timers.
        await Assert.That(attempts.Count).IsEqualTo(3);
        await Assert.That(attempts[0]).IsEqualTo(1);
        await Assert.That(attempts[1]).IsEqualTo(2);
        await Assert.That(attempts[2]).IsEqualTo(3);

        // Every delay that reached the clock is within the global cap
        // (largest per-attempt cap for this config: 40ms·2² = 160ms).
        foreach (TimeSpan delay in time.Delays)
        {
            await Assert.That(delay).IsGreaterThanOrEqualTo(TimeSpan.Zero);
            await Assert.That(delay).IsLessThan(TimeSpan.FromMilliseconds(160));
        }
    }

    [Test]
    public async Task ComputeDelay_NoJitter_IsExact()
    {
        var options = Opts(max: 5, delayMs: 40, jitter: false);
        await Assert.That(RetryPolicy.ComputeDelay(options, 1)).IsEqualTo(TimeSpan.FromMilliseconds(40));
        await Assert.That(RetryPolicy.ComputeDelay(options, 2)).IsEqualTo(TimeSpan.FromMilliseconds(80));
        await Assert.That(RetryPolicy.ComputeDelay(options, 3)).IsEqualTo(TimeSpan.FromMilliseconds(160));
    }

    [Test]
    public async Task ComputeDelay_Jitter_StaysWithinCap()
    {
        var options = Opts(max: 5, delayMs: 40, jitter: true);
        for (int i = 0; i < 1000; i++)
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                TimeSpan delay = RetryPolicy.ComputeDelay(options, attempt);
                await Assert.That(delay).IsGreaterThanOrEqualTo(TimeSpan.Zero);
                await Assert.That(delay).IsLessThan(TimeSpan.FromMilliseconds(40 * (1 << (attempt - 1))));
            }
        }
    }

    [Test]
    public async Task ExecuteAsync_NoJitter_DelayApproximatesBaseDelay()
    {
        var time = new RecordingTimeProvider();
        var policy = new RetryPolicy(time);

        try
        {
            await policy.ExecuteAsync<HttpResponseMessage>(
                _ => throw new HttpRequestException("timeout", inner: null, HttpStatusCode.RequestTimeout),
                Opts(max: 3, delayMs: 80),
                CancellationToken.None);
        }
        catch (HttpRequestException) { /* exhausted */ }

        // Without jitter every requested delay is exact: BaseDelay·2^(n-1).
        // max=3 attempts → 2 retries → [80ms, 160ms].
        await Assert.That(time.Delays.Count).IsEqualTo(2);
        await Assert.That(time.Delays[0]).IsEqualTo(TimeSpan.FromMilliseconds(80));
        await Assert.That(time.Delays[1]).IsEqualTo(TimeSpan.FromMilliseconds(160));
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
