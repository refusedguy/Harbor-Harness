using System.Text;
using Harbor.Ui.Framework.State;

namespace Harbor.Ui.Framework.Projection;

/// <summary>What a Ctrl+P palette row points at.</summary>
public enum PaletteRowKind
{
    /// <summary>A slash command from <see cref="ChatCommands.Slash" />.</summary>
    Command = 0,

    /// <summary>A registered panel id.</summary>
    Panel = 1,

    /// <summary>A recent session title.</summary>
    Session = 2,
}

/// <summary>
///     One Ctrl+P palette row. <paramref name="Text" /> is already
///     <see cref="PaletteRows.Sanitize" />d — a single line with no ANSI escape
///     sequence and no control character left in it — so a renderer may
///     interpolate it into whatever output dialect it speaks without
///     re-deciding whether untrusted text is safe.
/// </summary>
/// <param name="Kind">What the row points at; drives the renderer's colour.</param>
/// <param name="Text">Sanitized display text.</param>
public readonly record struct PaletteRow(PaletteRowKind Kind, string Text);

/// <summary>
///     Shared row builder for the Ctrl+P command palette: slash commands +
///     registered panels + recent sessions, filtered by a
///     <c>Contains(OrdinalIgnoreCase)</c> query and capped at
///     <see cref="MaxCommands" /> / <see cref="MaxPanels" /> /
///     <see cref="MaxSessions" /> rows, exactly as the three contrib shells did
///     independently.
/// </summary>
/// <remarks>
///     <para>
///         Issue #554: RazorConsole's palette escaped the query / panel / session
///         text before interpolating it, Termina and TerminalGui did not — the
///         same pasted text was handled differently depending on which backend
///         the user had selected, and a session title carrying an ANSI escape
///         reached the terminal verbatim in two of the three. The untrusted half
///         of that fix lives here, in one place, so a renderer cannot opt out of
///         it by forgetting a call: every row text is scrubbed of escape
///         sequences and control characters on the way out of
///         <see cref="Build" />.
///     </para>
///     <para>
///         Markup escaping is deliberately NOT duplicated here because it is
///         dialect-specific: a backend that emits Spectre markup still has to
///         double <c>[</c> and <c>]</c> on the way out (RazorConsole's
///         <c>Escape</c>), while a backend that emits ANSI or plain text has
///         nothing left to escape once the escape sequences are gone.
///     </para>
/// </remarks>
public static class PaletteRows
{
    /// <summary>Row cap for slash commands.</summary>
    public const int MaxCommands = 8;

    /// <summary>Row cap for panels, counted together with the commands above it.</summary>
    public const int MaxPanels = 12;

    /// <summary>Row cap for sessions, counted together with everything above it.</summary>
    public const int MaxSessions = 16;

    private const char Esc = '\u001B';
    private const char Bel = '\u0007';
    private const char CsiFinalLo = '\u0040';
    private const char CsiFinalHi = '\u007E';
    private const char InterLo = '\u0020';
    private const char InterHi = '\u002F';

    /// <summary>
    ///     Build the visible rows for <paramref name="query" />: slash commands
    ///     first, then panels, then sessions, each capped and each already
    ///     sanitized.
    /// </summary>
    public static IReadOnlyList<PaletteRow> Build(
        string query,
        IReadOnlyList<string> panels,
        IReadOnlyList<string> sessions)
    {
        ArgumentNullException.ThrowIfNull(panels);
        ArgumentNullException.ThrowIfNull(sessions);

        var rows = new List<PaletteRow>(MaxSessions + 4);
        int shown = 0;

        foreach (string cmd in ChatCommands.Slash)
        {
            if (!Matches(query, cmd))
            {
                continue;
            }

            rows.Add(new PaletteRow(PaletteRowKind.Command, Sanitize(cmd)));
            if (++shown >= MaxCommands)
            {
                break;
            }
        }

        foreach (string panel in panels)
        {
            if (!Matches(query, panel))
            {
                continue;
            }

            rows.Add(new PaletteRow(PaletteRowKind.Panel, Sanitize(panel)));
            if (++shown >= MaxPanels)
            {
                break;
            }
        }

        foreach (string session in sessions)
        {
            if (!Matches(query, session))
            {
                continue;
            }

            rows.Add(new PaletteRow(PaletteRowKind.Session, Sanitize(session)));
            if (++shown >= MaxSessions)
            {
                break;
            }
        }

        return rows;
    }

