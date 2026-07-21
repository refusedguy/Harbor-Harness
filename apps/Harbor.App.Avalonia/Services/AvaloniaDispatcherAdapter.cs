using Avalonia.Threading;
using Harbor.Ui.Framework.State;
namespace Harbor.App.Avalonia.Services;
/// <summary>
///     Adapter that marshals <see cref="UiStore.Changed" /> events onto the
///     Avalonia UI thread AND implements <see cref="IDispatcherAdapter" /> for
///     general UI-thread marshalling (Post / Invoke). The reducer emits from
///     any thread (the agent loop runs on the thread pool); the ViewModels
///     subscribe to <see cref="OnUiThread" /> which always fires on the UI
///     thread, or call <see cref="Post" /> / <see cref="Invoke{T}" /> directly
///     to marshal arbitrary work.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="Bind" /> is <b>idempotent</b>: binding the same store
///         twice is a no-op, and binding a different store first unsubscribes
///         from the previous one. This prevents duplicate subscriptions when
///         multiple ViewModels call Bind during their construction (the
///         previous design would subscribe once per ViewModel, multiplying
///         every state transition N times).
///     </para>
///     <para>
///         <see cref="Bind" /> should be called exactly once, from the
///         composition root (<c>AppHost.BuildAsync</c>), after the
///         <see cref="UiStore" /> singleton is available. ViewModels subscribe
///         only to <see cref="OnUiThread" />.
///     </para>
///     <para>
///         <b>Movability:</b> by exposing <see cref="IDispatcherAdapter" />,
///         ViewModels can be moved to <c>Harbor.Ui.Framework</c> (which has
///         no Avalonia dependency) by injecting the interface instead of the
///         concrete type. The <see cref="OnUiThread" /> event is the only
///         platform-specific surface still on this concrete type — future
///         work will surface it on a framework-side interface so VMs can
///         subscribe without depending on this class.
///     </para>
/// </remarks>
public sealed class AvaloniaDispatcherAdapter : IDispatcherAdapter
{
    private readonly EventHandler<UiStateChangedEventArgs> _onStoreChanged;

    /// <summary>Construct the adapter.</summary>
    public AvaloniaDispatcherAdapter()
    {
        // Cache the handler delegate so we can -= the exact same instance on rebind.
        _onStoreChanged = OnStoreChanged;
    }

    /// <summary>
    ///     The UiStore currently subscribed (or <c>null</c> if unbound).
    ///     Exposed so <see cref="SessionManager" /> can decide whether a
    ///     <see cref="Bind" /> call is a no-op (already bound) or an actual
    ///     rebind (different store → must trigger ChatViewModel replay).
    /// </summary>
    public UiStore? BoundStore
    {
        get;
        private set;
    }

    /// <inheritdoc />
    void IDispatcherAdapter.Bind(object store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (store is UiStore uiStore)
        {
            Bind(uiStore);
            return;
        }
        throw new ArgumentException(
            $"Expected UiStore instance, got {store.GetType().FullName}.",
            nameof(store));
    }

    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Dispatcher.UIThread.Post(action);
    }

    /// <inheritdoc />
    public T Invoke<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        // Dispatcher.UIThread.InvokeAsync returns a Task<T> that we wait on
        // synchronously — ViewModels call this from non-UI threads expecting
        // a blocking invoke (matches WPF Dispatcher.Invoke semantics).
        return Dispatcher.UIThread.InvokeAsync(func).GetAwaiter().GetResult();
    }

    /// <summary>Raised on the UI thread whenever the UiStore transitions.</summary>
    public event EventHandler<UiState>? OnUiThread;

    /// <summary>
    ///     Subscribe to <see cref="UiStore.Changed" /> and forward every
    ///     transition to <see cref="OnUiThread" /> via
    ///     <see cref="Dispatcher.UIThread.Post" />. Idempotent: calling
    ///     <see cref="Bind" /> with the same store instance is a no-op, and
    ///     calling it with a different store first unsubscribes from the
    ///     previous one.
    /// </summary>
    /// <param name="store">The UiStore to subscribe to.</param>
    public void Bind(UiStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (ReferenceEquals(BoundStore, store))
        {
            // Already bound — no-op. Prevents double subscription when multiple
            // ViewModels call Bind during their construction.
            return;
        }

        if (BoundStore is not null)
        {
            BoundStore.Changed -= _onStoreChanged;
        }

        BoundStore = store;
        store.Changed += _onStoreChanged;
    }

    /// <summary>
    ///     Unsubscribe from the given <paramref name="store" /> if and only
    ///     if it is currently the bound store. No-op otherwise. Used by
    ///     <see cref="ViewModels.ChatViewModel.RebindToStore" /> during
    ///     session switching to detach the dispatcher from the previous
    ///     session's UiStore before binding the new session's.
    /// </summary>
    /// <param name="store">
    ///     The store to unbind. If not the currently-bound
    ///     store, this is a no-op.
    /// </param>
    public void Unbind(UiStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!ReferenceEquals(BoundStore, store)) return;
        store.Changed -= _onStoreChanged;
        BoundStore = null;
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
