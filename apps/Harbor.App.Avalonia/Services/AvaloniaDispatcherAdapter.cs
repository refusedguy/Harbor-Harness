using Avalonia.Threading;

namespace Harbor.App.Avalonia.Services;

/// <summary>
///     Adapter that marshals <c>UiStore.Changed</c> events onto the Avalonia UI thread.
///     The reducer emits from any thread (the agent loop runs on the thread pool); the
///     ViewModels subscribe to <see cref="OnUiThread"/> which always fires on the UI thread.
/// </summary>
public sealed class AvaloniaDispatcherAdapter
{
    /// <summary>Raised on the UI thread whenever the UiStore transitions.</summary>
    public event EventHandler<Harbor.Tui.Abstractions.State.UiState>? OnUiThread;

    /// <summary>
    ///     Subscribe to <see cref="Harbor.Tui.Abstractions.State.UiStore.Changed"/> and forward
    ///     every transition to <see cref="OnUiThread"/> via <see cref="Dispatcher.UIThread.Post"/>.
    /// </summary>
    /// <param name="store">The UiStore to subscribe to.</param>
    public void Bind(Harbor.Tui.Abstractions.State.UiStore store)
    {
        store.Changed += (_, e) =>
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                OnUiThread?.Invoke(this, e.State);
            }
            else
            {
                Dispatcher.UIThread.Post(() => OnUiThread?.Invoke(this, e.State));
            }
        };
    }
}
