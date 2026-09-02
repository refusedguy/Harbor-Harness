using System.Net;
using System.Threading;
using Harbor.Abstractions.Events;
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
///     </para>
///     <para>
///         <b>Fatal:</b> HTTP 401/403/400/404/409/422-style client errors
///         (retrying cannot fix a bad key or bad request), plain
///         <see cref="OperationCanceledException" />, caller cancellation
///         (<c>ct.IsCancellationRequested</c>), and everything else.
///     </para>
///     <para>
///         <b>Retry-After:</b> <see cref="HttpRequestException" /> does not carry
///         response headers, so the header value is not retrievable at this
///         layer; the policy delay is used instead. Providers that surface the
///         value can wrap it into their exception type and extend the classifier.
///     </para>
/// </remarks>
public sealed class RetryPolicy : IRetryPolicy
{
    /// <summary>
    ///     Upper bound for the scaled exponential backoff: late attempts stop
    ///     growing past this ceiling regardless of the attempt counter.
    /// </summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

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
        if (options.BaseDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));

        int attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;

            try
            {
                return await operation(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
                when (attempt < options.MaxAttempts
                      && !ct.IsCancellationRequested
                      && IsTransient(ex, out TimeSpan? retryAfter))
            {
                onRetry?.Invoke(ex, attempt);

                // Prefer the server-provided retry hint when the classifier
                // surfaced one; otherwise use the exponentially scaled backoff.
                TimeSpan delay = retryAfter ?? ComputeDelay(options, attempt);
                await Task.Delay(delay, ct).ConfigureAwait(false);
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
    private static TimeSpan ComputeDelay(RetryOptions options, int failedAttempt)
    {
        double target = Math.Min(
            options.BaseDelay.TotalMilliseconds * Math.Pow(2, failedAttempt - 1),
            MaxBackoff.TotalMilliseconds);

        return options.UseJitter
            ? TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * target)
            : TimeSpan.FromMilliseconds(target);
    }

    /// <summary>
    ///     Classify an exception as transient (<see langword="true" />, may be
    ///     retried) or fatal (<see langword="false" />, propagate immediately).
    /// </summary>
    public static bool IsTransient(Exception ex, out TimeSpan? retryAfter)
    {
        retryAfter = null;

        switch (ex)
        {
            case OperationCanceledException oce:
                // Caller cancellation is handled by the catch filter. A timeout
                // surfaces as TaskCanceledException without the caller's token
                // being cancelled — that class of cancellation IS transient.
                return oce is TaskCanceledException;

            case HttpRequestException hre:
                return IsTransientStatus(hre.StatusCode, ref retryAfter);

            case LlmStreamErrorException streamError:
                // ROP-A ПР.5: provider streams surface transport failures as
                // typed error events, not exceptions. The classification made
                // at the wire (429 / 5xx / timeout / network) rides on the
                // exception so retries promised by this policy actually fire.
                return IsTransient(streamError.Kind);

            default:
                return false;
        }
    }

    /// <summary>
    ///     Retry verdict for a transport error kind classified at the provider
    ///     boundary (ROP-A ПР.5): rate limits, timeouts, network failures and
    ///     server overloads are transient; auth failures and malformed streams
    ///     are fatal.
    /// </summary>
    public static bool IsTransient(ProviderErrorKind kind) => ProviderErrors.IsTransient(kind);

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
