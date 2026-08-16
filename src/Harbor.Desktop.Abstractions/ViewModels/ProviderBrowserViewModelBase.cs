using Harbor.Ui.Framework.State;

namespace Harbor.Desktop.Abstractions.ViewModels;

/// <summary>
///     Base for the provider-browser view-model. Lists configured providers,
///     allows searching/filtering, and exposes the currently selected
///     provider for the detail panel.
/// </summary>
public abstract partial class ProviderBrowserViewModelBase : StoreSubscriberViewModel
{

    /// <summary>Search filter applied to <see cref="Providers" />.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Currently selected provider id, or null.</summary>
    [ObservableProperty]
    private string? _selectedProviderId;

    /// <summary>Construct a <see cref="ProviderBrowserViewModelBase" />.</summary>
    /// <param name="dispatcher">UI-thread marshaller and store binder.</param>
    /// <param name="logger">Logger.</param>
    protected ProviderBrowserViewModelBase(
        IDispatcherAdapter dispatcher,
        ILogger logger)
        : base(dispatcher, logger)
    {
        Select(state => state.Provider, v => SelectedProviderId = v);
    }

    /// <summary>Visible providers, projected for the view layer.</summary>
    public ObservableCollection<ProviderListItem> Providers { get; } = new();

    /// <summary>
    ///     Called when the global <see cref="UiState" /> changes. Applies all
    ///     declared selectors to project state slices into view-model properties.
    /// </summary>
    /// <param name="state">The current UI state snapshot.</param>
    protected override void OnStoreChanged(UiState state)
    {
        ApplySelectors(state);
    }

    /// <summary>Refresh the provider list from <c>IProviderRegistry</c>. Implemented by the platform VM.</summary>
    protected abstract Task RefreshAsync(CancellationToken cancellationToken);
}

/// <summary>
///     One provider-list row projected for the UI.
/// </summary>
/// <param name="Id">Provider id (e.g. "openai", "anthropic").</param>
/// <param name="DisplayName">Human-readable name.</param>
/// <param name="IsConfigured">True if the provider has credentials configured.</param>
public sealed record ProviderListItem(
    string Id,
    string DisplayName,
    bool IsConfigured);
