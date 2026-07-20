namespace Harbor.Ui.Framework.Services;

/// <summary>
///     Abstraction for marshalling work to the UI thread.
///     Each desktop app implements this (Avalonia → Dispatcher.UIThread,
///     WPF → Dispatcher, MAUI → MainThread, Blazor → InvokeAsync).
/// </summary>
public interface IDispatcherAdapter
{
    /// <summary>Post an action to the UI thread (non-blocking).</summary>
    void Post(Action action);
    
    /// <summary>Run an action on the UI thread and wait for completion.</summary>
    T Invoke<T>(Func<T> func);
    
    /// <summary>Bind to a UiStore — subscribe to Changed events.</summary>
    void Bind(object store);
}
