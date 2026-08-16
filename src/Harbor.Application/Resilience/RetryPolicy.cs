using System.Threading;

namespace Harbor.Core.Resilience;

public sealed class RetryPolicy : IRetryPolicy
{
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, RetryOptions options, CancellationToken ct)
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
            catch when (attempt < options.MaxAttempts)
            {
                var delay = options.UseJitter
                    ? TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * options.BaseDelay.TotalMilliseconds)
                    : options.BaseDelay;
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }
}