using Harbor.Desktop.Abstractions.Models;
namespace Harbor.Desktop.Shared.Commands;
/// <summary>
///     Catalog of built-in command-palette items shared by every desktop app.
///     Each platform app maps these to its own actions (which use platform-specific
///     services) — this catalog just defines the title / subtitle / icon strings
///     so the palette UI looks identical across platforms.
/// </summary>
public static class BuiltInCommands
{
    /// <summary>
    ///     Build the default list of built-in command-palette item templates.
    ///     Each template's <see cref="CommandPaletteItem.Action" /> is a no-op —
    ///     the platform app is expected to subscribe to the template's title
    ///     (or wrap the action with its own dispatcher) when binding.
    /// </summary>
    /// <remarks>
    ///     Returned as a list (not a static field) so each platform app gets
    ///     its own copy and can replace the no-op action with a real one.
    /// </remarks>
    public static IReadOnlyList<CommandPaletteItem> Templates() =>
    [
        new(
            "Open Session",
            "Open an existing chat session",
            "FolderIcon",
            static () => { }),
        new(
            "New Session",
            "Start a fresh chat session",
            "PlusIcon",
            static () => { }),
        new(
            "Branch Session",
            "Branch the current session at the selected message",
            "BranchIcon",
            static () => { }),
        new(
            "Toggle Theme",
            "Switch between dark and light",
            "ThemeIcon",
            static () => { }),
        new(
            "Open Code Editor",
            "Open the built-in code editor",
            "CodeIcon",
            static () => { }),
        new(
            "Open Diff View",
            "Open the diff viewer",
            "DiffIcon",
            static () => { }),
        new(
            "Open Token Usage",
            "Show per-session token usage and cost",
            "ChartIcon",
            static () => { }),
        new(
            "Open Settings",
            "Configure providers, theme, fonts",
            "SettingsIcon",
            static () => { }),
        new(
            "Open Provider Browser",
            "Browse and configure LLM providers",
            "ProviderIcon",
            static () => { }),
        new(
            "Quit",
            "Exit Harbor",
            "QuitIcon",
            static () => { })
    ];
}
