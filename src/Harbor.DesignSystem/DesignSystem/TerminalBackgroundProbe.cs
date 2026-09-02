namespace Harbor.DesignSystem;

/// <summary>
/// OSC 11 auto-theme detection (Claude Code pattern): parse the terminal's
/// background-color report (<c>OSC 11 ; rgb:RR/GG/BB ST|BEL</c>) and pick the
/// built-in theme whose surfaces match the real terminal background — light
/// report → <see cref="HarborTheme.HarborLight" />, dark →
/// <see cref="HarborTheme.HarborDark" />. Pure static logic: no Console I/O,
/// hosts send the query and feed the raw response here.
/// </summary>
public static class TerminalBackgroundProbe
{
    /// <summary>OSC 11 query — «report your background color» (BEL-terminated).</summary>
    public const string Query = "\u001B]11;?\u0007";

    /// <summary>WCAG relative-luminance threshold separating light from dark reports.</summary>
    public const double LightLuminanceThreshold = 0.5;

    /// <summary>
    /// Parses an OSC 11 background report. Accepts BEL- and ST-terminated
    /// responses, <c>rgb:</c> color specs in 8-bit (RR/GG/BB) and 16-bit
    /// (RRRR/GGGG/BBBB) component forms; the 16-bit form keeps the high byte.
    /// XParseColor scaled-RGB forms are out of scope — no known terminal
    /// answers OSC 11 with them.
    /// </summary>
    public static bool TryParseOsc11(string? response, out RgbColor background)
    {
        background = default;

        if (string.IsNullOrEmpty(response))
        {
            return false;
        }

        // Payload between "11;" and the terminator (BEL or ESC \).
        int head = response.IndexOf("11;", StringComparison.Ordinal);
        if (head < 0)
        {
            return false;
        }

        head += 3;
        int tail = response.Length;
        if (tail > head && response[tail - 1] == '\u0007')
        {
            tail--;
        }
        else if (tail - head >= 2 && response[tail - 2] == '\u001B' && response[tail - 1] == '\\')
        {
            tail -= 2;
        }

        string payload = response[head..tail].Trim();
        if (!payload.StartsWith("rgb:", StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = payload[4..].Split('/');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!TryParseComponent(parts[0], out byte r)
            || !TryParseComponent(parts[1], out byte g)
            || !TryParseComponent(parts[2], out byte b))
        {
            return false;
        }

        background = new RgbColor(r, g, b);
        return true;
    }

    /// <summary>WCAG 2.x relative luminance of a color in [0..1].</summary>
    public static double RelativeLuminance(RgbColor color)
    {
        double r = LinearChannel(color.R);
        double g = LinearChannel(color.G);
        double b = LinearChannel(color.B);
        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }

    /// <summary>
    /// Picks the built-in theme matching a terminal background: light report →
    /// <see cref="HarborTheme.HarborLight" />, otherwise
    /// <see cref="HarborTheme.HarborDark" />.
    /// </summary>
    public static HarborTheme PickTheme(RgbColor background) =>
        RelativeLuminance(background) >= LightLuminanceThreshold
            ? HarborTheme.HarborLight
            : HarborTheme.HarborDark;

    /// <summary>Detects a theme from a raw OSC 11 response; unparsable/absent → <see cref="HarborTheme.HarborDark" />.</summary>
    public static HarborTheme Detect(string? osc11Response) =>
        TryParseOsc11(osc11Response, out RgbColor background) ? PickTheme(background) : HarborTheme.HarborDark;

    private static bool TryParseComponent(string hex, out byte value)
    {
        value = 0;
        if (hex.Length == 2)
        {
            return TryFromHex(hex, out value);
        }

        if (hex.Length == 4)
        {
            // 16-bit form — keep the high byte.
            if (!TryFromHex(hex[..2], out value))
            {
                return false;
            }

            return byte.TryParse(hex[2..], System.Globalization.NumberStyles.HexNumber, null, out _);
        }

        return false;
    }

    private static bool TryFromHex(string hex, out byte value) =>
        byte.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out value);

    private static double LinearChannel(byte channel)
    {
        double c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
