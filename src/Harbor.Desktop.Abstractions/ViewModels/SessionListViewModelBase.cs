using Harbor.Abstractions.Models;

namespace Harbor.Desktop.Abstractions.ViewModels;

/// <summary>
///     Base for the session-list view-model. Holds the observable session
///     collection, search filter, and current selection; platform VMs add
///     the actual <c>ISessionStore</c> calls and platform-specific navigation.
/// </summary>
public abstract partial class SessionListViewModelBase : ViewModelBase
{
    /// <summary>Construct a <see cref="SessionListViewModelBase"/>.</summary>
    protected SessionListViewModelBase(ILogger logger) : base(logger)
    {
    }

    /// <summary>Visible sessions, projected for the view layer.</summary>
    public ObservableCollection<SessionListItem> Sessions { get; } = new();

    /// <summary>Search filter applied to <see cref="Sessions"/>.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Currently selected session id, or null.</summary>
    [ObservableProperty]
    private string? _selectedSessionId;

    /// <summary>True while the session list is loading from the store.</summary>
    [ObservableProperty]
    private bool _isLoading;

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
}

/// <summary>
///     One session-list row projected for the UI. Lightweight projection of
///     <see cref="Session"/> — the platform VM can map from the full Session
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
