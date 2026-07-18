using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.Abstractions.Sessions;
using Harbor.App.Avalonia.Services;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Avalonia.ViewModels;

/// <summary>
///     Left-sidebar session list. Search, new, branch, delete, select.
/// </summary>
public sealed partial class SessionListViewModel : ObservableObject
{
    private readonly ISessionStore _sessionStore;
    private readonly SessionManager _sessionManager;
    private readonly ILogger<SessionListViewModel> _logger;
    private readonly ToastService _toasts;

    /// <summary>Construct the session list view-model.</summary>
    public SessionListViewModel(
        ISessionStore sessionStore,
        SessionManager sessionManager,
        ILogger<SessionListViewModel> logger,
        ToastService toasts)
    {
        _sessionStore = sessionStore;
        _sessionManager = sessionManager;
        _logger = logger;
        _toasts = toasts;
    }

    /// <summary>All sessions visible in the sidebar.</summary>
    public ObservableCollection<SessionItemViewModel> Sessions { get; } = new();

    /// <summary>Selected sessions (multi-select with Ctrl+click).</summary>
    public ObservableCollection<SessionItemViewModel> Selected { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private SessionItemViewModel? _activeSession;

    /// <summary>Reload the session list from the store.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
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
        Dispatcher.UIThread.Post(() =>
        {
            Sessions.Clear();
            foreach (var s in filtered)
            {
                Sessions.Add(new SessionItemViewModel(s.Id, s.Title, s.Agent, s.Model, s.ProviderId, s.UpdatedAt, s.Metadata.MessageCount));
            }
            if (_sessionManager.Active is { } active)
            {
                ActiveSession = Sessions.FirstOrDefault(x => x.Id == active.Id);
            }
        });
    }

    /// <summary>Create a new session and switch to it.</summary>
    [RelayCommand]
    private async Task NewSessionAsync()
    {
        var session = await _sessionManager.NewSessionAsync().ConfigureAwait(false);
        if (session is null)
        {
            _toasts.Show("Failed to create session.", ToastKind.Error);
            return;
        }
        await RefreshAsync().ConfigureAwait(false);
        ActiveSession = Sessions.FirstOrDefault(x => x.Id == session.Id);
        _toasts.Show($"New session: {session.Title}", ToastKind.Success);
    }

    /// <summary>Branch the active session.</summary>
    [RelayCommand]
    private async Task BranchAsync()
    {
        var branch = await _sessionManager.BranchActiveAsync().ConfigureAwait(false);
        if (branch is null)
        {
            _toasts.Show("Branch failed — no active session.", ToastKind.Error);
            return;
        }
        await RefreshAsync().ConfigureAwait(false);
        ActiveSession = Sessions.FirstOrDefault(x => x.Id == branch.Id);
        _toasts.Show($"Branched → {branch.Title}", ToastKind.Success);
    }
    /// <summary>Open the selected session.</summary>
    /// <param name="item">Session to open.</param>
    [RelayCommand]
    private async Task OpenAsync(SessionItemViewModel? item)
    {
        if (item is null) return;
        var ok = await _sessionManager.OpenSessionAsync(item.Id).ConfigureAwait(false);
        if (!ok)
        {
            _toasts.Show($"Could not open session '{item.Title}'.", ToastKind.Error);
            return;
        }
        ActiveSession = item;
        _toasts.Show($"Opened: {item.Title}", ToastKind.Info);
    }

    /// <summary>Delete the selected session (with confirm).</summary>
    [RelayCommand]
    private async Task DeleteAsync(SessionItemViewModel? item)
    {
        if (item is null) return;
        var ok = await _sessionManager.DeleteSessionAsync(item.Id).ConfigureAwait(false);
        if (!ok)
        {
            _toasts.Show($"Could not delete session '{item.Title}'.", ToastKind.Error);
            return;
        }
        await RefreshAsync().ConfigureAwait(false);
        _toasts.Show($"Deleted: {item.Title}", ToastKind.Warning);
    }

    /// <summary>Rename the selected session.</summary>
    [RelayCommand]
    private async Task RenameAsync(SessionItemViewModel? item)
    {
        if (item is null) return;
        // The actual prompt is in the view (DialogService.PromptAsync). For the
        // simple flow we accept a default suffix.
        await _sessionManager.RenameSessionAsync(item.Id, item.Title + " (renamed)").ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
    }
}

/// <summary>One sidebar session row.</summary>
public sealed record SessionItemViewModel(
    string Id,
    string Title,
    string Agent,
    string Model,
    string ProviderId,
    DateTimeOffset UpdatedAt,
    int MessageCount)
{
    /// <summary>Relative time-ago label.</summary>
    public string RelativeTime => UpdatedAt switch
    {
        var t when (DateTimeOffset.UtcNow - t).TotalMinutes < 1 => "just now",
        var t when (DateTimeOffset.UtcNow - t).TotalHours < 1 => $"{(int)(DateTimeOffset.UtcNow - t).TotalMinutes}m ago",
        var t when (DateTimeOffset.UtcNow - t).TotalDays < 1 => $"{(int)(DateTimeOffset.UtcNow - t).TotalHours}h ago",
        var t => t.ToString("MMM d")
    };
}
