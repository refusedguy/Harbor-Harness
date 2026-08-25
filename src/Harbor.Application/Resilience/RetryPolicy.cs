using System.Net;
using System.Threading;
using Harbor.Abstractions.Events;
using Harbor.Core.Agents;

namespace Harbor.Core.Resilience;

/// <summary>
///     Default retry policy. Retries only <b>transient</b> failures; fatal
///     failures (auth/quota rejections, caller cancellation) propagate
///     immediately.
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
                // surfaced one; otherwise use the configured backoff delay.
                TimeSpan delay = retryAfter ?? ComputeDelay(options);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    private static TimeSpan ComputeDelay(RetryOptions options)
    {
        return options.UseJitter
            ? TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * options.BaseDelay.TotalMilliseconds)
            : options.BaseDelay;
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
