using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.Desktop.Abstractions.ViewModels;
namespace Harbor.App.Wpf.ViewModels;
/// <summary>
///     Ctrl+P command palette. Fuzzy-matches a query against a list of
///     commands and lets the user invoke the top hit.
/// </summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private readonly List<CommandEntry> _allCommands;

    /// <summary>Search query.</summary>
    [ObservableProperty] private string _query = string.Empty;

    /// <summary>Selected result index.</summary>
    [ObservableProperty] private int _selectedIndex;

    /// <summary>Construct a <see cref="CommandPaletteViewModel" />.</summary>
    public CommandPaletteViewModel()
    {
        _allCommands = new List<CommandEntry>
        {
            new("app.new-session", "New Session", "Create a new chat session", "session"),
            new("app.fork", "Fork Session", "Branch the current session", "session"),
            new("app.open-file", "Open File", "Open a file in the editor", "file"),
            new("app.save-file", "Save File", "Save the current buffer", "file"),
            new("app.browse-providers", "Browse Providers", "Open the provider browser", "provider"),
            new("app.settings", "Settings", "Open settings", "settings"),
            new("app.toggle-theme", "Toggle Theme", "Switch dark/light theme", "theme"),
            new("app.show-tokens", "Show Token Usage", "Bring the token panel to front", "tokens"),
            new("app.show-diff", "Show Diff", "Bring the diff panel to front", "diff"),
            new("app.clear-chat", "Clear Chat", "Clear the chat transcript", "chat"),
            new("app.cancel", "Cancel Run", "Abort the current run", "chat")
        };
        Results = new ObservableCollection<CommandEntry>(_allCommands);
        Query = string.Empty;
    }

    /// <summary>Visible results (filtered by <see cref="Query" />).</summary>
    public ObservableCollection<CommandEntry> Results { get; }

    /// <summary>Invoked when the user picks a command. Carries the command id.</summary>
    public event Action<string>? CommandInvoked;

    /// <summary>Invoke the selected result.</summary>
    [RelayCommand]
    private void InvokeSelected()
    {
        if (Results.Count == 0) return;
        int idx = SelectedIndex < 0 || SelectedIndex >= Results.Count ? 0 : SelectedIndex;
        CommandInvoked?.Invoke(Results[idx].Id);
    }

    /// <summary>Move the selection up.</summary>
    [RelayCommand]
    private void MoveUp()
    {
        if (Results.Count == 0) return;
        SelectedIndex = (SelectedIndex - 1 + Results.Count) % Results.Count;
    }

    /// <summary>Move the selection down.</summary>
    [RelayCommand]
    private void MoveDown()
    {
        if (Results.Count == 0) return;
        SelectedIndex = (SelectedIndex + 1) % Results.Count;
    }

    partial void OnQueryChanged(string value)
    {
        string needle = (value ?? string.Empty).Trim().ToLowerInvariant();
        Results.Clear();
        foreach (var c in _allCommands)
        {
            if (string.IsNullOrEmpty(needle) ||
                c.Title.ToLowerInvariant().Contains(needle) ||
                c.Id.Contains(needle))
            {
                Results.Add(c);
            }
        }
        SelectedIndex = Results.Count > 0 ? 0 : -1;
    }
}

/// <summary>
///     A command entry in the palette. Platform projection of the shared
///     <see cref="Harbor.Desktop.Abstractions.ViewModels.CommandEntry" /> record (kept in
///     this namespace so the WPF XAML <c>vm:</c> mappings resolve to it). The
///     canonical data lives on the shared record.
/// </summary>
public sealed class CommandEntry : Harbor.Desktop.Abstractions.ViewModels.CommandEntry
{
    /// <summary>Construct a <see cref="CommandEntry" />.</summary>
    public CommandEntry(string id, string title, string description, string category)
        : base(id, title, description, category) { }
}
