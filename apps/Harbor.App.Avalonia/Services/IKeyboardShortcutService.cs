namespace Harbor.App.Avalonia.Services;

/// <summary>
///     #2682 Z06: read the human-readable form of a global shortcut. The
///     palette shows this beside a command so the binding is visible before
///     the user commits to a row.
/// </summary>
/// <remarks>
///     Dispatch stays in <see cref="KeyboardShortcutService.HandleKeyDown" />;
///     this port is display-only and cannot change which key does what.
/// </remarks>
public interface IKeyboardShortcutService
{
    /// <summary>The display text for a palette command id, or empty when it has no global binding.</summary>
    string GetKeyText(string commandId);
}

/// <summary>
///     #2682 Z06: the display half of the shell's global chords, keyed by the
///     stable command ids the palette already uses as its <c>Hint</c> column.
/// </summary>
/// <remarks>
///     Kept as a pure table so it can be pinned without booting Avalonia or
///     resolving the shell's whole DI graph. Unknown ids return the empty
///     string: a command with no chord must not paint an empty <c>Kbd</c> chip.
/// </remarks>
public static class KeyboardShortcutHints
{
    private static readonly IReadOnlyDictionary<string, string> ByCommand =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Palette"] = "Ctrl+P",
            ["SidebarToggle"] = "Ctrl+B",
            ["ThemeToggle"] = "Ctrl+Shift+T",
            ["TerminalPane"] = "Ctrl+`",
            ["FileOpen"] = "Ctrl+O",
            ["FileSave"] = "Ctrl+S",
            ["ChatClear"] = "Ctrl+L",
        };

    /// <summary>Return the chip text for <paramref name="commandId"/>, or an empty string.</summary>
    public static string Get(string commandId) =>
        ByCommand.TryGetValue(commandId, out string? text) ? text ?? string.Empty : string.Empty;
}
