using System.Text;
namespace Harbor.Tui.Spectre.Fullscreen.Helpers;
/// <summary>
///     Mouse event parsing for SGR mouse protocol.
///     Single responsibility: parse mouse escape sequences.
/// </summary>
internal static class MouseHandler
{

    /// <summary>
    ///     Parse a mouse escape sequence. SGR format: \x1b[&lt;button;col;rowM or m.
    ///     Button 64 = wheel up, 65 = wheel down.
    /// </summary>
    internal static MouseAction ParseSequence()
    {
        if (!Console.KeyAvailable) return MouseAction.None;

        var sb = new StringBuilder();
        while (Console.KeyAvailable)
        {
            var ch = Console.ReadKey(intercept: true);
            sb.Append(ch.KeyChar);
            if (ch.KeyChar == 'M' || ch.KeyChar == 'm') break;
        }

        string seq = sb.ToString();
        if (seq.StartsWith('[') && (seq.EndsWith('M') || seq.EndsWith('m')))
        {
            string inner = seq[1..^1];
            string[] parts = inner.Split(';');
            if (parts.Length == 3 && int.TryParse(parts[0], out int button))
            {
                return button switch
                {
                    64 => MouseAction.ScrollUp,
                    65 => MouseAction.ScrollDown,
                    _ => MouseAction.Click
                };
            }
        }
        return MouseAction.None;
    }

    internal enum MouseAction { None, ScrollUp, ScrollDown, Click }
}
