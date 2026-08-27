namespace Harbor.DesignSystem;

/// <summary>
/// WCAG 2.x accessibility math over the HDS catalog (§Accessibility):
/// relative luminance, contrast ratios, and the AA/AAA pass levels used by
/// every Harbor surface to validate focus indicators, hints, and role text.
/// Pure functions — no platform deps.
/// </summary>
public static class Accessibility
{
    /// <summary>WCAG relative luminance of an sRGB color.</summary>
    public static double RelativeLuminance(in RgbColor c)
    {
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
    }

    /// <summary>Contrast ratio between two colors — 1:1 (invisible) .. 21:1 (max).</summary>
    public static double ContrastRatio(in RgbColor a, in RgbColor b)
    {
        double la = RelativeLuminance(a), lb = RelativeLuminance(b);
        double hi = Math.Max(la, lb), lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    // ── WCAG 2.x thresholds ────────────────────────────────────────────────
    public const double TextAaRatio = 4.5;          // normal text
    public const double TextAaaRatio = 7.0;         // enhanced normal text
    public const double LargeTextAaRatio = 3.0;     // ≥18 pt or ≥14 pt bold
    public const double UiComponentRatio = 3.0;     // borders, glyphs, focus rings
}
