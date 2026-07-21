namespace Harbor.Desktop.Abstractions.ViewModels;
/// <summary>
///     Base for the provider-browser view-model. Lists configured providers,
///     allows searching/filtering, and exposes the currently selected
///     provider for the detail panel.
/// </summary>
public abstract partial class ProviderBrowserViewModelBase : ViewModelBase
{

    /// <summary>Search filter applied to <see cref="Providers" />.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Currently selected provider id, or null.</summary>
    [ObservableProperty]
    private string? _selectedProviderId;
    /// <summary>Construct a <see cref="ProviderBrowserViewModelBase" />.</summary>
    protected ProviderBrowserViewModelBase(ILogger logger) : base(logger)
    {
    }

    /// <summary>Visible providers, projected for the view layer.</summary>
    public ObservableCollection<ProviderListItem> Providers { get; } = new();

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
