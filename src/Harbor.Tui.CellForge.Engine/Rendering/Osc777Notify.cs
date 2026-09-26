using System.Text;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// OSC 777 desktop-notification formatter (rxvt-unicode family: «ESC ] 777 ;
/// notify ; &lt;title&gt; ; &lt;body&gt; BEL»). urxvt fields are ';' separated,
/// so title/body are sanitized — control bytes and separators cannot break the
/// envelope. kitty answers a real capability probe (OSC 99) instead and is
/// driven through <c>CapabilityEventKind.Osc99NotifyReport</c>; this encoder is
/// the fallback family (see <c>NotifyProbe.Detect</c>).
///
/// Static formatter ONLY — callers write the returned string themselves.
/// </summary>
public static class Osc777Notify
{
    private const char EscChar = '\u001B';
    private const char BelChar = '\u0007';

    /// <summary>
    /// Builds a notify sequence. Title/body control bytes (ESC, BEL, CR, LF,
    /// other C0/C1) and the ';' field separator are replaced with spaces so a
    /// hostile title can neither terminate the OSC string nor split fields.
    /// </summary>
    public static string Encode(string title, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return $"{EscChar}]777;notify;{Sanitize(title)};{Sanitize(body)}{BelChar}";
    }

    /// <summary>Replaces envelope-breaking bytes (applied to title and body).</summary>
    public static string Sanitize(ReadOnlySpan<char> text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            sb.Append(c is ';' or EscChar or BelChar || char.IsControl(c) ? ' ' : c);
        }

        return sb.ToString();
    }
}
