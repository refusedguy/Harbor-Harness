namespace Harbor.Desktop.Abstractions.Services;

/// <summary>
///     Marshals work to the UI thread. Each platform implements this with its
///     own dispatcher primitive (Avalonia <c>Dispatcher.UIThread</c>,
///     WPF <c>Application.Current.Dispatcher</c>, MAUI <c>MainThread</c>,
///     Blazor <c>Dispatcher</c> from <c>Microsoft.AspNetCore.Components</c>).
/// </summary>
public interface IDispatcherAdapter
{
    /// <summary>True if the current thread is the UI thread.</summary>
    bool CheckAccess();

    /// <summary>
    ///     Run <paramref name="action"/> on the UI thread, blocking the caller
    ///     until it completes. Use <see cref="InvokeAsync"/> for non-blocking.
    /// </summary>
    void Invoke(Action action);

    /// <summary>
    ///     Schedule <paramref name="action"/> on the UI thread without waiting
    ///     for it to complete (fire-and-forget).
    /// </summary>
    void Post(Action action);

    /// <summary>
    ///     Run <paramref name="action"/> on the UI thread and complete the
    ///     returned task when it finishes.
    /// </summary>
    Task InvokeAsync(Action action);

    /// <summary>
    ///     Run <paramref name="func"/> on the UI thread and return its result.
    /// </summary>
    Task<T> InvokeAsync<T>(Func<T> func);
}
