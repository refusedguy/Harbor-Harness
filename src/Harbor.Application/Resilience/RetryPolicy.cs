using System.Globalization;
using System.Net;
using System.Threading;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Resilience;
using Harbor.Application.Agents;

namespace Harbor.Application.Resilience;

/// <summary>
///     Default retry policy. Retries only <b>transient</b> failures; fatal
///     failures (auth/quota rejections, caller cancellation) propagate
///     immediately. Between attempts the policy sleeps an exponentially
///     growing backoff — <c>BaseDelay · 2^(attempt − 1)</c>, capped at
///     <see cref="MaxBackoff" /> — optionally flattened by jitter so
///     synchronized callers do not form retry waves.
/// </summary>
/// <remarks>
///     <para>
///         <b>Transient:</b> <see cref="HttpRequestException" /> with no status
///         code (network-level failure), HTTP 408, HTTP 429 and any 5xx.
///         A <see cref="TaskCanceledException" /> raised while the caller's
///         token is NOT cancelled represents a provider timeout and is retried.
///         <see cref="IOException" /> and <see cref="TimeoutException" /> are
///         transient too (#572) — the same physical failure as a status-less
///         <see cref="HttpRequestException" />, judged by
///         <see cref="TransientFailurePolicy.ShouldRetry" /> so the remote-MCP
///         transports and this policy cannot disagree about it again.
///     </para>
///     <para>
///         <b>Fatal:</b> HTTP 401/403/400/404/409/422-style client errors
///         (retrying cannot fix a bad key or bad request), plain
///         <see cref="OperationCanceledException" />, caller cancellation
///         (<c>ct.IsCancellationRequested</c>), and everything else.
///     </para>
///     <para>
///         <b>Retry-After (#270):</b> when the failure carries a server hint —
///         <see cref="IRetryAfterHint" /> or <c>Exception.Data["RetryAfter"]</c>
///         (<c>TimeSpan</c>, seconds as a number, seconds/HTTP-date as text) —
///         the policy sleeps it (clamped to
///         <see cref="RetryOptions.EffectiveMaxRetryAfter" />) instead of the
///         computed backoff. <see cref="HttpRequestException" /> itself carries
///         no response headers, so without such a channel the policy delay
///         applies.
///     </para>
///     <para>
///         <b>Rate-limit budget (#270):</b> HTTP 429 / <c>RateLimit</c> failures
///         draw from <see cref="RetryOptions.EffectiveRateLimitAttempts" />
///         with <see cref="RetryOptions.EffectiveRateLimitBaseDelay" />-rooted
///         backoff (capped at two minutes per sleep), not from the general
///         <see cref="RetryOptions.MaxAttempts" /> budget — 429 storms need
///         minutes-scale patience, not seconds.
///     </para>
/// </remarks>
public sealed class RetryPolicy : IRetryPolicy
{
    private readonly TimeProvider _time;

    /// <summary>
    ///     Construct with an explicit clock (#54). Production uses
    ///     <see cref="TimeProvider.System" />; tests pass a recording fake so
    ///     no wall-clock assertion can flake on loaded runners.
    /// </summary>
    public RetryPolicy(TimeProvider? timeProvider = null) =>
        _time = timeProvider ?? TimeProvider.System;
    /// <summary>
    ///     Upper bound for the scaled exponential backoff: late attempts stop
    ///     growing past this ceiling regardless of the attempt counter.
    /// </summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Upper bound for one rate-limit backoff sleep (#270): 429 storms are
    ///     weathered with minutes-scale patience, while a single sleep never
    ///     parks the run for longer than this.
    /// </summary>
    private static readonly TimeSpan MaxRateLimitBackoff = TimeSpan.FromMinutes(2);