    /// <summary>
    ///     Make untrusted palette text safe to interpolate: ANSI escape sequences
    ///     (CSI / OSC / two-character forms) and lone escapes are dropped, a
    ///     remaining CR / LF / TAB collapses to a space so a multi-line session
    ///     title cannot break out of the popup frame, and every other control
    ///     character (including DEL and the C1 range) is dropped. Printable text,
    ///     markup metacharacters and full Unicode survive unchanged — a title
    ///     reading <c>]red[</c> is still shown verbatim by every backend.
    /// </summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        ReadOnlySpan<char> source = text;
        if (!NeedsScrubbing(source))
        {
            return text;
        }

        var sb = new StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];
            if (c == Esc)
            {
                // SkipEscapeSequence returns the LAST index it consumed, so the
                // +1 steps over the whole sequence (a lone trailing ESC yields
                // the end of the string).
                i = SkipEscapeSequence(source, i) + 1;
                continue;
            }

            if (c is '\r' or '\n' or '\t')
            {
                sb.Append(' ');
            }
            else if (!char.IsControl(c))
            {
                sb.Append(c);
            }

            i++;
        }

        return sb.ToString();
    }

    private static bool Matches(string query, string candidate) =>
        string.IsNullOrEmpty(query)
        || candidate.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static bool NeedsScrubbing(ReadOnlySpan<char> text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsControl(text[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Index of the LAST character consumed by the escape sequence starting at
    ///     <paramref name="escIndex" /> (which must hold <see cref="Esc" />), so the
    ///     caller can step over the whole sequence with a single <c>+ 1</c>.
    /// </summary>
    private static int SkipEscapeSequence(ReadOnlySpan<char> text, int escIndex)
    {
        int next = escIndex + 1;
        if (next >= text.Length)
        {
            return escIndex;
        }

        return text[next] switch
        {
            '[' => SkipCsi(text, next),
            ']' or 'P' or 'X' or '^' or '_' => SkipStringSequence(text, next),
            _ => SkipTwoOrThreeByte(text, next),
        };
    }

    /// <summary>CSI: parameters and intermediates, then exactly one final byte.</summary>
    private static int SkipCsi(ReadOnlySpan<char> text, int bracket)
    {
        int i = bracket + 1;
        while (i < text.Length && (text[i] < CsiFinalLo || text[i] > CsiFinalHi))
        {
            i++;
        }

        return i < text.Length ? i : text.Length - 1;
    }

    /// <summary>OSC / DCS / SOS / PM / APC: runs to BEL or to the ESC-backslash terminator.</summary>
    private static int SkipStringSequence(ReadOnlySpan<char> text, int introducer)
    {
        int i = introducer + 1;
        while (i < text.Length)
        {
            if (text[i] == Bel)
            {
                return i;
            }

            if (text[i] == Esc)
            {
                // ESC \ = ST; a lone embedded ESC is swallowed up to itself.
                bool isSt = i + 1 < text.Length && text[i + 1] == '\\';
                return isSt ? i + 1 : i;
            }

            i++;
        }

        return text.Length - 1;
    }

    /// <summary>Two- and three-byte forms (ESC 7, ESC M, ESC ( B) plus their intermediates.</summary>
    private static int SkipTwoOrThreeByte(ReadOnlySpan<char> text, int i)
    {
        if (text[i] >= InterLo && text[i] <= InterHi)
        {
            i++;
            while (i < text.Length && text[i] >= InterLo && text[i] <= InterHi)
            {
                i++;
            }
        }

        return i < text.Length ? i : text.Length - 1;
    }
}
