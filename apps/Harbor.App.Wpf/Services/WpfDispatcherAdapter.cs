using System.Windows.Threading;
using Harbor.Abstractions.Events;

namespace Harbor.App.Wpf.Services;

/// <summary>
///     Marshals agent-loop callbacks (which arrive on background threads) onto
///     the WPF UI thread via the <see cref="Dispatcher" />. Implements the
///     Adapter pattern over Harbor's <c>IEventBus</c>.
/// </summary>
public sealed class WpfDispatcherAdapter : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherPriority _priority;
    private bool _disposed;

    /// <summary>
    ///     Construct a <see cref="WpfDispatcherAdapter" /> bound to the current
    ///     dispatcher (must be called from the UI thread).
    /// </summary>
    /// <param name="priority">Priority used for BeginInvoke calls.</param>
    public WpfDispatcherAdapter(DispatcherPriority priority = DispatcherPriority.Background)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _priority = priority;
    }

    /// <summary>
    ///     Get whether the calling thread is the UI thread.
    /// </summary>
    public bool CheckAccess() => _dispatcher.CheckAccess();

    /// <summary>
    ///     Invoke an action on the UI thread synchronously. Use sparingly —
    ///     prefer <see cref="PostAsync{T}" /> for fire-and-forget events.
    /// </summary>
    /// <param name="action">The action to invoke.</param>
    public void Invoke(Action action)
    {
        if (_dispatcher.CheckAccess())
            action();
        else
            _dispatcher.Invoke(action, _priority);
    }

    /// <summary>
    ///     Post an action to the UI thread asynchronously (fire-and-forget).
    /// </summary>
    /// <param name="action">The action to post.</param>
    public void Post(Action action)
    {
        if (_disposed) return;
        _dispatcher.BeginInvoke(action, _priority);
    }

    /// <summary>
    ///     Marshal an <see cref="AgentEvent" /> to the UI thread and await its
    ///     processing. Returns a completed task — the actual handler runs on
    ///     the UI thread.
    /// </summary>
    /// <typeparam name="T">The return type (unused — kept for API symmetry).</typeparam>
    /// <param name="event">The event payload.</param>
    /// <param name="handler">The UI-thread handler.</param>
    /// <returns>A completed task.</returns>
    public Task PostAsync<T>(AgentEvent @event, Action<AgentEvent> handler)
    {
        if (_disposed) return Task.CompletedTask;
        _dispatcher.BeginInvoke(new Action(() => handler(@event)), _priority);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
    }
}
