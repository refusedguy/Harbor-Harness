using Harbor.App.Avalonia.Services;
using Harbor.Abstractions.Tools;
using Harbor.Desktop.ViewModels;
using Harbor.Ui.Framework.Commands;
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
    private readonly IKeyboardShortcutService _shortcuts;
    private readonly ILogger<CommandPaletteViewModel> _logger;
    private readonly IFloatingTerminals? _floatingTerminals;

    /// <summary>Construct the palette view-model.</summary>
    public CommandPaletteViewModel(
        IDispatcherAdapter dispatcher,
        ILogger<CommandPaletteViewModel> logger,
        IShellChrome shellChrome,
        IWorkspaceCommands workspaceCommands,
        TuiEffectHost effects,
        IKeyboardShortcutService shortcuts,
        IFloatingTerminals? floatingTerminals = null)
        : base(dispatcher, logger)
    {
        _shellChrome = shellChrome;
        _workspaceCommands = workspaceCommands;
        _effects = effects;
        _shortcuts = shortcuts;
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
            // #2682 Z06: the chip text comes from the shell's shortcut service,
            // keyed by the same id the Hint column carries. Labels keep their
            // parenthesized chord so a query like "ctrl+b" still finds the row.
            new("command", "Toggle sidebar (Ctrl+B)", "SidebarToggle", ToggleSidebar,
                _shortcuts.GetKeyText("SidebarToggle")),
            new("command", "Toggle theme (Ctrl+Shift+T)", "ThemeToggle", ToggleTheme,
                _shortcuts.GetKeyText("ThemeToggle")),
            new("command", "Floating terminal pane (Ctrl+`)", "TerminalPane", ToggleTerminalPane,
                _shortcuts.GetKeyText("TerminalPane")),
            new("command", "New session", "SessionNew", NewSession),
            new("command", "Branch active session", "SessionBranch", BranchSession),
            new("command", "Open file (Ctrl+O)", "FileOpen", OpenFile,
                _shortcuts.GetKeyText("FileOpen")),
            new("command", "Save file (Ctrl+S)", "FileSave", SaveFile,
                _shortcuts.GetKeyText("FileSave")),
            new("command", "Stop agent", "AgentStop", StopAgent),
            new("command", "Clear chat (Ctrl+L)", "ChatClear", ClearChat,
                _shortcuts.GetKeyText("ChatClear")),
            new("command", "Refresh session list", "SessionRefresh", RefreshSessions),
        });

        // #462: the slash half is projected from the single registry the CLI
        // dispatcher binds handlers to. This used to be a 12-entry literal that
        // had drifted (it omitted /new, /permissions, /plugins, /skills,
        // /tree, /fork and /renderer while offering /clear, which the
        // dispatcher cannot run).
        foreach (SlashCommandDefinition def in SlashCommandCatalog.All)
        {
            AllCommands.Add(new CommandResultViewModel(
                "slash", def.Invocation, def.Description, () => RunSlash(def.Invocation)));
        }

        // #2682 Z03: seed the alias table with the vocabulary the CLI
        // dispatcher already accepts ("h" -> "/help", "quit" -> "/exit",
        // "m" -> "/model", ...). Without this, typing a documented alias in
        // the Avalonia palette found nothing: neither the invocation nor the
        // description contains the synonym. A settings layer can add more
        // entries through the inherited CommandAliases dictionary.
        foreach (SlashCommandDefinition def in SlashCommandCatalog.All)
        {
            foreach (string alias in def.Aliases)
            {
                if (!string.IsNullOrWhiteSpace(alias))
                {
                    CommandAliases[alias.Trim()] = def.Invocation;
                }
            }
        }

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

    // #569: CommandResultViewModel holds an `Action`, so these two callbacks
    // cannot await — the palette's command table is synchronous by contract.
    // They used to discard the Task bare, which meant a Ctrl+O / Ctrl+S routed
    // through the palette could fail with no log at all. TaskFireAndForget
    // keeps the sync contract and still observes the fault.
    private void OpenFile() => TaskFireAndForget.Forget(
        _workspaceCommands.OpenFileAsync(),
        ex => CommandPaletteViewModelLog.PaletteOpenFileFailed(_logger, ex));
    private void SaveFile() => TaskFireAndForget.Forget(
        _workspaceCommands.SaveFileAsync(),
        ex => CommandPaletteViewModelLog.PaletteSaveFileFailed(_logger, ex));

    private void StopAgent() => _workspaceCommands.StopAgent();
    private void ClearChat() => _workspaceCommands.ClearChat();
    private void RefreshSessions() => _workspaceCommands.RefreshSessions();

    private void RunSlash(string command)
    {
        _effects.Run(new TuiEffect.RunSlash(command));
        _shellChrome.CloseOverlay(OverlayIds.Palette);
    }
}

/// <summary>
///     SG14: BCL <c>[LoggerMessage]</c> delegates for <see cref="CommandPaletteViewModel" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class CommandPaletteViewModelLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Palette open-file failed")]
    public static partial void PaletteOpenFileFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Palette save-file failed")]
    public static partial void PaletteSaveFileFailed(ILogger logger, Exception ex);
}
