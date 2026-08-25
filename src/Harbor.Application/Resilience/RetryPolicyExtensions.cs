using Harbor.Abstractions.Results;
namespace Harbor.Application.Resilience;

/// <summary>
///     ROP-B П.21 — the retry-vs-compensation boundary, as code:
///     <list type="bullet">
///         <item>"repeat the SAME operation with backoff" →
///             <see cref="IRetryPolicy.ExecuteAsync{T}(Func{CancellationToken,Task{T}},RetryOptions,CancellationToken)" />
///             (exception-based: transient classification, Retry-After, jitter);</item>
///         <item>"if it failed — do something DIFFERENT" → CSE <c>Compensate</c> on the
///             <see cref="Result{T}" /> this adapter hands back (see AuthStore.GetApiKeyAsync,
///             CompactionService secondary fallback).</item>
///     </list>
///     Exceptions must not leak into railway code and Result must not emulate
///     backoff loops — this adapter is the only seam between the two worlds.
/// </summary>
public static class RetryPolicyExtensions
{
    /// <summary>
    ///     Execute a retrying operation and surface its outcome as a railway
    ///     <see cref="Result{T}" />. Cancellation propagates
    ///     (<see cref="ResultErrors.Message" />) so Esc semantics stay intact;
    ///     every other exception becomes <c>Failure(ex.Message)</c> after the
    ///     policy exhausted its attempts.
    /// </summary>
    public static Task<Result<T>> ExecuteSafeAsync<T>(
        this IRetryPolicy policy,
        Func<CancellationToken, Task<T>> operation,
        RetryOptions options,
        Action<Exception, int>? onRetry,
        CancellationToken ct) =>
        Result.Try(() => policy.ExecuteAsync(operation, options, onRetry, ct), ResultErrors.Message);

    /// <summary>Same as the full overload without the per-attempt hook.</summary>
    public static Task<Result<T>> ExecuteSafeAsync<T>(
        this IRetryPolicy policy,
        Func<CancellationToken, Task<T>> operation,
        RetryOptions options,
        CancellationToken ct) =>
        policy.ExecuteSafeAsync(operation, options, onRetry: null, ct);
}
