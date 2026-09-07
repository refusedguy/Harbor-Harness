namespace Harbor.Application.Resilience;

public interface IRetryPolicy
{
    public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, RetryOptions options, CancellationToken ct);

    /// <summary>
    ///     Execute <paramref name="operation" /> with retry. Invokes
    ///     <paramref name="onRetry" /> between attempts with the transient
    ///     exception and the 1-based attempt number that failed — a hook for
    ///     logging/publishing without coupling the policy to a logger.
    /// </summary>
    public Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        RetryOptions options,
        Action<Exception, int>? onRetry,
        CancellationToken ct)
        => ExecuteAsync(operation, options, ct);
}
