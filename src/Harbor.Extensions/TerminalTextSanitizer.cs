using System.Text;

namespace Harbor.Abstractions.Extensions;

/// <summary>
///     Makes model/tool-supplied text inert before a terminal paints it
///     (#2678 item 100, goose <c>sanitize_tool_confirmation_line/text</c>).
///     Tool names and arguments are attacker-controlled: an embedded ESC or a
///     bidirectional control can rewrite the terminal state or spoof the order
///     of characters in a permission prompt, so the confirmation line and card
///     must never paint those code points verbatim.
///     <para>
///         The sanitizer is a pure, allocation-free-on-clean-input string
///         transform. It keeps printable text (including Unicode) and the
///         harmless whitespace <c>\n</c>/<c>\t</c>, drops C0/C1 controls, DEL,
///         and the bidi formatting characters (ALM, LRE/RLE, LRO/RLO, PDF,
///         LRI/RLI/FSI/PDI). Dropping ESC alone disarms a sequence because its
///         parameter bytes then paint as ordinary text. The transform uses no
///         reflection, performs no I/O, and needs only the BCL, so it is
///         AOT-safe.
///     </para>
/// </summary>
public static class TerminalTextSanitizer
{
    /// <summary>
    ///     Sanitizes multi-line display text, preserving line feeds and tabs.
    ///     Returns the original instance when nothing had to be removed (the
    ///     common case), and <see cref="string.Empty" /> for null input.
    /// </summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        int dirty = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (IsUnsafe(text[i]))
            {
                dirty = i;
                break;
            }
        }

        if (dirty < 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        sb.Append(text, 0, dirty);
        for (int i = dirty; i < text.Length; i++)
        {
            char c = text[i];
            if (!IsUnsafe(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Sanitizes a single terminal line: CR/LF/tab collapse to a space,
    ///     every other control and all bidi controls are dropped, and the
    ///     result is trimmed. This is the shape a confirmation header or a
    ///     one-line tool argument needs; the fast path avoids allocation when
    ///     the input is already clean.
    /// </summary>
    public static string SanitizeSingleLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        int dirty = -1;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (IsUnsafe(c) || c is '\n' or '\r' or '\t')
            {
                dirty = i;
                break;
            }
        }

        if (dirty < 0)
        {
            return text.Trim();
        }

        var sb = new StringBuilder(text.Length);
        sb.Append(text, 0, dirty);
        for (int i = dirty; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '\n' or '\r' or '\t')
            {
                sb.Append(' ');
            }
            else if (!IsUnsafe(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    ///     True for code points that must never reach a terminal as-is.
    ///     Tab and line feed are deliberately excluded: callers decide
    ///     whether to keep or collapse them.
    /// </summary>
    private static bool IsUnsafe(char c) =>
        (c < ' ' && c is not ('\n' or '\t'))   // C0 controls, including ESC
        || c == '\u007f'                       // DEL
        || (c >= '\u0080' && c <= '\u009f')    // C1 controls
        || c == '\u061c'                       // ARABIC LETTER MARK
        || (c >= '\u200e' && c <= '\u200f')    // LRM / RLM
        || (c >= '\u202a' && c <= '\u202e')    // LRE / RLE / PDF / LRO / RLO
        || (c >= '\u2066' && c <= '\u2069');   // LRI / RLI / FSI / PDI
}
