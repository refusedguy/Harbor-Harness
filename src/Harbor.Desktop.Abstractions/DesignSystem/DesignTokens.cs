namespace Harbor.Desktop.Abstractions.DesignSystem;
/// <summary>
///     Spacing, corner-radius, and font-size design tokens. Matched to the
///     4/8/12/16/24/32/48/64 px scale used by every Harbor desktop app.
/// </summary>
public static class DesignTokens
{
    // ── Spacing (px) ───────────────────────────────────────────────────────
    /// <summary>2 px — hairline.</summary>
    public const int Space2 = 2;
    /// <summary>4 px — inline icon gap.</summary>
    public const int Space4 = 4;
    /// <summary>8 px — default inline gap.</summary>
    public const int Space8 = 8;
    /// <summary>12 px — panel padding.</summary>
    public const int Space12 = 12;
    /// <summary>16 px — default panel padding.</summary>
    public const int Space16 = 16;
    /// <summary>24 px — section gap.</summary>
    public const int Space24 = 24;
    /// <summary>32 px — large section gap.</summary>
    public const int Space32 = 32;
    /// <summary>48 px — page margin.</summary>
    public const int Space48 = 48;
    /// <summary>64 px — hero gap.</summary>
    public const int Space64 = 64;

    // ── Corner radius (px) ─────────────────────────────────────────────────
    /// <summary>2 px — tight corners (input boxes).</summary>
    public const int Radius2 = 2;
    /// <summary>4 px — default control radius.</summary>
    public const int Radius4 = 4;
    /// <summary>8 px — card radius.</summary>
    public const int Radius8 = 8;
    /// <summary>12 px — large card radius.</summary>
    public const int Radius12 = 12;
    /// <summary>16 px — panel radius.</summary>
    public const int Radius16 = 16;
    /// <summary>9999 px — pill.</summary>
    public const int RadiusPill = 9999;

    // ── Font sizes (px) ────────────────────────────────────────────────────
    /// <summary>11 px — caption / status bar.</summary>
    public const int FontSize11 = 11;
    /// <summary>12 px — small text.</summary>
    public const int FontSize12 = 12;
    /// <summary>13 px — body text (default).</summary>
    public const int FontSize13 = 13;
    /// <summary>14 px — UI body.</summary>
    public const int FontSize14 = 14;
    /// <summary>16 px — subtitle.</summary>
    public const int FontSize16 = 16;
    /// <summary>18 px — title.</summary>
    public const int FontSize18 = 18;
    /// <summary>20 px — heading.</summary>
    public const int FontSize20 = 20;
    /// <summary>24 px — large heading.</summary>
    public const int FontSize24 = 24;
    /// <summary>32 px — display.</summary>
    public const int FontSize32 = 32;

    // ── Font weights ───────────────────────────────────────────────────────
    /// <summary>Normal weight (400).</summary>
    public const int FontWeightNormal = 400;
    /// <summary>Medium weight (500).</summary>
    public const int FontWeightMedium = 500;
    /// <summary>Semi-bold weight (600).</summary>
    public const int FontWeightSemiBold = 600;
    /// <summary>Bold weight (700).</summary>
    public const int FontWeightBold = 700;
}
