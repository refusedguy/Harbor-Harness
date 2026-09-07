using CommunityToolkit.Mvvm.ComponentModel;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging;
namespace Harbor.Ui.Framework.ViewModels;

public abstract class StoreSubscriberViewModel : ObservableObject, IDisposable
{
    protected readonly IDispatcherAdapter Dispatcher;
    private readonly EventHandler<UiState> _onStoreChanged;

    private readonly List<ISelector> _selectors = new();

    protected StoreSubscriberViewModel(
        IDispatcherAdapter dispatcher,
        ILogger logger)
    {
        Dispatcher = dispatcher;
        Logger = logger;
        _onStoreChanged = (_, state) =>
        {
            OnStoreChanged(state);
            OnAfterSelectorsApplied(state);
        };
        Dispatcher.StateChanged += _onStoreChanged;
    }

    protected ILogger Logger { get; }

    public virtual void Dispose() => Dispatcher.StateChanged -= _onStoreChanged;

    protected abstract void OnStoreChanged(UiState state);

    /// <summary>
    ///     Called after all selectors have been applied and INPC notifications raised.
    ///     Override in platform-specific ViewModels to execute platform-specific logic
    ///     (e.g., Avalonia Dispatcher.UIThread.Post, WPF Dispatcher.Invoke).
    /// </summary>
    protected virtual void OnAfterSelectorsApplied(UiState state) {}

    /// <summary>
    ///     Declare a state→VM projection. Applied only when the slice actually changes.
    ///     Use inside <see cref="OnStoreChanged" /> to eliminate manual assignments.
    /// </summary>
    protected void Select<T>(Func<UiState, T> read, Action<T> apply, IEqualityComparer<T>? cmp = null)
        => _selectors.Add(new Selector<T>(read, apply, cmp));

    /// <summary>Apply all declared selectors against a state snapshot.</summary>
    protected void ApplySelectors(UiState state)
    {
        for (int i = 0; i < _selectors.Count; i++)
            _selectors[i].Apply(state);
    }

    /// <summary>Reset selector caches so the next state change always applies.</summary>
    protected void ResetSelectors()
    {
        for (int i = 0; i < _selectors.Count; i++)
            _selectors[i].Reset();
    }

    private interface ISelector
    {
        public void Apply(UiState s);
        public void Reset();
    }

    private sealed class Selector<T> : ISelector
    {
        private readonly Action<T> _apply;
        private readonly IEqualityComparer<T> _cmp;
        private readonly Func<UiState, T> _read;
        private bool _has;
        private T _last;

        public Selector(Func<UiState, T> read, Action<T> apply, IEqualityComparer<T>? cmp)
        {
            _read = read;
            _apply = apply;
            _cmp = cmp ?? EqualityComparer<T>.Default;
            _last = default!;
        }

        public void Apply(UiState s)
        {
            var v = _read(s);
            if (_has && _cmp.Equals(_last, v)) return;
            _last = v;
            _has = true;
            _apply(v);
        }

        public void Reset() => _has = false;
    }
}
