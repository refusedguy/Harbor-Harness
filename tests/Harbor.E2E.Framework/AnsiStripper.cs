namespace Harbor.E2E.Framework;
/// <summary>
///     Strip ANSI escape sequences from terminal output so the TUI driver's
///     rolling screen buffer can be searched with plain <c>string.Contains</c>.
/// </summary>
/// <remarks>
///     <para>
///         Handles CSI sequences (<c>ESC [ ... m</c> etc.), OSC sequences
///         (<c>ESC ] ... BEL</c>), and bare escape codes (<c>ESC</c> + 1 char).
///         This is NOT a full ANSI parser — it's enough for the renderers
///         Harbor uses (Spectre.Console, Terminal.Gui, Termina, RazorConsole),
///         which emit only CSI cursor/colour sequences plus the occasional OSC
///         title-set. If a renderer starts using private-mode sequences that
///         confuse this stripper, extend <c>Strip</c> rather than re-implement
///         per-test.
///     </para>
/// </remarks>
internal static class AnsiStripper
{
    /// <summary>
    ///     Remove ANSI escape sequences from <paramref name="raw" /> and return
    ///     the visible text. Newlines (\r\n, \r, \n) are preserved as \n.
    /// </summary>
    public static string Strip(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        var sb = new StringBuilder(raw.Length);
        int i = 0;
        while (i < raw.Length)
        {
            char c = raw[i];
            if (c == '\u001b' && i + 1 < raw.Length)
            {
                char next = raw[i + 1];
                if (next == '[')
                {
                    // CSI: ESC [ <params> <intermediate> <final>
                    int j = i + 2;
                    while (j < raw.Length)
                    {
                        char p = raw[j];
                        j++;
                        // final byte: 0x40..0x7E
                        if (p >= 0x40 && p <= 0x7E)
                            break;
                    }
                    i = j;
                    continue;
                }
                if (next == ']')
                {
                    // OSC: ESC ] ... BEL or ST (ESC \)
                    int j = i + 2;
                    while (j < raw.Length)
                    {
                        char p = raw[j];
                        j++;
                        if (p == '\u0007') // BEL
                            break;
                        if (p == '\u001b' && j < raw.Length && raw[j] == '\\')
                        {
                            j++;
                            break;
                        }
                    }
                    i = j;
                    continue;
                }
                // Bare escape: ESC + 1 char (e.g. ESC =, ESC >, ESC 7, ESC 8)
                i += 2;
                continue;
            }

            // Normalise newlines.
            if (c == '\r')
            {
                if (i + 1 < raw.Length && raw[i + 1] == '\n')
                    i += 2;
                else
                    i++;
                sb.Append('\n');
                continue;
            }

            // Drop NUL bytes (sometimes emitted by renderers after a clear).
            if (c == '\0')
            {
                i++;
                continue;
            }

            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
