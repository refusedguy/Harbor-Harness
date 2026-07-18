namespace Harbor.Desktop.DesignSystem;

/// <summary>
///     Typography tokens shared across every desktop app. Font sizes are in
///     px (the value platform UI frameworks expect), weights are numeric
///     (CSS / XAML convention).
/// </summary>
public static class TypographyTokens
{
    // ── Font sizes (px) ────────────────────────────────────────────────────
    public const int CaptionSize = 11;
    public const int SmallSize = 12;
    public const int BodySize = 13;
    public const int BodyLargeSize = 14;
    public const int SubtitleSize = 16;
    public const int TitleSize = 18;
    public const int TitleLargeSize = 20;
    public const int HeadingSize = 24;
    public const int DisplaySize = 32;

    // ── Font weights ───────────────────────────────────────────────────────
    public const int WeightThin = 100;
    public const int WeightExtraLight = 200;
    public const int WeightLight = 300;
    public const int WeightNormal = 400;
    public const int WeightMedium = 500;
    public const int WeightSemiBold = 600;
    public const int WeightBold = 700;
    public const int WeightExtraBold = 800;
    public const int WeightBlack = 900;

    // ── Line heights (unitless ratio) ──────────────────────────────────────
    public const double LineHeightTight = 1.15;
    public const double LineHeightNormal = 1.4;
    public const double LineHeightRelaxed = 1.6;
    public const double LineHeightLoose = 1.8;

    // ── Letter spacing (em) ────────────────────────────────────────────────
    public const double LetterSpacingTight = -0.01;
    public const double LetterSpacingNormal = 0.0;
    public const double LetterSpacingWide = 0.025;
    public const double LetterSpacingExtraWide = 0.05;
}
