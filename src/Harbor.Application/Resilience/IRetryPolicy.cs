namespace Harbor.Core.Resilience;

public interface IRetryPolicy
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, RetryOptions options, CancellationToken ct);
}