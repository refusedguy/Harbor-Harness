namespace Harbor.Desktop.DesignSystem;

/// <summary>
///     Spacing tokens on the 4/8/12/16/24/32/48/64 px scale. Matched to
///     <c>Harbor.Desktop.Abstractions.DesignSystem.DesignTokens</c> (which
///     only carries the subset used by base VMs) — this class adds the
///     half-steps (2 px) and the "tiny" (4 px) and "huge" (96 px) ends of
///     the scale.
/// </summary>
public static class SpacingTokens
{
    public const int None = 0;
    public const int Hairline = 2;
    public const int Tiny = 4;
    public const int Small = 8;
    public const int Medium = 12;
    public const int Default = 16;
    public const int Large = 24;
    public const int XLarge = 32;
    public const int Huge = 48;
    public const int Massive = 64;
    public const int Epic = 96;

    // ── Corner radius ──────────────────────────────────────────────────────
    public const int RadiusNone = 0;
    public const int RadiusTiny = 2;
    public const int RadiusSmall = 4;
    public const int RadiusMedium = 6;
    public const int RadiusDefault = 8;
    public const int RadiusLarge = 12;
    public const int RadiusXLarge = 16;
    public const int RadiusPill = 9999;

    // ── Border widths ──────────────────────────────────────────────────────
    public const int BorderHairline = 1;
    public const int BorderThin = 2;
    public const int BorderDefault = 3;
    public const int BorderThick = 4;

    // ── Z-index layers ─────────────────────────────────────────────────────
    public const int ZBase = 0;
    public const int ZSticky = 100;
    public const int ZDropdown = 1000;
    public const int ZModal = 2000;
    public const int ZToast = 3000;
    public const int ZTooltip = 4000;
}
