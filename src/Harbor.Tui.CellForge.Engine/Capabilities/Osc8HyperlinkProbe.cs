namespace Harbor.Tui.CellForge.Capabilities;

/// <summary>
/// OSC 8 hyperlink capability detection. OSC 8 has no wire query, so this is
/// environmental detection in the same shape as
/// <see cref="InlineImageProbe.Detect"/>:
/// 1. explicit override HARBOR_OSC8_LINKS (off|on|auto);
/// 2. multiplexer guard — tmux/screen passthrough strips OSC 8;
/// 3. terminals known to speak OSC 8 (Windows Terminal, iTerm2, WezTerm,
///    Apple Terminal, kitty family);
/// 4. otherwise off, so an unknown terminal gets visible text only.
/// </summary>
public static class Osc8HyperlinkProbe
{
    /// <summary>Environment override: "off", "on" or "auto" (default).</summary>
    public const string OverrideVar = "HARBOR_OSC8_LINKS";

    /// <summary>Detects whether the current terminal can render OSC 8 links.</summary>
    public static bool Detect(Func<string, string?>? environmentLookup = null)
    {
        var lookup = environmentLookup ?? Environment.GetEnvironmentVariable;
        string? value = lookup(OverrideVar)?.Trim().ToLowerInvariant();
        if (value is "off" or "0" or "false")
        {
            return false;
        }

        if (value is "on" or "1" or "true")
        {
            return true;
        }

        if (IsInsideMultiplexer(lookup))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(lookup("WT_SESSION")))
        {
            return true;
        }

        string? termProgram = lookup("TERM_PROGRAM");
        if (IsTermProgram(termProgram, "iTerm.app")
            || IsTermProgram(termProgram, "WezTerm")
            || IsTermProgram(termProgram, "Apple_Terminal"))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(lookup("KITTY_WINDOW_ID"))
            || !string.IsNullOrEmpty(lookup("KITTY_PID")))
        {
            return true;
        }

        return lookup("TERM")?.Contains("kitty", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>True inside tmux/screen — OSC 8 passthrough is out of scope.</summary>
    public static bool IsInsideMultiplexer(Func<string, string?> lookup) =>
        !string.IsNullOrEmpty(lookup("TMUX")) || !string.IsNullOrEmpty(lookup("STY"));

    private static bool IsTermProgram(string? termProgram, string expected) =>
        string.Equals(termProgram, expected, StringComparison.OrdinalIgnoreCase);
}
