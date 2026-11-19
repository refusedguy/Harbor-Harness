// AgentTitleGlyph.cs, #2680 F174. Supplies the agent half of a terminal title.
// F124's TerminalTitleWriter already composes indicator + glyph + subject and
// frames the result in OSC, but leaves the glyph to its caller. This map gives
// each built-in agent a stable glyph and a fallback for names it does not know.
// Pure, BCL-only leaf: no Harbor imports, no I/O and no clock, so it stays
// inside the CellForge.Engine atomicity perimeter (zero project references,
// zero Harbor imports).

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
///     Stable title glyphs for the built-in agent names (#2680 F174). Lookup is
///     case-insensitive and trims surrounding whitespace; a missing or unknown
///     name yields <see cref="Fallback" /> rather than an empty title slot.
/// </summary>
/// <remarks>
///     The glyph vocabulary follows the one tools already declare through their
///     own <c>Glyph</c> member: one short, stable mark per named actor. The
///     engine cannot import the agent registry, so it owns this small table and
///     the caller projects its agent name into it.
/// </remarks>
public static class AgentTitleGlyph
{
    /// <summary>Used when the caller supplies no agent or an unknown name.</summary>
    public const string Fallback = "🤖";

    private static readonly Dictionary<string, string> Glyphs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["code"] = "🧠",
        ["plan"] = "📐",
        ["ask"] = "💬",
        ["explore"] = "🔍",
        ["architect"] = "🏗",
        ["debug"] = "🐛",
        ["orchestrator"] = "🎛"
    };

    /// <summary>
    ///     Returns the glyph for <paramref name="agentName" />, or
    ///     <see cref="Fallback" /> when it is missing or unrecognised.
    /// </summary>
    public static string For(string? agentName)
    {
        if (string.IsNullOrWhiteSpace(agentName)) return Fallback;
        return Glyphs.TryGetValue(agentName.Trim(), out string? glyph) ? glyph! : Fallback;
    }

    /// <summary>
    ///     Builds an OSC 0 terminal title with the agent's glyph, delegating the
    ///     composition and control-character scrubbing to
    ///     <see cref="TerminalTitleWriter" />. This is the "reuse the glyph in
    ///     the title" half of F174.
    /// </summary>
    /// <param name="subject">A session or project label.</param>
    /// <param name="agentName">The agent whose glyph should appear in the title.</param>
    /// <param name="state">The activity state shown as the leading indicator.</param>
    public static string Build(string? subject, string? agentName, TerminalActivityState state)
        => TerminalTitleWriter.Build(subject, For(agentName), state);
}