    public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, RetryOptions options, CancellationToken ct)
    {
        return ExecuteAsync(operation, options, onRetry: null, ct);
    }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        RetryOptions options,
        Action<Exception, int>? onRetry,
        CancellationToken ct)
    {
        if (operation is null) throw new ArgumentNullException(nameof(operation));
        if (options.MaxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.EffectiveRateLimitAttempts < 1) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.BaseDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));

        int attempt = 0;
        int rateLimitAttempts = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;

            try
            {
                return await operation(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
                when (!ct.IsCancellationRequested
                      && IsTransient(ex, out TimeSpan? retryAfter))
            {
                // Rate limits use a dedicated minutes-scale budget while all other
                // transients share the general one (see #270).
                bool rateLimited = IsRateLimit(ex);
                int consumed;
                int limit;
                if (rateLimited)
                {
                    rateLimitAttempts++;
                    consumed = rateLimitAttempts;
                    limit = options.EffectiveRateLimitAttempts;
                }
                else
                {
                    consumed = attempt;
                    limit = options.MaxAttempts;
                }

                if (consumed >= limit)
                    throw;

                onRetry?.Invoke(ex, attempt);

                // Prefer the server-provided retry hint when the classifier
                // surfaced one; otherwise use the exponentially scaled backoff
                // (rate-limit-rooted for 429s).
                TimeSpan delay = retryAfter is { } hint
                    ? ClampRetryAfter(hint, options)
                    : rateLimited
                        ? ComputeRateLimitDelay(options, rateLimitAttempts)
                        : ComputeDelay(options, attempt);
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     Exponential backoff for the retry that follows the failure of
    ///     attempt <paramref name="failedAttempt" />:
    ///     <c>BaseDelay · 2^(attempt − 1)</c>, capped at <see cref="MaxBackoff" />.
    ///     With jitter enabled the delay is drawn uniformly from
    ///     <c>[0, target)</c> — full jitter — so concurrent callers that fail
    ///     together de-synchronize instead of forming retry waves.
    /// </summary>
    /// <remarks>
    ///     Public so the tool-dispatch retry loop (#43) shares the exact same
    ///     backoff mechanics instead of duplicating the formula: decision lives
    ///     in <c>IToolRetryDecider</c>, computation lives here.
    /// </remarks>
    public static TimeSpan ComputeDelay(RetryOptions options, int failedAttempt)
    {
        double target = Math.Min(
            options.BaseDelay.TotalMilliseconds * Math.Pow(2, failedAttempt - 1),
            MaxBackoff.TotalMilliseconds);

        return options.UseJitter
            ? TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * target)
            : TimeSpan.FromMilliseconds(target);
    }

    /// <summary>
    ///     Exponential backoff for the retry that follows a rate-limit failure
    ///     (#270): <c>EffectiveRateLimitBaseDelay · 2^(attempt − 1)</c>, capped
    ///     at <see cref="MaxRateLimitBackoff" /> (minutes-scale, not seconds).
    ///     Jitter semantics match <see cref="ComputeDelay" />. The tool-dispatch
    ///     loop keeps using <see cref="ComputeDelay" /> — tool 429s stay on the
    ///     conservative single-retry path by design.
    /// </summary>
    public static TimeSpan ComputeRateLimitDelay(RetryOptions options, int failedRateLimitAttempt)
    {
        if (failedRateLimitAttempt < 1) throw new ArgumentOutOfRangeException(nameof(failedRateLimitAttempt));

        double target = Math.Min(
            options.EffectiveRateLimitBaseDelay.TotalMilliseconds * Math.Pow(2, failedRateLimitAttempt - 1),
            MaxRateLimitBackoff.TotalMilliseconds);

        return options.UseJitter
            ? TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * target)
            : TimeSpan.FromMilliseconds(target);
    }

    /// <summary>
    ///     Clamp a server-provided Retry-After hint into
    ///     <c>[0, <see cref="RetryOptions.EffectiveMaxRetryAfter" />]</c> so a
    ///     rogue header can neither spin the loop nor park the run forever.
    /// </summary>
    public static TimeSpan ClampRetryAfter(TimeSpan retryAfter, RetryOptions options)
    {
        if (retryAfter < TimeSpan.Zero)
            return TimeSpan.Zero;
        TimeSpan cap = options.EffectiveMaxRetryAfter;
        return retryAfter > cap ? cap : retryAfter;
    }

    /// <summary>
    ///     Classify an exception as transient (<see langword="true" />, may be
    ///     retried) or fatal (<see langword="false" />, propagate immediately).
    ///     Dispatch is a strategy chain (<see cref="IExceptionClassifier" />):
    ///     a new transient failure type adds a classifier; the Retry-After
    ///     side-channel (#270) is consulted centrally here, never per classifier.
    /// </summary>
    public static bool IsTransient(Exception ex, out TimeSpan? retryAfter)
    {
        retryAfter = null;

        foreach (var classifier in Classifiers)
        {
            if (classifier.TryClassify(ex, out bool transient, out retryAfter))
            {
                // #270: the classifiers read the wire shape; the Retry-After
                // hint rides out-of-band (no provider propagates headers yet —
                // verified by audit), so consult the side-channel here rather
                // than in every classifier.
                if (transient && retryAfter is null && TryGetRetryAfter(ex, out TimeSpan hint))
                    retryAfter = hint;
                return transient;
            }
        }

        return false;
    }

    /// <summary>
    ///     Whether the failure is a rate limit (HTTP 429 / provider
    ///     <c>RateLimit</c> kind): such failures draw from the separate
    ///     minutes-scale budget instead of the general transient budget.
    /// </summary>
    public static bool IsRateLimit(Exception ex) => ex switch
    {
        HttpRequestException hre => hre.StatusCode == HttpStatusCode.TooManyRequests,
        LlmStreamErrorException streamError => streamError.Kind == ProviderErrorKind.RateLimit,
        _ => false,
    };

    /// <summary>
    ///     Read a server-provided Retry-After hint carried out-of-band on the
    ///     exception (#270): <see cref="IRetryAfterHint" /> first, then
    ///     <c>Exception.Data["RetryAfter"]</c> / <c>"Retry-After"</c> as
    ///     <c>TimeSpan</c>, seconds as a number, or seconds/HTTP-date as text.
    ///     Negative or unparseable values are ignored. The result is unclamped;
    ///     apply <see cref="ClampRetryAfter" /> before sleeping.
    /// </summary>
    public static bool TryGetRetryAfter(Exception ex, out TimeSpan retryAfter)
    {
        retryAfter = default;
        if (ex is IRetryAfterHint hint && hint.RetryAfter is { } hinted && hinted >= TimeSpan.Zero)
        {
            retryAfter = hinted;
            return true;
        }

        if (ex.Data.Contains("RetryAfter") && ToRetryAfter(ex.Data["RetryAfter"], out retryAfter))
            return true;
        return ex.Data.Contains("Retry-After") && ToRetryAfter(ex.Data["Retry-After"], out retryAfter);
    }

    private static bool ToRetryAfter(object? value, out TimeSpan retryAfter)
    {
        retryAfter = default;
        switch (value)
        {
            case TimeSpan span when span >= TimeSpan.Zero:
                retryAfter = span;
                return true;
            case double seconds when double.IsFinite(seconds) && seconds >= 0:
                retryAfter = TimeSpan.FromSeconds(seconds);
                return true;
            case float seconds when float.IsFinite(seconds) && seconds >= 0:
                retryAfter = TimeSpan.FromSeconds(seconds);
                return true;
            case int seconds when seconds >= 0:
                retryAfter = TimeSpan.FromSeconds(seconds);
                return true;
            case long seconds when seconds >= 0:
                retryAfter = TimeSpan.FromSeconds(seconds);
                return true;
            case string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && parsed >= 0:
                retryAfter = TimeSpan.FromSeconds(parsed);
                return true;
            case string text when DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset date):
                retryAfter = date - DateTimeOffset.UtcNow;
                if (retryAfter < TimeSpan.Zero)
                    retryAfter = TimeSpan.Zero;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    ///     Strategy contract for one transient-failure family. Returns
    ///     <see langword="true" /> when the exception belongs to the family
    ///     (verdict in the out transient flag); <see langword="false" />
    ///     declines so the next classifier is consulted.
    /// </summary>
    internal interface IExceptionClassifier
    {
        bool TryClassify(Exception ex, out bool transient, out TimeSpan? retryAfter);
    }

    private sealed class CancellationClassifier : IExceptionClassifier
    {
        public bool TryClassify(Exception ex, out bool transient, out TimeSpan? retryAfter)
        {
            retryAfter = null;
            if (ex is not OperationCanceledException oce)
            {
                transient = false;
                return false;
            }

            // Caller cancellation is handled by the catch filter. A timeout
            // surfaces as TaskCanceledException without the caller's token
            // being cancelled — that class of cancellation IS transient.
            transient = oce is TaskCanceledException;
            return true;
        }
    }

    private sealed class HttpClassifier : IExceptionClassifier
    {
        public bool TryClassify(Exception ex, out bool transient, out TimeSpan? retryAfter)
        {
            retryAfter = null;
            if (ex is not HttpRequestException hre)
            {
                transient = false;
                return false;
            }

            transient = IsTransientStatus(hre.StatusCode, ref retryAfter);
            return true;
        }

        private static bool IsTransientStatus(HttpStatusCode? status, ref TimeSpan? retryAfter)
        {
            // No status code → failure below the HTTP layer (DNS, connection reset,
            // TLS): inherently transient.
            if (status is null)
            {
                return true;
            }

            int code = (int)status;
            if (code == 429)
            {
                // Rate-limited. HttpRequestException exposes no response headers, so
                // a wire-level Retry-After is not retrievable here — leave null and
                // let the caller fall back to the policy delay.
                retryAfter = null;
                return true;
            }

            return code == 408 || code >= 500;
        }
    }

    private sealed class StreamErrorClassifier : IExceptionClassifier
    {
        public bool TryClassify(Exception ex, out bool transient, out TimeSpan? retryAfter)
        {
            retryAfter = null;
            if (ex is not LlmStreamErrorException streamError)
            {
                transient = false;
                return false;
            }

            // ROP-A ПР.5: provider streams surface transport failures as
            // typed error events, not exceptions. The classification made
            // at the wire (429 / 5xx / timeout / network) rides on the
            // exception so retries promised by this policy actually fire.
            transient = IsTransient(streamError.Kind);
            return true;
        }
    }

    /// <summary>
    ///     The socket verdict (#572) — <see cref="IOException" /> and
    ///     <see cref="TimeoutException" /> — and the shared retry budget the
    ///     remote-MCP transports consume. This classifier is total and defers
    ///     entirely to <see cref="TransientFailurePolicy.ShouldRetry" />: it must
    ///     not restate the type set, or it becomes the fourth copy of the answer
    ///     this issue removed.
    /// </summary>
    private sealed class SocketClassifier : IExceptionClassifier
    {
        public bool TryClassify(Exception ex, out bool transient, out TimeSpan? retryAfter)
        {
            retryAfter = null;
            transient = TransientFailurePolicy.ShouldRetry(ex);
            return transient;
        }
    }

    private static readonly IExceptionClassifier[] Classifiers =
    [
        new CancellationClassifier(),
        new HttpClassifier(),
        new StreamErrorClassifier(),
        // #572: last on purpose. The three above own the more specific verdict
        // for their families; this one only answers for the exceptions they
        // decline, so a stream error or an HTTP failure is never re-judged here.
        new SocketClassifier(),
    ];

    /// <summary>
    ///     Retry verdict for a transport error kind classified at the provider
    ///     boundary (ROP-A ПР.5): rate limits, timeouts, network failures and
    ///     server overloads are transient; auth failures and malformed streams
    ///     are fatal.
    /// </summary>
    public static bool IsTransient(ProviderErrorKind kind) => ProviderErrors.IsTransient(kind);
}
