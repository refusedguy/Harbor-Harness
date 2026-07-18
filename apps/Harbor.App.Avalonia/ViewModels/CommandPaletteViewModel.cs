using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.App.Avalonia.Services;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Avalonia.ViewModels;

/// <summary>
///     Command palette (cmdk-style) view-model. Fuzzy-searches across:
///     slash commands, sessions, recently opened files, view switches, settings actions.
/// </summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly ILogger<CommandPaletteViewModel> _logger;
    private readonly List<CommandResultViewModel> _allCommands;

    /// <summary>Construct the palette view-model.</summary>
    public CommandPaletteViewModel(IServiceProvider services, ILogger<CommandPaletteViewModel> logger)
    {
        _services = services;
        _logger = logger;
        // Build the command list inside the constructor (instance methods are valid here).
        _allCommands = new List<CommandResultViewModel>
        {
            new("command", "Switch to chat",         "ChatView",         SwitchToChat),
            new("command", "Switch to code editor",  "CodeEditorView",   SwitchToCode),
            new("command", "Open settings",          "SettingsDialog",   OpenSettings),
            new("command", "Open provider browser",  "ProviderBrowser",  OpenProviderBrowser),
            new("command", "Open diff view",         "DiffView",         OpenDiff),
            new("command", "Open token usage chart", "TokenUsageView",   OpenTokenUsage),
            new("command", "Toggle sidebar (Ctrl+B)","SidebarToggle",    ToggleSidebar),
            new("command", "Toggle theme (Ctrl+Shift+T)", "ThemeToggle", ToggleTheme),
            new("command", "New session",            "SessionNew",       NewSession),
            new("command", "Branch active session",  "SessionBranch",    BranchSession),
            new("command", "Open file (Ctrl+O)",     "FileOpen",         OpenFile),
            new("command", "Save file (Ctrl+S)",     "FileSave",         SaveFile),
            new("command", "Stop agent",             "AgentStop",        StopAgent),
            new("command", "Clear chat (Ctrl+L)",    "ChatClear",        ClearChat),
            new("command", "Refresh session list",   "SessionRefresh",   RefreshSessions),
            new("slash", "/help",      "Slash command", () => RunSlash("/help")),
            new("slash", "/exit",      "Slash command", () => RunSlash("/exit")),
            new("slash", "/setup",     "Slash command", () => RunSlash("/setup")),
            new("slash", "/auth",      "Slash command", () => RunSlash("/auth")),
            new("slash", "/model",     "Slash command", () => RunSlash("/model")),
            new("slash", "/agent",     "Slash command", () => RunSlash("/agent")),
            new("slash", "/config",    "Slash command", () => RunSlash("/config")),
            new("slash", "/providers", "Slash command", () => RunSlash("/providers")),
            new("slash", "/sessions",  "Slash command", () => RunSlash("/sessions")),
            new("slash", "/tui",       "Slash command", () => RunSlash("/tui")),
            new("slash", "/storage",   "Slash command", () => RunSlash("/storage")),
            new("slash", "/clear",     "Slash command", () => RunSlash("/clear")),
        };
        Results = new ObservableCollection<CommandResultViewModel>(_allCommands);
        SelectedIndex = 0;
    }

    /// <summary>Visible search results.</summary>
    public ObservableCollection<CommandResultViewModel> Results { get; }

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private int _selectedIndex;

    /// <summary>Recompute results when the query changes.</summary>
    partial void OnQueryChanged(string value)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Results.Clear();
            var q = (value ?? string.Empty).Trim().ToLowerInvariant();
            var matches = string.IsNullOrEmpty(q)
                ? _allCommands
                : _allCommands
                    .Where(c => c.Label.ToLowerInvariant().Contains(q) || c.Hint.ToLowerInvariant().Contains(q))
                    .OrderByDescending(c => FuzzyScore(c.Label.ToLowerInvariant(), q))
                    .ToList();
            foreach (var m in matches)
            {
                Results.Add(m);
            }
            SelectedIndex = Results.Count > 0 ? 0 : -1;
        });
    }

    /// <summary>Simple subsequence-match score. Higher = better match.</summary>
    private static int FuzzyScore(string text, string query)
    {
        if (string.IsNullOrEmpty(query)) return 0;
        if (text.Contains(query, StringComparison.OrdinalIgnoreCase)) return 100 - text.Length;
        int ti = 0, qi = 0, score = 0;
        while (ti < text.Length && qi < query.Length)
        {
            if (text[ti] == query[qi]) { score += 1; qi++; }
            ti++;
        }
        return qi == query.Length ? score - (text.Length - query.Length) : -1;
    }

    /// <summary>Run the command at the given index.</summary>
    public void InvokeSelected()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Results.Count) return;
        var cmd = Results[SelectedIndex];
        try
        {
            cmd.Action.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command '{Label}' threw", cmd.Label);
        }
    }

    /// <summary>Move selection up by one.</summary>
    public void MoveUp()
    {
        if (SelectedIndex > 0) SelectedIndex--;
    }

    /// <summary>Move selection down by one.</summary>
    public void MoveDown()
    {
        if (SelectedIndex < Results.Count - 1) SelectedIndex++;
    }

    // ── Command implementations — they resolve the MainViewModel from DI and invoke its commands. ──

    private MainViewModel Main => _services.GetRequiredService<MainViewModel>();

    private void SwitchToChat() => Main.SwitchViewCommand.Execute("chat");
    private void SwitchToCode() => Main.SwitchViewCommand.Execute("code");
    private void OpenSettings() => Main.IsSettingsOpen = true;
    private void OpenProviderBrowser() => Main.IsProviderBrowserOpen = true;
    private void OpenDiff() => Main.IsDiffOpen = true;
    private void OpenTokenUsage() => Main.IsTokenUsageOpen = true;
    private void ToggleSidebar() => Main.ToggleSidebarCommand.Execute(null);
    private void ToggleTheme() => Main.ToggleThemeCommand.Execute(null);
    private void NewSession() => _ = Main.Sessions.NewSessionCommand.ExecuteAsync(null);
    private void BranchSession() => _ = Main.Sessions.BranchCommand.ExecuteAsync(null);
    private void OpenFile() => _ = Main.CodeEditor.OpenFileCommand.ExecuteAsync(null);
    private void SaveFile() => _ = Main.CodeEditor.SaveCommand.ExecuteAsync(null);
    private void StopAgent() => Main.Chat.StopCommand.Execute(null);
    private void ClearChat() => Main.Chat.ClearCommand.Execute(null);
    private void RefreshSessions() => _ = Main.Sessions.RefreshCommand.ExecuteAsync(null);

    private void RunSlash(string command)
    {
        var effects = _services.GetRequiredService<TuiEffectHost>();
        effects.Run(new TuiEffect.RunSlash(command));
        Main.IsCommandPaletteOpen = false;
    }
}

/// <summary>One command result row.</summary>
public sealed record CommandResultViewModel(string Kind, string Label, string Hint, Action Action)
{
    /// <summary>Icon glyph based on kind.</summary>
    public string Icon => Kind switch
    {
        "command" => "⚡",
        "slash"   => "/",
        "file"    => "📄",
        "session" => "💬",
        _         => "•"
    };
}
