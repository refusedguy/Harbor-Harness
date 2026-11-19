// F132 (issue #2680 part 2/2): env-only half of a "system" theme — resolve the
// terminal's colour capability and background hint from TERM/COLORTERM/NO_COLOR.
// Complements TerminalBackgroundProbe (an OSC 11 wire query): this probe needs no
// round-trip, so it is the fallback for terminals that never answer a query.

namespace Harbor.Tui.CellForge.Capabilities;

/// <summary>Palette width a terminal advertises through its environment.</summary>
public enum TerminalColorDepth : byte
{
    /// <summary>No 256-colour or truecolor advertisement — 16-colour fallback.</summary>
    Standard = 0,

    /// <summary><c>TERM</c> advertises a 256-colour entry.</summary>
    Colors256 = 1,

    /// <summary><c>COLORTERM</c> advertises 24-bit colour.</summary>
    TrueColor = 2,
}

/// <summary>
/// Background hint derived from <c>COLORFGBG</c>. This is a hint, not a
/// measurement: <see cref="TerminalBackgroundProbe"/> with an OSC 11 response
/// is authoritative when the terminal answers the query.
/// </summary>
public enum TerminalBackground : byte
{
    /// <summary>No usable <c>COLORFGBG</c> hint — let the host keep its default.</summary>
    Unknown = 0,

    /// <summary>Non-white background — pick a dark-surface theme.</summary>
    Dark = 1,

    /// <summary>White/bright-white background — pick a light-surface theme.</summary>
    Light = 2,
}

/// <summary>
/// The env-derived "system theme" decision: how many colours the terminal can
/// show, whether colour is explicitly disabled, and which background family it
/// reports. Pure value type; hosts map it onto their own theme catalog.
/// </summary>
/// <param name="Depth">Advertised palette width.</param>
/// <param name="Background">Background family hint.</param>
/// <param name="ColorsDisabled">True when colour output must be suppressed.</param>
public readonly record struct SystemThemeProfile(
    TerminalColorDepth Depth,
    TerminalBackground Background,
    bool ColorsDisabled)
{
    /// <summary>
    /// The depth a renderer should actually use: <see cref="TerminalColorDepth.Standard"/>
    /// once colour is disabled, otherwise the advertised <see cref="Depth"/>.
    /// </summary>
    public TerminalColorDepth EffectiveDepth =>
        ColorsDisabled ? TerminalColorDepth.Standard : Depth;
}

/// <summary>
/// Environmental "follow the terminal" theme detection (F132, issue #2680).
/// There is no portable wire query for palette width, and the OSC 11 background
/// query is not answered by every terminal, so detection is environmental and
/// follows the same injectable shape as <see cref="TerminalProtocolProbe"/>:
/// 1. explicit override <c>HARBOR_THEME_SYSTEM</c> (<c>off</c> disables colour);
/// 2. <c>NO_COLOR</c> (non-empty) disables colour per no-color.org;
/// 3. <c>COLORTERM</c> = truecolor/24bit → <see cref="TerminalColorDepth.TrueColor"/>;
/// 4. <c>TERM</c> containing "256color" → <see cref="TerminalColorDepth.Colors256"/>;
/// 5. otherwise <see cref="TerminalColorDepth.Standard"/>;
/// 6. <c>COLORFGBG</c> second field hints the background family.
///
/// Every switch is injectable for tests. Nothing is wired into the product, no
/// public contract outside this file changes, and no gate or threshold is touched.
/// </summary>
public static class SystemThemeProbe
{
    /// <summary>Override variable: <c>off</c>/<c>0</c>/<c>false</c> disables colour.</summary>
    public const string OverrideVar = "HARBOR_THEME_SYSTEM";

    /// <summary>Resolves the "system theme" profile from the process environment.</summary>
    public static SystemThemeProfile Detect(Func<string, string?>? lookup = null)
    {
        var get = lookup ?? Environment.GetEnvironmentVariable;
        bool disabled = IsColourDisabled(get);
        return new SystemThemeProfile(
            Depth: DetectDepth(get),
            Background: DetectBackground(get),
            ColorsDisabled: disabled);
    }

    /// <summary>
    /// True when colour output must be suppressed: the explicit override says
    /// <c>off</c>, or <c>NO_COLOR</c> is set to a non-empty value (no-color.org
    /// treats presence-with-content as the opt-out).
    /// </summary>
    public static bool IsColourDisabled(Func<string, string?> lookup)
    {
        string? value = Normalize(lookup(OverrideVar));
        if (value is "off" or "0" or "false")
        {
            return true;
        }

        return !string.IsNullOrEmpty(lookup("NO_COLOR"));
    }

    /// <summary>
    /// Palette width from <c>COLORTERM</c> then <c>TERM</c>. A 24-bit
    /// advertisement wins over a 256-colour <c>TERM</c> entry.
    /// </summary>
    public static TerminalColorDepth DetectDepth(Func<string, string?> lookup)
    {
        string? colorTerm = Normalize(lookup("COLORTERM"));
        if (colorTerm is "truecolor" or "24bit")
        {
            return TerminalColorDepth.TrueColor;
        }

        string? term = lookup("TERM");
        return term?.Contains("256color", StringComparison.OrdinalIgnoreCase) == true
            ? TerminalColorDepth.Colors256
            : TerminalColorDepth.Standard;
    }

    /// <summary>
    /// Background family from the second <c>COLORFGBG</c> field (foreground;background).
    /// ANSI white (7) and bright white (15) mean a light surface; any other numeric
    /// value means a dark surface; a missing or malformed field is
    /// <see cref="TerminalBackground.Unknown"/> rather than a guess.
    /// </summary>
    public static TerminalBackground DetectBackground(Func<string, string?> lookup)
    {
        string? value = lookup("COLORFGBG");
        if (string.IsNullOrWhiteSpace(value))
        {
            return TerminalBackground.Unknown;
        }

        string[] fields = value.Split(';');
        if (fields.Length < 2
            || !int.TryParse(fields[0].Trim(), out _)
            || !int.TryParse(fields[1].Trim(), out int background))
        {
            return TerminalBackground.Unknown;
        }

        return background is 7 or 15 ? TerminalBackground.Light : TerminalBackground.Dark;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
