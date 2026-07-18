using Avalonia.Threading;
using Harbor.Ui.Framework.State;

namespace Harbor.App.Avalonia.Services;

/// <summary>
///     Adapter that marshals <see cref="UiStore.Changed"/> events onto the Avalonia UI thread.
///     The reducer emits from any thread (the agent loop runs on the thread pool); the
///     ViewModels subscribe to <see cref="OnUiThread"/> which always fires on the UI thread.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="Bind"/> is <b>idempotent</b>: binding the same store twice is a no-op,
///         and binding a different store first unsubscribes from the previous one. This
///         prevents duplicate subscriptions when multiple ViewModels call Bind during their
///         construction (the previous design would subscribe once per ViewModel, multiplying
///         every state transition N times).
///     </para>
///     <para>
///         <see cref="Bind"/> should be called exactly once, from the composition root
///         (<c>AppHost.BuildAsync</c>), after the <see cref="UiStore"/> singleton is
///         available. ViewModels subscribe only to <see cref="OnUiThread"/>.
///     </para>
/// </remarks>
public sealed class AvaloniaDispatcherAdapter
{
    private UiStore? _boundStore;
    private readonly EventHandler<UiStateChangedEventArgs> _onStoreChanged;

    /// <summary>Construct the adapter.</summary>
    public AvaloniaDispatcherAdapter()
    {
        // Cache the handler delegate so we can -= the exact same instance on rebind.
        _onStoreChanged = OnStoreChanged;
    }

    /// <summary>Raised on the UI thread whenever the UiStore transitions.</summary>
    public event EventHandler<UiState>? OnUiThread;

    /// <summary>
    ///     Subscribe to <see cref="UiStore.Changed"/> and forward every transition to
    ///     <see cref="OnUiThread"/> via <see cref="Dispatcher.UIThread.Post"/>. Idempotent:
    ///     calling <see cref="Bind"/> with the same store instance is a no-op, and calling
    ///     it with a different store first unsubscribes from the previous one.
    /// </summary>
    /// <param name="store">The UiStore to subscribe to.</param>
    public void Bind(UiStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (ReferenceEquals(_boundStore, store))
        {
            // Already bound — no-op. Prevents double subscription when multiple
            // ViewModels call Bind during their construction.
            return;
        }

        if (_boundStore is not null)
        {
            _boundStore.Changed -= _onStoreChanged;
        }

        _boundStore = store;
        store.Changed += _onStoreChanged;
    }

    private void OnStoreChanged(object? sender, UiStateChangedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            OnUiThread?.Invoke(this, e.State);
        }
        else
        {
            Dispatcher.UIThread.Post(() => OnUiThread?.Invoke(this, e.State));
        }
    }
}
