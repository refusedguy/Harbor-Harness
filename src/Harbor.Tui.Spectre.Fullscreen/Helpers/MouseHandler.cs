namespace Harbor.Tui.Spectre.Fullscreen.Helpers;

/// <summary>
/// Mouse event parsing for SGR mouse protocol.
/// Single responsibility: parse mouse escape sequences.
/// </summary>
internal static class MouseHandler
{
    internal enum MouseAction { None, ScrollUp, ScrollDown, Click }

    /// <summary>
    /// Parse a mouse escape sequence. SGR format: \x1b[&lt;button;col;rowM or m.
    /// Button 64 = wheel up, 65 = wheel down.
    /// </summary>
    internal static MouseAction ParseSequence()
    {
        if (!Console.KeyAvailable) return MouseAction.None;

        var sb = new System.Text.StringBuilder();
        while (Console.KeyAvailable)
        {
            var ch = Console.ReadKey(intercept: true);
            sb.Append(ch.KeyChar);
            if (ch.KeyChar == 'M' || ch.KeyChar == 'm') break;
        }

        var seq = sb.ToString();
        if (seq.StartsWith('[') && (seq.EndsWith('M') || seq.EndsWith('m')))
        {
            var inner = seq[1..^1];
            var parts = inner.Split(';');
            if (parts.Length == 3 && int.TryParse(parts[0], out var button))
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
}
