using System.Threading;
using System.Threading.Tasks;

namespace Harbor.Application.Resilience;

public interface IRetryPolicy
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, RetryOptions options, CancellationToken ct);

    /// <summary>
    ///     Execute <paramref name="operation" /> with retry. Invokes
    ///     <paramref name="onRetry" /> between attempts with the transient
    ///     exception and the 1-based attempt number that failed — a hook for
    ///     logging/publishing without coupling the policy to a logger.
    /// </summary>
    Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        RetryOptions options,
        Action<Exception, int>? onRetry,
        CancellationToken ct)
        => ExecuteAsync(operation, options, ct);

    /// <summary>
    ///     Execute <paramref name="operation" /> with retry, where the
    ///     operation reports an expected failure as a VALUE (#1514) instead of
    ///     throwing it. The policy reads the failure's transport
    ///     classification to decide whether another attempt is worth it, and
    ///     the final verdict comes back as a failure — no exception crosses
    ///     the attempt boundary, so the caller routes on a value rather than
    ///     on a catch.
    /// </summary>
    /// <remarks>
    ///     Same budget, same backoff mechanics and same rate-limit split as
    ///     the exception-shaped overload: the difference is where the failure
    ///     is read from, not how long the policy waits.
    /// </remarks>
    /// <param name="operation">The attempt. Returns the attempt's verdict.</param>
    /// <param name="options">The retry budget and the backoff roots.</param>
    /// <param name="onRetry">
    ///     Invoked between attempts with the failure and the 1-based attempt
    ///     number that failed — a hook for logging/publishing without
    ///     coupling the policy to a logger.
    /// </param>
    /// <param name="ct">Cancellation.</param>
    Task<Result<T, StreamFailure>> ExecuteValueAsync<T>(
        Func<CancellationToken, Task<Result<T, StreamFailure>>> operation,
        RetryOptions options,
        Action<StreamFailure, int>? onRetry,
        CancellationToken ct);
}
