using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Ui.Framework.Sessions;
using Harbor.Ui.Framework.Services;
using Microsoft.Extensions.Logging;
namespace Harbor.App.Avalonia.ViewModels;
/// <summary>
///     Left-sidebar session list. Search, new, branch, delete, select.
/// </summary>
public sealed partial class SessionListViewModel : ObservableObject
{
    private readonly ISessionManager _sessionManager;
    private readonly IDispatcherAdapter _dispatcher;
    private readonly ILogger<SessionListViewModel> _logger;
    private readonly ISessionStore _sessionStore;
    private readonly IToastService _toasts;

    [ObservableProperty]
    private SessionItemViewModel? _activeSession;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Construct the session list view-model.</summary>
    public SessionListViewModel(
        ISessionStore sessionStore,
        ISessionManager sessionManager,
        ILogger<SessionListViewModel> logger,
        IToastService toasts,
        IDispatcherAdapter dispatcher)
    {
        _sessionStore = sessionStore;
        _sessionManager = sessionManager;
        _logger = logger;
        _toasts = toasts;
        _dispatcher = dispatcher;

        // Subscribe to live status + message-count changes from the
        // SessionManager so the sidebar rows update in real time without
        // a full RefreshAsync round-trip (Task S2 / Problem 1 + 2).
        // StatusChanged fires when ChatViewModel.OnStoreChanged calls
        // SetStatus(working/done/error/idle); MessageCountChanged fires
        // when ChatViewModel.OnStoreChanged pushes the current line count.
        // Both handlers marshal to the UI thread because Sessions is an
        // ObservableCollection bound to the sidebar ListBox.
        _sessionManager.StatusChanged += OnSessionStatusChanged;
        _sessionManager.MessageCountChanged += OnSessionMessageCountChanged;
    }

    /// <summary>All sessions visible in the sidebar.</summary>
    public ObservableCollection<SessionItemViewModel> Sessions { get; } = new();

    /// <summary>Selected sessions (multi-select with Ctrl+click).</summary>
    public ObservableCollection<SessionItemViewModel> Selected { get; } = new();

    /// <summary>
    ///     Pushed by <see cref="LeftRailViewModel" /> (and any other
    ///     consumer) when it needs to know about live status updates
    ///     beyond the inner <see cref="Sessions" /> collection. Fires
    ///     after the matching <see cref="SessionItemViewModel.Status" />
    ///     has already been updated in place, so subscribers can either
    ///     read the new value from the item or use the payload directly.
    /// </summary>
    public event Action<string, SessionStatus>? ItemStatusChanged;

    /// <summary>
    ///     Raised when a session's live message count changes (new message
    ///     appended). Subscribers (e.g. <see cref="Shell.LeftRailViewModel" />)
    ///     update the corresponding row's count in place.
    /// </summary>
    public event Action<string, int>? ItemMessageCountChanged;

    private void OnSessionStatusChanged(string sessionId, SessionStatus status)
    {
        _dispatcher.Post(() =>
        {
            var item = Sessions.FirstOrDefault(s => s.Id == sessionId);
            if (item is not null)
            {
                item.Status = status;
            }
            ItemStatusChanged?.Invoke(sessionId, status);
        });
    }

    private void OnSessionMessageCountChanged(string sessionId, int count)
    {
        _dispatcher.Post(() =>
        {
            var item = Sessions.FirstOrDefault(s => s.Id == sessionId);
            if (item is not null)
            {
                item.MessageCount = count;
            }
            ItemMessageCountChanged?.Invoke(sessionId, count);
        });
    }

