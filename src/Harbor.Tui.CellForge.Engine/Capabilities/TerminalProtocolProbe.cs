namespace Harbor.Tui.CellForge.Capabilities;

/// <summary>
/// Environmental capability detection for the cheap OSC/CSI protocols added
/// in issue #2219 (OSC 7501 program status, OSC 9;4 progress, OSC 7 cwd,
/// DECSCUSR cursor shape, underline styles/color).
///
/// None of these has a reliable wire query that is safe to send in every
/// terminal, so detection follows the same shape as
/// <see cref="Osc8HyperlinkProbe"/>: explicit override, multiplexer guard,
/// known-terminal allow-list, otherwise off. Emitters stay inert on the
/// fallback path, so an unknown terminal receives only the plain frame and
/// no protocol bytes can corrupt it. Every switch is injectable for tests.
/// </summary>
public static class TerminalProtocolProbe
{
    public const string Osc7501Override = "HARBOR_OSC7501";
    public const string Osc94Override = "HARBOR_OSC94_PROGRESS";
    public const string Osc7Override = "HARBOR_OSC7_CWD";
    public const string DecscusrOverride = "HARBOR_DECSCUSR";
    public const string UnderlineOverride = "HARBOR_UNDERLINE_STYLES";

    public static bool DetectOsc7501(Func<string, string?>? lookup = null) =>
        Detect(lookup, Osc7501Override, knownTerminal: true);

    public static bool DetectOsc94Progress(Func<string, string?>? lookup = null) =>
        Detect(lookup, Osc94Override, knownTerminal: true);

    public static bool DetectOsc7Cwd(Func<string, string?>? lookup = null) =>
        Detect(lookup, Osc7Override, knownTerminal: true);

    public static bool DetectDecscusr(Func<string, string?>? lookup = null) =>
        Detect(lookup, DecscusrOverride, knownTerminal: true);

    /// <summary>
    /// Underline styles (SGR 4:3/4:4/4:5) and underline color (SGR 58/59) are
    /// supported by the same modern terminal family. Singular SGR 4 stays on
    /// everywhere, so disabling this switch only suppresses the extended bytes.
    /// </summary>
    public static bool DetectUnderlineStyles(Func<string, string?>? lookup = null) =>
        Detect(lookup, UnderlineOverride, knownTerminal: true);

    /// <summary>
    /// Shared ladder: override → multiplexer guard → known terminal → off.
    /// </summary>
    private static bool Detect(Func<string, string?>? lookup, string overrideVar, bool knownTerminal)
    {
        var get = lookup ?? Environment.GetEnvironmentVariable;
        string? value = get(overrideVar)?.Trim().ToLowerInvariant();
        if (value is "off" or "0" or "false")
        {
            return false;
        }

        if (value is "on" or "1" or "true")
        {
            return true;
        }

        if (!string.IsNullOrEmpty(get("TMUX")) || !string.IsNullOrEmpty(get("STY")))
        {
            return false;
        }

        if (!knownTerminal)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(get("WT_SESSION")))
        {
            return true;
        }

        string? termProgram = get("TERM_PROGRAM");
        if (string.Equals(termProgram, "iTerm.app", StringComparison.OrdinalIgnoreCase)
            || string.Equals(termProgram, "WezTerm", StringComparison.OrdinalIgnoreCase)
            || string.Equals(termProgram, "Apple_Terminal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(get("KITTY_WINDOW_ID")) || !string.IsNullOrEmpty(get("KITTY_PID")))
        {
            return true;
        }

        string? term = get("TERM");
        return term?.Contains("kitty", StringComparison.OrdinalIgnoreCase) == true
            || term?.Contains("ghostty", StringComparison.OrdinalIgnoreCase) == true;
    }
}
