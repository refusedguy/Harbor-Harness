using Harbor.App.Avalonia.Services;
using Harbor.Desktop.Abstractions.ViewModels;
using Harbor.Ui.Framework.Navigation;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging;
namespace Harbor.App.Avalonia.ViewModels;
/// <summary>
///     Command palette (cmdk-style) view-model. Fuzzy-searches across:
///     slash commands, sessions, recently opened files, view switches, settings actions.
///     Inherits the shared fuzzy-filter + selection logic from
///     <see cref="CommandPaletteViewModelBase" />; this class only wires the
///     platform-specific command callbacks (which bounce through the shell VM).
/// </summary>
public sealed partial class CommandPaletteViewModel : CommandPaletteViewModelBase
{
    private readonly IShellChrome _shellChrome;
    private readonly IWorkspaceCommands _workspaceCommands;
    private readonly TuiEffectHost _effects;
    private readonly ILogger<CommandPaletteViewModel> _logger;
    private readonly IFloatingTerminals? _floatingTerminals;

    /// <summary>Construct the palette view-model.</summary>
    public CommandPaletteViewModel(
        IDispatcherAdapter dispatcher,
        ILogger<CommandPaletteViewModel> logger,
        IShellChrome shellChrome,
        IWorkspaceCommands workspaceCommands,
        TuiEffectHost effects,
        IFloatingTerminals? floatingTerminals = null)
        : base(dispatcher, logger)
    {
        _shellChrome = shellChrome;
        _workspaceCommands = workspaceCommands;
        _effects = effects;
        _logger = logger;
        _floatingTerminals = floatingTerminals;
        // Build the command list (platform callbacks) and run the initial filter.
        AllCommands.AddRange(new CommandResultViewModel[]
        {
            new("command", "Switch to chat", "ChatView", SwitchToChat),
            new("command", "Switch to code editor", "CodeEditorView", SwitchToCode),
            new("command", "Open settings", "SettingsDialog", OpenSettings),
            new("command", "Open provider browser", "ProviderBrowser", OpenProviderBrowser),
            new("command", "Open diff view", "DiffView", OpenDiff),
            new("command", "Open token usage chart", "TokenUsageView", OpenTokenUsage),
            new("command", "Toggle sidebar (Ctrl+B)", "SidebarToggle", ToggleSidebar),
            new("command", "Toggle theme (Ctrl+Shift+T)", "ThemeToggle", ToggleTheme),
            new("command", "Floating terminal pane (Ctrl+`)", "TerminalPane", ToggleTerminalPane),
            new("command", "New session", "SessionNew", NewSession),
            new("command", "Branch active session", "SessionBranch", BranchSession),
            new("command", "Open file (Ctrl+O)", "FileOpen", OpenFile),
            new("command", "Save file (Ctrl+S)", "FileSave", SaveFile),
            new("command", "Stop agent", "AgentStop", StopAgent),
            new("command", "Clear chat (Ctrl+L)", "ChatClear", ClearChat),
            new("command", "Refresh session list", "SessionRefresh", RefreshSessions),
            new("slash", "/help", "Slash command", () => RunSlash("/help")),
            new("slash", "/exit", "Slash command", () => RunSlash("/exit")),
            new("slash", "/setup", "Slash command", () => RunSlash("/setup")),
            new("slash", "/auth", "Slash command", () => RunSlash("/auth")),
            new("slash", "/model", "Slash command", () => RunSlash("/model")),
            new("slash", "/agent", "Slash command", () => RunSlash("/agent")),
            new("slash", "/config", "Slash command", () => RunSlash("/config")),
            new("slash", "/providers", "Slash command", () => RunSlash("/providers")),
            new("slash", "/sessions", "Slash command", () => RunSlash("/sessions")),
            new("slash", "/tui", "Slash command", () => RunSlash("/tui")),
            new("slash", "/storage", "Slash command", () => RunSlash("/storage")),
            new("slash", "/clear", "Slash command", () => RunSlash("/clear"))
        });
        Refilter(string.Empty);
    }

    private void SwitchToChat() => _shellChrome.Navigate("chat");
    private void SwitchToCode() => _shellChrome.Navigate("code");
    private void OpenSettings() => _shellChrome.OpenOverlay(OverlayIds.Settings);
    private void OpenProviderBrowser() => _shellChrome.OpenOverlay(OverlayIds.ProviderBrowser);
    private void OpenDiff() => _shellChrome.OpenOverlay(OverlayIds.Diff);
    private void OpenTokenUsage() => _shellChrome.OpenOverlay(OverlayIds.TokenUsage);
    private void ToggleSidebar() => _shellChrome.ToggleSidebar();
    private void ToggleTheme() => _shellChrome.ToggleTheme();
    private void ToggleTerminalPane()
    {
        if (_floatingTerminals is { } terminals)
        {
            terminals.TogglePane();
        }
    }
    private void NewSession() => _workspaceCommands.NewSession();
    private void BranchSession() => _workspaceCommands.BranchSession();
    private void OpenFile() => _ = _workspaceCommands.OpenFileAsync();
    private void SaveFile() => _ = _workspaceCommands.SaveFileAsync();
    private void StopAgent() => _workspaceCommands.StopAgent();
    private void ClearChat() => _workspaceCommands.ClearChat();
    private void RefreshSessions() => _workspaceCommands.RefreshSessions();

    private void RunSlash(string command)
    {
        _effects.Run(new TuiEffect.RunSlash(command));
        _shellChrome.CloseOverlay(OverlayIds.Palette);
    }
}
