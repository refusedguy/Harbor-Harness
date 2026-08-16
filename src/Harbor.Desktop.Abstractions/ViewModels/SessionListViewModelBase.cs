using System.Linq;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Ui.Framework.State;

namespace Harbor.Desktop.Abstractions.ViewModels;

/// <summary>
///     Base for the session-list view-model. Holds the observable session
///     collection, search filter, and current selection; platform VMs add
///     the actual <c>ISessionStore</c> calls and platform-specific navigation.
/// </summary>
public abstract partial class SessionListViewModelBase : StoreSubscriberViewModel
{

    /// <summary>True while the session list is loading from the store.</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Search filter applied to <see cref="Sessions" />.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Currently selected session id, or null.</summary>
    [ObservableProperty]
    private string? _selectedSessionId;

    /// <summary>Construct a <see cref="SessionListViewModelBase" />.</summary>
    /// <param name="dispatcher">UI-thread marshaller and store binder.</param>
    /// <param name="logger">Logger.</param>
    protected SessionListViewModelBase(IDispatcherAdapter dispatcher, ILogger logger)
        : base(dispatcher, logger)
    {
        Select(state => state.Sessions, v => SyncCollection(Sessions,
            v.Select(si => new SessionListItem(si.SessionId.Value, si.Title, si.LastActivityAt, 0)).ToImmutableArray()));
        Select(state => state.ActiveSessionId, v => SelectedSessionId = v is SessionId id ? id.Value : null);
        Select(state => state.IsLoading, v => IsLoading = v);
    }

    /// <summary>Visible sessions, projected for the view layer.</summary>
    public ObservableCollection<SessionListItem> Sessions { get; } = new();

    /// <summary>
    ///     Called when the global <see cref="UiState" /> changes. Applies all
    ///     declared selectors to project state slices into view-model properties.
    /// </summary>
    /// <param name="state">The current UI state snapshot.</param>
    protected override void OnStoreChanged(UiState state)
    {
        ApplySelectors(state);
    }

    /// <summary>Refresh the session list from the store. Implemented by the platform VM.</summary>
    protected abstract Task RefreshAsync(CancellationToken cancellationToken);

    /// <summary>Create a new session. Implemented by the platform VM.</summary>
    protected abstract Task CreateAsync(string title, CancellationToken cancellationToken);

    /// <summary>Branch the current session. Implemented by the platform VM.</summary>
    protected abstract Task BranchAsync(string parentId, CancellationToken cancellationToken);

    /// <summary>Open the selected session. Implemented by the platform VM.</summary>
    protected abstract Task OpenAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Delete the given session. Implemented by the platform VM.</summary>
    protected abstract Task DeleteAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>
    ///     Synchronise an <see cref="ImmutableArray{T}" /> into an
    ///     <see cref="ObservableCollection{T}" /> by clearing and re-adding.
    /// </summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="target">The observable collection to update.</param>
    /// <param name="source">The new immutable snapshot.</param>
    protected static void SyncCollection<T>(ObservableCollection<T> target, ImmutableArray<T> source)
    {
        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }
}

/// <summary>
///     One session-list row projected for the UI. Lightweight projection of
///     <see cref="Session" /> — the platform VM can map from the full Session
///     model when refreshing.
/// </summary>
/// <param name="Id">Session id.</param>
/// <param name="Title">Display title.</param>
/// <param name="UpdatedAt">Last-updated timestamp.</param>
/// <param name="MessageCount">Number of messages in the session.</param>
public sealed record SessionListItem(
    string Id,
    string Title,
    DateTimeOffset UpdatedAt,
    int MessageCount);