    /// <summary>Reload the session list from the store.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var result = await _sessionStore.ListAsync().ConfigureAwait(false);
            if (result.IsFailure)
            {
                _logger.LogError("List sessions failed: {Error}", result.Error);
                return;
            }
            var filtered = string.IsNullOrWhiteSpace(SearchText)
                ? result.Value
                : result.Value.Where(s => s.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                                          || s.Agent.Contains(SearchText, StringComparison.OrdinalIgnoreCase)).ToList();
            _dispatcher.Post(() =>
            {
                Sessions.Clear();
                foreach (var s in filtered)
                {
                    var item = new SessionItemViewModel(s.Id, s.Title, s.Agent, s.Model, s.ProviderId, s.UpdatedAt, s.Metadata.MessageCount, s.Directory);
                    item.Status = _sessionManager.GetStatus(s.Id);
                    (string? branch, bool dirty) = _sessionManager.GetGitInfo(s.Id);
                    item.GitBranch = branch;
                    item.GitIsDirty = dirty;
                    Sessions.Add(item);
                }
                if (_sessionManager.Active is { } active)
                {
                    ActiveSession = Sessions.FirstOrDefault(x => x.Id == active.Id);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh sessions crashed");
            _toasts.Show($"Could not load sessions: {ex.Message}", ToastKind.Error);
        }
    }

    /// <summary>Create a new session and switch to it.</summary>
    [RelayCommand]
    private async Task NewSessionAsync()
    {
        try
        {
            var session = await _sessionManager.NewSessionAsync(workingDirectory: Environment.CurrentDirectory).ConfigureAwait(false);
            if (session is null)
            {
                _toasts.Show("Failed to create session — check that a provider + model are configured.", ToastKind.Error);
                return;
            }
            await RefreshAsync().ConfigureAwait(false);
            // Must run on UI thread — Sessions is ObservableCollection modified by RefreshAsync.
            // Accessing it from a background thread (after ConfigureAwait(false)) throws
            // "Collection was modified" because the UI thread may be mid-update.
            _dispatcher.Post(() =>
            {
                var newItem = Sessions.FirstOrDefault(x => x.Id == session.Id);
                if (newItem is not null)
                {
                    ActiveSession = newItem;
                }
            });
            _toasts.Show($"New session: {session.Title}", ToastKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NewSession crashed");
            _toasts.Show($"Failed to create session: {ex.Message}", ToastKind.Error);
        }
    }

    /// <summary>Branch the active session.</summary>
    [RelayCommand]
    private async Task BranchAsync()
    {
        try
        {
            var branch = await _sessionManager.BranchActiveAsync().ConfigureAwait(false);
            if (branch is null)
            {
                _toasts.Show("Branch failed — no active session.", ToastKind.Error);
                return;
            }
            await RefreshAsync().ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                ActiveSession = Sessions.FirstOrDefault(x => x.Id == branch.Id);
            });
            _toasts.Show($"Branched → {branch.Title}", ToastKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Branch crashed");
            _toasts.Show($"Branch failed: {ex.Message}", ToastKind.Error);
        }
    }

    /// <summary>Open the selected session.</summary>
    /// <param name="item">Session to open.</param>
    [RelayCommand]
    private async Task OpenAsync(SessionItemViewModel? item)
    {
        if (item is null) return;
        try
        {
            bool ok = await _sessionManager.OpenSessionAsync(item.Id).ConfigureAwait(false);
            if (!ok)
            {
                _toasts.Show($"Could not open session '{item.Title}'.", ToastKind.Error);
                return;
            }
            ActiveSession = item;
            _toasts.Show($"Opened: {item.Title}", ToastKind.Info);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open session crashed");
            _toasts.Show($"Could not open session: {ex.Message}", ToastKind.Error);
        }
    }

    /// <summary>Delete the selected session (with confirm).</summary>
    [RelayCommand]
    private async Task DeleteAsync(SessionItemViewModel? item)
    {
        if (item is null) return;
        try
        {
            bool ok = await _sessionManager.DeleteSessionAsync(item.Id).ConfigureAwait(false);
            if (!ok)
            {
                _toasts.Show($"Could not delete session '{item.Title}'.", ToastKind.Error);
                return;
            }
            await RefreshAsync().ConfigureAwait(false);
            _toasts.Show($"Deleted: {item.Title}", ToastKind.Warning);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete session crashed");
            _toasts.Show($"Could not delete session: {ex.Message}", ToastKind.Error);
        }
    }

    /// <summary>
    ///     Rename the selected session. <b>Not yet supported</b> — surfaces an
    ///     honest "coming in v0.8" toast to the user instead of silently
    ///     mutating in-memory state (the previous behaviour gave the illusion
    ///     of a rename that would be lost on next refresh).
    /// </summary>
    [RelayCommand]
    private async Task RenameAsync(SessionItemViewModel? item)
    {
        if (item is null) return;
        try
        {
            bool ok = await _sessionManager.RenameSessionAsync(item.Id, item.Title + " (renamed)").ConfigureAwait(false);
            if (!ok)
            {
                _toasts.Show("Rename not yet supported — coming in v0.8.", ToastKind.Warning);
                return;
            }
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rename session crashed");
            _toasts.Show($"Rename failed: {ex.Message}", ToastKind.Error);
        }
    }
}
