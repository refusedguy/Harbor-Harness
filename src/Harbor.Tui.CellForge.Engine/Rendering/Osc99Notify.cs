using System.Text;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// OSC 99 desktop-notification formatter (kitty protocol: «ESC ] 99 ; ;
/// &lt;title&gt;\n&lt;body&gt; ESC \»). Used when the startup capability probe
/// (<c>TerminalQueries.Osc99NotifyProbe</c>) was answered — kitty confirmed on
/// the wire. The urxvt family counterpart is <see cref="Osc777Notify" />.
///
/// Static formatter ONLY — callers write the returned string themselves.
/// </summary>
public static class Osc99Notify
{
    private const char EscChar = '\u001B';

    /// <summary>
    /// Builds a basic kitty notification (title + body, no metadata). Title
    /// and body control bytes are neutralized so a hostile string cannot
    /// terminate the OSC envelope.
    /// </summary>
    public static string Encode(string title, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return $"{EscChar}]99;;{Sanitize(title)}\n{Sanitize(body)}{EscChar}\\";
    }

    /// <summary>Strips envelope-breaking control bytes (kitty payload form).</summary>
    public static string Sanitize(ReadOnlySpan<char> text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            sb.Append(c == EscChar || char.IsControl(c) ? ' ' : c);
        }

        return sb.ToString();
    }
}
