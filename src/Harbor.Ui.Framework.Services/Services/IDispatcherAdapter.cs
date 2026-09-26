namespace Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
/// <summary>
///     Abstraction for marshalling work to the UI thread.
///     Each desktop app implements this (Avalonia → Dispatcher.UIThread,
///     WPF → Dispatcher, MAUI → MainThread, Blazor → InvokeAsync).
/// </summary>
public interface IDispatcherAdapter
{
    /// <summary>Post an action to the UI thread (non-blocking).</summary>
    public void Post(Action action);

    /// <summary>Run an action on the UI thread and wait for completion.</summary>
    public T Invoke<T>(Func<T> func);

    /// <summary>Subscribe to state changes from a UiStore.</summary>
    public void Bind(UiStore store);

    /// <summary>Unsubscribe from state changes for a UiStore.</summary>
    public void Unbind(UiStore store);

    /// <summary>
    ///     Raised on the UI thread whenever the bound UiStore transitions.
    ///     ViewModels subscribe to this instead of the concrete type's event.
    /// </summary>
    public event EventHandler<UiState>? StateChanged;
}
