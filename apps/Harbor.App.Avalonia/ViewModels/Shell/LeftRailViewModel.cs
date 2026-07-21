using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.Abstractions.Models;
namespace Harbor.App.Avalonia.ViewModels.Shell;
/// <summary>
///     Left-rail view-model for the Orca shell — projects
///     <see cref="SessionListViewModel" /> sessions into dense
///     <see cref="SessionRowViewModel" /> rows.
/// </summary>
/// <remarks>
///     <para>
///         <b>TEA boundary:</b> this VM is a pure projection. It owns no
///         session state — every mutation (<c>NewSession</c>, <c>Open</c>,
///         <c>Branch</c>) is delegated to the shared
///         <see cref="SessionListViewModel" /> which talks to the
///         <c>SessionManager</c> → <c>ISessionStore</c> → <c>UiStore</c>
///         chain. The rail VM only mirrors the resulting collection changes.
///     </para>
///     <para>
///         Subscribes to <see cref="ObservableCollection{T}.CollectionChanged" />
///         on <see cref="SessionListViewModel.Sessions" /> so the dense rows
///         stay in sync after <c>RefreshAsync</c> / <c>NewSession</c> /
///         <c>Branch</c> / <c>Delete</c>.
///     </para>
/// </remarks>
public sealed partial class LeftRailViewModel : ObservableObject, IDisposable
{
    private readonly IDispatcherAdapter _dispatcher;
    private readonly SessionListViewModel _inner;

    /// <summary>Currently selected row (TwoWay with the ListBox).</summary>
    [ObservableProperty]
    private SessionRowViewModel? _activeSession;
    private bool _disposed;

    /// <summary>Free-text filter applied to <see cref="FilteredSessions" />.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Construct the rail VM wrapping <paramref name="inner" />.</summary>
    public LeftRailViewModel(SessionListViewModel inner, IDispatcherAdapter dispatcher)
    {
        _inner = inner;
        _dispatcher = dispatcher;
        _inner.Sessions.CollectionChanged += OnInnerCollectionChanged;
        // Subscribe to live status + message-count updates so the dense
        // rows track the agent state in real time (Task S2 / Problem 1 + 2)
        // without waiting for a full ReprojectAll cycle.
        _inner.ItemStatusChanged += OnItemStatusChanged;
        _inner.ItemMessageCountChanged += OnItemMessageCountChanged;
        // Project whatever is already there.
        ReprojectAll();
    }

    /// <summary>Dense rows projected from <see cref="SessionListViewModel.Sessions" />.</summary>
    public ObservableCollection<SessionRowViewModel> FilteredSessions { get; } = new();

    /// <summary>Title shown at the top of the rail (brand).</summary>
    public string BrandTitle => "Harbor";

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _inner.Sessions.CollectionChanged -= OnInnerCollectionChanged;
        _inner.ItemStatusChanged -= OnItemStatusChanged;
        _inner.ItemMessageCountChanged -= OnItemMessageCountChanged;
    }

    /// <summary>Invoke the inner NewSession command (delegates to SessionManager).</summary>
    [RelayCommand]
    private async Task NewSessionAsync() => await _inner.NewSessionCommand.ExecuteAsync(null).ConfigureAwait(false);

    /// <summary>Refresh the underlying session list (delegates to inner VM).</summary>
    [RelayCommand]
    private async Task RefreshAsync() => await _inner.RefreshCommand.ExecuteAsync(null).ConfigureAwait(false);

    /// <summary>
    ///     Called when <see cref="ActiveSession" /> changes (ListBox selection).
    ///     Forwards the selection to the inner VM so the SessionManager opens
    ///     the session in the chat view.
    /// </summary>
    /// <param name="value">The newly selected row, or null.</param>
    partial void OnActiveSessionChanged(SessionRowViewModel? value)
    {
        if (value is null) return;
        // Find the matching SessionItemViewModel in the inner list and forward.
        var match = _inner.Sessions.FirstOrDefault(s => s.Id == value.Id);
        if (match is not null)
        {
            _ = _inner.OpenCommand.ExecuteAsync(match);
        }
    }

    /// <summary>Re-filter when SearchText changes.</summary>
    /// <param name="value">New search text.</param>
    partial void OnSearchTextChanged(string value) => ReprojectAll();

    private void OnInnerCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Marshal to UI thread — CollectionChanged fires on whatever thread
        // mutated the source collection (often a threadpool continuation).
        _dispatcher.Post(ReprojectAll);
    }

    /// <summary>
    ///     Rebuild <see cref="FilteredSessions" /> from the inner collection
    ///     applying the current <see cref="SearchText" />. Marks the active
    ///     row's <see cref="SessionRowViewModel.IsActive" /> flag.
    /// </summary>
    public void ReprojectAll()
    {
        var innerActive = _inner.ActiveSession;
        var rows = _inner.Sessions
            .Where(s => string.IsNullOrWhiteSpace(SearchText)
                        || s.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                        || s.Agent.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                        || s.Model.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
            .Select(s => new SessionRowViewModel(
                s.Id,
                s.Title,
                s.Agent,
                s.Model,
                s.ProviderId,
                s.UpdatedAt,
                s.MessageCount,
                MapStatus(s.Status),
                mode: "Chat",
                workdir: null,
                costTotal: null)
            {
                IsActive = innerActive is not null && innerActive.Id == s.Id
            })
            .ToList();

        FilteredSessions.Clear();
        foreach (var r in rows)
        {
            FilteredSessions.Add(r);
        }

        // Keep ActiveSession in sync with the inner VM's active selection.
        if (innerActive is not null)
        {
            var match = rows.FirstOrDefault(r => r.Id == innerActive.Id);
            if (match is not null && !ReferenceEquals(match, ActiveSession))
            {
                ActiveSession = match;
            }
        }
        else if (ActiveSession is not null)
        {
            ActiveSession = null;
        }
    }

    /// <summary>
    ///     Map the domain <see cref="SessionStatus" /> enum to the
    ///     <see cref="SessionRowViewModel.Status" /> string the Orca rail
    ///     template expects (idle/running/error/completed). The mapping
    ///     is the single source of truth — <see cref="ReprojectAll" />
    ///     and <see cref="OnItemStatusChanged" /> both go through here so
    ///     a full re-projection and a live update produce identical
    ///     dot colours for the same underlying state.
    /// </summary>
    private static string MapStatus(SessionStatus status) => status switch
    {
        SessionStatus.Working => "running",
        SessionStatus.Error => "error",
        SessionStatus.Done => "completed",
        SessionStatus.Aborted => "error",
        _ => "idle"
    };

    private void OnItemStatusChanged(string sessionId, SessionStatus status)
    {
        _dispatcher.Post(() =>
        {
            var row = FilteredSessions.FirstOrDefault(r => r.Id == sessionId);
            if (row is not null)
            {
                row.Status = MapStatus(status);
            }
        });
    }

    private void OnItemMessageCountChanged(string sessionId, int count)
    {
        _dispatcher.Post(() =>
        {
            var row = FilteredSessions.FirstOrDefault(r => r.Id == sessionId);
            if (row is not null)
            {
                row.MessageCount = count;
            }
        });
    }
}
