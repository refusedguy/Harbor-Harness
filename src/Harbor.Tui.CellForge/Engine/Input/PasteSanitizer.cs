using System.Buffers;

namespace Harbor.Tui.CellForge.Input;

/// <summary>Outcome of <see cref="PasteSanitizer.Sanitize" />.</summary>
/// <param name="Text">Sanitized paste buffer. On the clean fast path this is
/// the ORIGINAL string reference (zero allocation).</param>
/// <param name="Modified">True when anything was stripped or normalized.</param>
/// <param name="EscapeSequences">ANSI escape sequences removed (CSI, OSC,
/// DCS/SOS/PM/APC, lone ESC).</param>
/// <param name="ControlChars">Control bytes removed (C0 except \n/\t, DEL,
/// C1) including \r normalizations.</param>
public readonly record struct PasteSanitizeResult(
    string Text,
    bool Modified,
    int EscapeSequences,
    int ControlChars);

/// <summary>
/// Bracketed-paste anti-injection sanitizer (osc-sprint): strips ANSI escape
/// sequences and control bytes from a paste buffer BEFORE it reaches the
/// composer and, through submit, the agent.
///
/// The parser already guarantees paste payloads are never decoded into
/// key/mouse events (PasteEvent contract — newlines can never synthesize
/// Enter). This is the second layer: what lands in the composer and is then
/// routed to the LLM is pure text — no escape sequences that a downstream
/// consumer (markdown renderer, shell block, plugin) could mis-execute.
///
/// Hot path: the scan is stack-only (ReadOnlySpan + SearchValues); a clean
/// paste returns the original string reference with ZERO allocation. A dirty
/// paste allocates exactly once (two-pass <c>string.Create</c> — count, then
/// fill). No intermediate buffers, no regex, AOT-safe.
/// </summary>
public static class PasteSanitizer
{
    private const char Esc = '\u001B';
    private const char Bel = '\u0007';

    /// <summary>Bytes that force the sanitize pass: ESC + C0 except \n/\t
    /// (BEL, CR, SO/SI, …), DEL and the whole C1 range (incl. 0x9B — the
    /// 8-bit CSI intro). \n and \t are legitimate text and never trigger.</summary>
    private static readonly SearchValues<char> Triggers = BuildTriggers();

    /// <summary>Sanitizes a paste buffer: escape sequences and control bytes
    /// out, newlines/tabs/printable text (incl. full Unicode) preserved,
    /// \r\n and lone \r normalized to \n.</summary>
    public static PasteSanitizeResult Sanitize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ReadOnlySpan<char> source = text;

        // Hot path: nothing to strip → the caller keeps the original string.
        if (source.IndexOfAny(Triggers) < 0)
        {
            return new PasteSanitizeResult(text, Modified: false, 0, 0);
        }

        int length = Scan(source, default, write: false, out int escapes, out int controls);
        string result = string.Create(
            length, text,
            static (destination, source) => _ = Scan(source, destination, write: true, out _, out _));
        return new PasteSanitizeResult(result, Modified: true, escapes, controls);
    }

    private static SearchValues<char> BuildTriggers()
    {
        Span<char> triggers = stackalloc char[63]; // 30 C0 + DEL + 32 C1
        int i = 0;
        for (char c = '\0'; c <= '\u001F'; c++)
        {
            if (c is not ('\n' or '\t'))
            {
                triggers[i++] = c;
            }
        }

        triggers[i++] = '\u007F';
        for (char c = '\u0080'; c <= '\u009F'; c++)
        {
            triggers[i++] = c;
        }

        return SearchValues.Create(triggers[..i]);
    }

    /// <summary>Single-pass state machine: with <c>write=false</c> it counts
    /// (output length + stats), with <c>write=true</c> it fills the destination.
    /// Both passes walk identical state so the length is exact.</summary>
    private static int Scan(
        ReadOnlySpan<char> source,
        Span<char> destination,
        bool write,
        out int escapes,
        out int controls)
    {
        escapes = 0;
        controls = 0;
        int outPos = 0;
        int i = 0;
        int len = source.Length;

        while (i < len)
        {
            char c = source[i];

            if (c == Esc)
            {
                escapes++;
                i++;
                if (i >= len)
                {
                    break; // trailing lone ESC — dropped
                }

                switch (source[i])
                {
                    case '[': // CSI: params/intermediates until the final byte 0x40–0x7E
                        i++;
                        while (i < len && (source[i] < '\u0040' || source[i] > '\u007E'))
                        {
                            i++;
                        }

                        if (i < len)
                        {
                            i++; // final byte
                        }

                        break;

                    case ']' or 'P' or 'X' or '^' or '_': // OSC/DCS/SOS/PM/APC string
                        i++;
                        while (i < len)
                        {
                            if (source[i] == Bel)
                            {
                                i++;
                                break;
                            }

                            if (source[i] == Esc)
                            {
                                i++; // ESC \ = ST; a lone embedded ESC is swallowed
                                if (i < len && source[i] == '\\')
                                {
                                    i++;
                                }

                                break;
                            }

                            i++;
                        }

                        break;

                    default:
                        if (source[i] is >= '\u0020' and <= '\u002F')
                        {
                            // nF: intermediates (e.g. ESC ( B) then one final byte
                            while (i < len && source[i] is >= '\u0020' and <= '\u002F')
                            {
                                i++;
                            }

                            if (i < len)
                            {
                                i++;
                            }
                        }
                        else
                        {
                            i++; // Fe/Fp: single final byte (e.g. ESC 7, ESC M)
                        }

                        break;
                }

                continue;
            }

            if (c == '\r')
            {
                controls++;
                i++;
                if (i < len && source[i] == '\n')
                {
                    continue; // \r\n → the following \n copies normally
                }

                if (write)
                {
                    destination[outPos] = '\n'; // lone \r → \n (terminal Enter semantics)
                }

                outPos++;
                continue;
            }

            if (c is '\t' or '\n'
                || (c >= ' ' && c != '\u007F' && (c < '\u0080' || c > '\u009F')))
            {
                if (write)
                {
                    destination[outPos] = c;
                }

                outPos++;
                i++;
                continue;
            }

            controls++; // remaining C0, DEL, C1
            i++;
        }

        return outPos;
    }
}
