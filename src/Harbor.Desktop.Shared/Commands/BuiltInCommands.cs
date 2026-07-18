using Harbor.Desktop.Abstractions.Models;

namespace Harbor.Desktop.Shared.Commands;

/// <summary>
///     Catalog of built-in command-palette items shared by every desktop app.
/// Each platform app maps these to its own actions (which use platform-specific
/// services) — this catalog just defines the title / subtitle / icon strings
/// so the palette UI looks identical across platforms.
/// </summary>
public static class BuiltInCommands
{
    /// <summary>
    ///     Build the default list of built-in command-palette item templates.
    /// Each template's <see cref="CommandPaletteItem.Action"/> is a no-op —
    /// the platform app is expected to subscribe to the template's title
    /// (or wrap the action with its own dispatcher) when binding.
    /// </summary>
    /// <remarks>
    ///     Returned as a list (not a static field) so each platform app gets
    ///     its own copy and can replace the no-op action with a real one.
    /// </remarks>
    public static IReadOnlyList<CommandPaletteItem> Templates() =>
    [
        new CommandPaletteItem(
            Title: "Open Session",
            Subtitle: "Open an existing chat session",
            Icon: "FolderIcon",
            Action: static () => { }),
        new CommandPaletteItem(
            Title: "New Session",
            Subtitle: "Start a fresh chat session",
            Icon: "PlusIcon",
            Action: static () => { }),
        new CommandPaletteItem(
            Title: "Branch Session",
            Subtitle: "Branch the current session at the selected message",
            Icon: "BranchIcon",
            Action: static () => { }),
        new CommandPaletteItem(
            Title: "Toggle Theme",
            Subtitle: "Switch between dark and light",
            Icon: "ThemeIcon",
            Action: static () => { }),
        new CommandPaletteItem(
            Title: "Open Code Editor",
            Subtitle: "Open the built-in code editor",
            Icon: "CodeIcon",
            Action: static () => { }),
        new CommandPaletteItem(
            Title: "Open Diff View",
            Subtitle: "Open the diff viewer",
            Icon: "DiffIcon",
            Action: static () => { }),
        new CommandPaletteItem(
            Title: "Open Token Usage",
            Subtitle: "Show per-session token usage and cost",
            Icon: "ChartIcon",
            Action: static () => { }),
        new CommandPaletteItem(
            Title: "Open Settings",
            Subtitle: "Configure providers, theme, fonts",
            Icon: "SettingsIcon",
            Action: static () => { }),
        new CommandPaletteItem(
            Title: "Open Provider Browser",
            Subtitle: "Browse and configure LLM providers",
            Icon: "ProviderIcon",
            Action: static () => { }),
        new CommandPaletteItem(
            Title: "Quit",
            Subtitle: "Exit Harbor",
            Icon: "QuitIcon",
            Action: static () => { }),
    ];
}
