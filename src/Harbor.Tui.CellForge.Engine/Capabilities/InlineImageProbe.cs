namespace Harbor.Tui.CellForge.Capabilities;

/// <summary>Inline-image protocols the runtime can speak.</summary>
public enum InlineImageKind : byte
{
    /// <summary>No inline graphics — timeline keeps the text description card.</summary>
    None = 0,

    /// <summary>iTerm2-family OSC 1337 (iTerm2, WezTerm, Konsole, mintty).</summary>
    Osc1337 = 1,

    /// <summary>kitty APC graphics (DCS _G … ESC \) — PNG payloads only.</summary>
    KittyApc = 2,
}

/// <summary>
/// Inline-image capability detection (osc-sprint §1337). There is no wire
/// probe for either protocol, so detection is environmental — the same
/// discipline as <see cref="CapabilityProber.IsInsideMultiplexer" />:
/// 1. explicit override HARBOR_INLINE_IMAGE (auto|off|osc1337|kitty);
/// 2. multiplexer guard — tmux/screen passthrough is out of scope;
/// 3. kitty family (APC): KITTY_WINDOW_ID / KITTY_PID / TERM xterm-kitty;
/// 4. OSC 1337 family: iTerm2, WezTerm, Konsole, mintty;
/// 5. otherwise None — the text card is the graceful fallback.
/// </summary>
public static class InlineImageProbe
{
    private const string OverrideVar = "HARBOR_INLINE_IMAGE";

    /// <summary>Detects the inline-image protocol for the current session.</summary>
    public static InlineImageKind Detect(Func<string, string?>? environmentLookup = null)
    {
        var lookup = environmentLookup ?? Environment.GetEnvironmentVariable;
        switch (NormalizeOverride(lookup(OverrideVar)))
        {
            case "off": return InlineImageKind.None;
            case "osc1337": return InlineImageKind.Osc1337;
            case "kitty": return InlineImageKind.KittyApc;
        }

        if (IsInsideMultiplexer(lookup))
        {
            return InlineImageKind.None;
        }

        if (IsKittyFamily(lookup))
        {
            return InlineImageKind.KittyApc;
        }

        return IsOsc1337Family(lookup) ? InlineImageKind.Osc1337 : InlineImageKind.None;
    }

    private static string? NormalizeOverride(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    /// <summary>True inside tmux/screen — passthrough wrappers are out of scope
    /// (mirrors the kitty-keyboard guardrail in <see cref="CapabilityProber" />).</summary>
    public static bool IsInsideMultiplexer(Func<string, string?> lookup) =>
        !string.IsNullOrEmpty(lookup("TMUX")) || !string.IsNullOrEmpty(lookup("STY"));

    private static bool IsKittyFamily(Func<string, string?> lookup) =>
        !string.IsNullOrEmpty(lookup("KITTY_WINDOW_ID"))
        || !string.IsNullOrEmpty(lookup("KITTY_PID"))
        || lookup("TERM")?.Contains("kitty", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsOsc1337Family(Func<string, string?> lookup)
    {
        string? termProgram = lookup("TERM_PROGRAM");
        return !string.IsNullOrEmpty(lookup("ITERM_SESSION_ID"))
               || !string.IsNullOrEmpty(lookup("WEZTERM_EXECUTABLE"))
               || !string.IsNullOrEmpty(lookup("KONSOLE_VERSION"))
               || IsTermProgram(termProgram, "iTerm.app")
               || IsTermProgram(termProgram, "WezTerm")
               || IsTermProgram(termProgram, "mintty");
    }

    private static bool IsTermProgram(string? termProgram, string expected) =>
        string.Equals(termProgram, expected, StringComparison.OrdinalIgnoreCase);
}
