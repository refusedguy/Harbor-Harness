using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.Desktop.Abstractions.ViewModels;
namespace Harbor.App.Wpf.ViewModels;
/// <summary>
///     Sidebar session list. Supports search, fork/branch, and switching the
///     active session. Backed by an in-memory collection; in a real wiring
///     this calls into <c>ISessionStore.ListAsync</c>.
/// </summary>
public sealed partial class SessionListViewModel : ObservableObject
{
    private readonly List<SessionEntryViewModel> _allSessions;

    /// <summary>Search text. Filters the list as the user types.</summary>
    [ObservableProperty] private string _searchText = string.Empty;

    /// <summary>The currently selected session id, or <see langword="null" />.</summary>
    [ObservableProperty] private string? _selectedSessionId;

    /// <summary>Construct a <see cref="SessionListViewModel" />.</summary>
    public SessionListViewModel()
    {
        _allSessions = new List<SessionEntryViewModel>();
        Sessions = new ObservableCollection<SessionEntryViewModel>();

        // Seed a few sample sessions so the sidebar isn't empty.
        var now = DateTimeOffset.UtcNow;
        AddSample(now.AddDays(-1), "Refactor AgentLoop.cs", "code");
        AddSample(now.AddHours(-3), "Add SQLite store", "code");
        AddSample(now.AddHours(-1), "Investigate memory leak", "plan");
        AddSample(now, "New session", "code");

        SearchText = string.Empty;
    }

    /// <summary>Visible sessions (after filtering).</summary>
    public ObservableCollection<SessionEntryViewModel> Sessions { get; }

    /// <summary>Create a new session.</summary>
    [RelayCommand]
    private void NewSession()
    {
        var entry = new SessionEntryViewModel(
            Guid.NewGuid().ToString("N"),
            $"Session {DateTime.Now:yyyy-MM-dd HH:mm}",
            "code",
            DateTimeOffset.UtcNow,
            null);
        _allSessions.Add(entry);
        ReapplyFilter();
        SelectedSessionId = entry.Id;
    }

    /// <summary>Fork the currently selected session into a new branch.</summary>
    [RelayCommand(CanExecute = nameof(CanFork))]
    private void ForkSelected()
    {
        var parent = FindSelected();
        if (parent is null) return;
        var fork = new SessionEntryViewModel(
            Guid.NewGuid().ToString("N"),
            parent.Title + " (fork)",
            parent.AgentName,
            DateTimeOffset.UtcNow,
            parent.Id);
        _allSessions.Add(fork);
        ReapplyFilter();
        SelectedSessionId = fork.Id;
    }

    /// <summary>Delete the selected session.</summary>
    [RelayCommand(CanExecute = nameof(CanFork))]
    private void DeleteSelected()
    {
        var selected = FindSelected();
        if (selected is null) return;
        _allSessions.Remove(selected);
        ReapplyFilter();
        SelectedSessionId = null;
    }

    private bool CanFork() => !string.IsNullOrEmpty(SelectedSessionId);

    partial void OnSearchTextChanged(string value) => ReapplyFilter();

    partial void OnSelectedSessionIdChanged(string? value) => ForkSelectedCommand.NotifyCanExecuteChanged();

    private SessionEntryViewModel? FindSelected()
        => string.IsNullOrEmpty(SelectedSessionId)
            ? null
            : _allSessions.FirstOrDefault(s => s.Id == SelectedSessionId);

    private void ReapplyFilter()
    {
        string needle = (SearchText ?? string.Empty).Trim();
        Sessions.Clear();
        foreach (var s in _allSessions)
        {
            if (string.IsNullOrEmpty(needle) || s.Title.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                Sessions.Add(s);
            }
        }
    }

    private void AddSample(DateTimeOffset updatedAt, string title, string agent)
    {
        _allSessions.Add(new SessionEntryViewModel(
            Guid.NewGuid().ToString("N"),
            title,
            agent,
            updatedAt,
            null));
    }
}

/// <summary>
///     Sidebar entry for a session. Platform projection of the shared
///     <see cref="Harbor.Desktop.Abstractions.ViewModels.SessionEntryViewModel" /> record
///     (kept in this namespace so the WPF XAML <c>vm:</c> mappings and the
///     <c>with</c>-expression in <see cref="SessionListViewModel" /> resolve to it).
///     The canonical data lives on the shared record.
/// </summary>
public sealed class SessionEntryViewModel : Harbor.Desktop.Abstractions.ViewModels.SessionEntryViewModel
{
    /// <summary>Construct a <see cref="SessionEntryViewModel" />.</summary>
    public SessionEntryViewModel(string id, string title, string agentName, DateTimeOffset updatedAt, string? parentId)
        : base(id, title, agentName, updatedAt, parentId) { }
}
