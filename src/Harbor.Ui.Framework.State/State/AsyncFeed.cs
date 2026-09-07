using CSharpFunctionalExtensions;
using Microsoft.Extensions.Logging;
namespace Harbor.Ui.Framework.State;

/// <summary>
///     Owner-scoped async data loader. Wraps a load function with timeout,
///     cancellation, and state tracking — replaces the repetitive
///     <c>CTS + try/catch/finally + IsLoading/ErrorMessage</c> boilerplate.
/// </summary>
public sealed class AsyncFeed<T> : IDisposable
{
    private readonly Func<CancellationToken, Task<Result<T>>> _load;
    private readonly ILogger? _logger;
    private readonly TimeSpan _timeout;
    private CancellationTokenSource? _cts;

    public AsyncFeed(
        Func<CancellationToken, Task<Result<T>>> load,
        TimeSpan timeout,
        ILogger? logger = null)
    {
        _load = load;
        _timeout = timeout;
        _logger = logger;
    }

    public AsyncData<T> Current { get; private set; } = AsyncData<T>.Idle;

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
    public event Action<AsyncData<T>>? Changed;

    public async Task RefreshAsync()
    {
        var old = _cts;
        _cts = new CancellationTokenSource(_timeout);
        old?.Cancel();
        old?.Dispose();

        Publish(Current.ToLoading());
        try
        {
            var result = await _load(_cts.Token).ConfigureAwait(true);
            Publish(AsyncData<T>.From(result));
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            Publish(AsyncData<T>.Failed($"Timed out after {_timeout.TotalSeconds:F0}s"));
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "AsyncFeed refresh failed");
            Publish(AsyncData<T>.Failed(ex.Message));
        }
    }

    private void Publish(AsyncData<T> data)
    {
        Current = data;
        Changed?.Invoke(data);
    }
}
