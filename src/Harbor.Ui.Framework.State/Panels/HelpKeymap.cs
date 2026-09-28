namespace Harbor.Ui.Framework.Panels;

/// <summary>
///     Single source of truth for the builtin help-panel hotkey table. Both the
///     Spectre (<c>contrib/tui/Harbor.Tui.SpectreTui/Panels/Builtin/HelpPanel</c>)
///     and CellForge (<c>CellForgeHelpPanel</c>) help panels render these rows —
///     each in its own widget style (markup vs plain cells) — so the keymap text
///     can no longer drift between renderers. Mirrors the
///     <see cref="Harbor.Ui.Framework.State.ChatKeyMap"/> default bindings.
/// </summary>
public static class HelpKeymap
{
    /// <summary>One hotkey row: the key gesture plus what it does.</summary>
    public sealed record Entry(string Key, string Description);

    /// <summary>Hotkey rows in display order.</summary>
    public static IReadOnlyList<Entry> Rows { get; } =
    [
        new("Alt+1..9", "toggle Nth panel"),
        new("Ctrl+Tab", "cycle panel focus (next tab when a tab strip is open)"),
        new("Ctrl+↑/↓", "grow / shrink focused panel"),
        new("q / Esc", "return focus to chat"),
        new("Ctrl+Tab / Ctrl+Shift+Tab", "next / previous session tab"),
        new("Ctrl+T", "open / switch session tab"),
        new("Ctrl+W", "close current session tab"),
        new("?", "toggle this help panel"),
        new("F2", "toggle input/chat focus"),
        new("F12", "toggle logs panel (live ILogger output)"),
        new("Ctrl+L", "clear transcript"),
        new("Ctrl+C", "abort running agent"),
        new("Esc", "quit"),
    ];
}
