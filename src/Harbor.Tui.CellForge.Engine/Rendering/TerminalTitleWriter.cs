// TerminalTitleWriter.cs, #2680 F124. Composes the terminal title from a
// session label, an optional agent glyph and the activity indicator classified
// by TerminalActivityClassifier (#2680 F125), then wraps it in an OSC 0 or OSC 2
// sequence. This is a pure, BCL-only leaf: it writes the escape sequence into a
// returned string and never touches stdout, so it stays inside the
// CellForge.Engine atomicity perimeter (zero project references, zero Harbor
// imports). The caller decides when to emit the string and which signals feed
// the classifier.
//
// The upstream shape this ports (kilocode/cli/cmd/tui/terminal-title.ts) has a
// format() that blends an indicator and a glyph into a title. This file keeps
// that composition and the OSC framing local, and reuses the F125 indicator so
// one state has one spelling everywhere a title or tab label appears.

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
///     Builds a terminal title and its OSC escape sequence (#2680 F124).
///     Every method is pure and ordinal: the same inputs always produce the
///     same string, with no culture-sensitive formatting and no I/O.
/// </summary>
public static class TerminalTitleWriter
{
    /// <summary>The BEL byte that terminates an OSC control sequence.</summary>
    public const char Bell = '\u0007';

    private const string OscPrefix = "\u001b]";
    private const string DefaultSubject = "Harbor";

    /// <summary>
    ///     Composes the plain title text. The indicator comes first, then the
    ///     glyph if one was supplied, then the subject. A missing or empty
    ///     subject falls back to <see cref="DefaultSubject" />. Control
    ///     characters in the caller's text are removed so it cannot close or
    ///     inject an OSC sequence.
    /// </summary>
    /// <param name="subject">A session name, project name or other label.</param>
    /// <param name="glyph">An optional agent glyph, supplied by the caller.</param>
    /// <param name="state">The activity state to show as the indicator.</param>
    public static string Format(string? subject, string? glyph, TerminalActivityState state)
    {
        var indicator = TerminalActivityClassifier.Format(state);
        var safeSubject = Sanitize(subject);
        if (safeSubject.Length == 0) safeSubject = DefaultSubject;
        var safeGlyph = Sanitize(glyph);

        return safeGlyph.Length == 0
            ? indicator + " " + safeSubject
            : indicator + " " + safeGlyph + " " + safeSubject;
    }

    /// <summary>Wraps <paramref name="title" /> in an OSC 0 sequence.</summary>
    public static string Osc0(string title) => Wrap("0", title);

    /// <summary>Wraps <paramref name="title" /> in an OSC 2 sequence.</summary>
    public static string Osc2(string title) => Wrap("2", title);

    /// <summary>
    ///     Composes the title and wraps it in OSC 0, the sequence most
    ///     terminals accept as both the window and the icon title.
    /// </summary>
    public static string Build(string? subject, string? glyph, TerminalActivityState state)
        => Osc0(Format(subject, glyph, state));

    private static string Wrap(string code, string title)
        => OscPrefix + code + ";" + Sanitize(title) + Bell;

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var needsScrub = false;
        foreach (var c in value)
        {
            if (c == '\u001b' || c == Bell || c < ' ')
            {
                needsScrub = true;
                break;
            }
        }

        if (!needsScrub) return value;

        var chars = value.ToCharArray();
        var write = 0;
        foreach (var c in chars)
        {
            if (c == '\u001b' || c == Bell || c < ' ') continue;
            chars[write++] = c;
        }

        return new string(chars, 0, write);
    }
}
