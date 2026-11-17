using System.Text;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// OSC 8 hyperlink formatter (the de-facto terminal hyperlink protocol:
/// kitty, iTerm2, WezTerm, GNOME Terminal, Windows Terminal, …). Wire form:
/// «ESC ] 8 ; &lt;params&gt; ; &lt;URI&gt; ESC \» opens a link and
/// «ESC ] 8 ; ; ESC \» closes it. Both are zero-width: they change neither
/// the cell grid nor the tracked pen column, which is why the link can live
/// in <see cref="HyperlinkTable"/> instead of <see cref="Cell"/>.
///
/// <para>Static formatter ONLY — it never touches Console. Callers append the
/// bytes to the frame through <see cref="AnsiWriter.RawBytes"/> so the escape
/// stream leaves the process in the same single backend write as the cells.</para>
/// </summary>
public static class Osc8Hyperlink
{
    private static readonly byte[] CloseBytes = Encoding.ASCII.GetBytes("\u001B]8;;\u001B\\");

    /// <summary>Close-link sequence: «ESC ] 8 ; ; ESC \».</summary>
    public static ReadOnlySpan<byte> Close => CloseBytes;

    /// <summary>
    /// Builds an open-link sequence for <paramref name="uri"/> with no params.
    /// </summary>
    public static byte[] Open(string uri) => Open(uri, string.Empty);

    /// <summary>
    /// Builds an open-link sequence with an optional link <paramref name="id"/>
    /// (the OSC 8 <c>id=…</c> param, used by terminals to merge repeated
    /// references to the same target). Both URI and id have envelope-breaking
    /// control bytes replaced, so a hostile string cannot terminate the OSC
    /// string early or inject a second sequence.
    /// </summary>
    public static byte[] Open(string uri, string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(uri);
        string safeId = SanitizeParam(id);
        string paramsPart = safeId.Length == 0 ? string.Empty : $"id={safeId}";
        string payload = $"\u001B]8;{paramsPart};{SanitizeUri(uri)}\u001B\\";
        return Encoding.UTF8.GetBytes(payload);
    }

    /// <summary>
    /// Neutralizes bytes that would break out of an OSC 8 string: ESC (which
    /// could start a forced ST or another sequence), BEL and every C0/DEL
    /// control. Printable characters, including ';', are kept — a URI may
    /// legitimately carry a query string.
    /// </summary>
    public static string SanitizeUri(ReadOnlySpan<char> uri) => Sanitize(uri, param: false);

    private static string SanitizeParam(ReadOnlySpan<char> value) => Sanitize(value, param: true);

    private static string Sanitize(ReadOnlySpan<char> value, bool param)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            bool unsafeChar = c is '\u001B' or '\u0007' || c < ' ' || c == '\u007F';
            if (param)
            {
                // The id sits in the params field: ';' would open the URI field.
                unsafeChar |= c == ';';
            }

            sb.Append(unsafeChar ? '_' : c);
        }

        return sb.ToString();
    }
}
