using CommunityToolkit.Mvvm.ComponentModel;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging;

namespace Harbor.Ui.Framework.ViewModels;

public abstract class StoreSubscriberViewModel : ObservableObject, IDisposable
{
    protected readonly IDispatcherAdapter Dispatcher;
    private readonly ILogger _logger;
    private readonly EventHandler<UiState> _onStoreChanged;

    protected ILogger Logger => _logger;

    protected StoreSubscriberViewModel(
        IDispatcherAdapter dispatcher,
        ILogger logger)
    {
        Dispatcher = dispatcher;
        _logger = logger;
        _onStoreChanged = (_, state) => OnStoreChanged(state);
        Dispatcher.StateChanged += _onStoreChanged;
    }

    protected abstract void OnStoreChanged(UiState state);

    public virtual void Dispose()
    {
        Dispatcher.StateChanged -= _onStoreChanged;
    }
}
