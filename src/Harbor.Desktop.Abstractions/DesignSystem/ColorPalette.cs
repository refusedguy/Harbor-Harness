namespace Harbor.Desktop.Abstractions.DesignSystem;
/// <summary>
///     Catppuccin-Mocha (dark) and Catppuccin-Latte (light) palette as
///     framework-agnostic <see cref="RgbColor" /> constants. Source:
///     <see href="https://catppuccin.com/palette" />.
/// </summary>
/// <remarks>
///     The <see cref="Harbor.Desktop.DesignSystem" /> package mirrors these as
///     a flat <see cref="Dictionary{TKey, TValue}" /> for theme switching;
///     this static class is the canonical reference for inline use in
///     base VMs and shared services.
/// </remarks>
public static class ColorPalette
{
    // ── Catppuccin-Mocha (dark) ────────────────────────────────────────────
    /// <summary>Mocha Base — primary app background (#1E1E2E).</summary>
    public static readonly RgbColor MochaBase = (0x1E, 0x1E, 0x2E);
    /// <summary>Mocha Mantle — sidebar / status bar (#181825).</summary>
    public static readonly RgbColor MochaMantle = (0x18, 0x18, 0x25);
    /// <summary>Mocha Crust — deepest panel (#11111B).</summary>
    public static readonly RgbColor MochaCrust = (0x11, 0x11, 0x1B);
    /// <summary>Mocha Surface0 — cards / borders (#313244).</summary>
    public static readonly RgbColor MochaSurface0 = (0x31, 0x32, 0x44);
    /// <summary>Mocha Surface1 — selected (#45475A).</summary>
    public static readonly RgbColor MochaSurface1 = (0x45, 0x47, 0x5A);
    /// <summary>Mocha Surface2 — hover (#585B70).</summary>
    public static readonly RgbColor MochaSurface2 = (0x58, 0x5B, 0x70);
    /// <summary>Mocha Text — primary text (#CDD6F4).</summary>
    public static readonly RgbColor MochaText = (0xCD, 0xD6, 0xF4);
    /// <summary>Mocha Subtext0 — secondary text (#A6ADC8).</summary>
    public static readonly RgbColor MochaSubtext0 = (0xA6, 0xAD, 0xC8);
    /// <summary>Mocha Blue — primary accent (#89B4FA).</summary>
    public static readonly RgbColor MochaBlue = (0x89, 0xB4, 0xFA);
    /// <summary>Mocha Lavender — secondary accent (#B4BEFE).</summary>
    public static readonly RgbColor MochaLavender = (0xB4, 0xBE, 0xFE);
    /// <summary>Mocha Pink (#F5C2E7).</summary>
    public static readonly RgbColor MochaPink = (0xF5, 0xC2, 0xE7);
    /// <summary>Mocha Red — error / abort (#F38BA8).</summary>
    public static readonly RgbColor MochaRed = (0xF3, 0x8B, 0xA8);
    /// <summary>Mocha Green — success (#A6E3A1).</summary>
    public static readonly RgbColor MochaGreen = (0xA6, 0xE3, 0xA1);
    /// <summary>Mocha Yellow — system / warning (#F9E2AF).</summary>
    public static readonly RgbColor MochaYellow = (0xF9, 0xE2, 0xAF);
    /// <summary>Mocha Peach — warning toast (#FAB387).</summary>
    public static readonly RgbColor MochaPeach = (0xFA, 0xB3, 0x87);
    /// <summary>Mocha Teal (#94E2D5).</summary>
    public static readonly RgbColor MochaTeal = (0x94, 0xE2, 0xD5);
    /// <summary>Mocha Sky — chat user (#89DCEB).</summary>
    public static readonly RgbColor MochaSky = (0x89, 0xDC, 0xEB);
    /// <summary>Mocha Maroon (#EBA0AC).</summary>
    public static readonly RgbColor MochaMaroon = (0xEB, 0xA0, 0xAC);
    /// <summary>Mocha Mauve (#CBA6F7).</summary>
    public static readonly RgbColor MochaMauve = (0xCB, 0xA6, 0xF7);

    // ── Catppuccin-Latte (light) ───────────────────────────────────────────
    /// <summary>Latte Base — primary app background (#EFF1F5).</summary>
    public static readonly RgbColor LatteBase = (0xEF, 0xF1, 0xF5);
    /// <summary>Latte Mantle — sidebar (#E6E9EF).</summary>
    public static readonly RgbColor LatteMantle = (0xE6, 0xE9, 0xEF);
    /// <summary>Latte Crust — deepest panel (#DCE0E8).</summary>
    public static readonly RgbColor LatteCrust = (0xDC, 0xE0, 0xE8);
    /// <summary>Latte Surface0 — cards / borders (#CCD0DA).</summary>
    public static readonly RgbColor LatteSurface0 = (0xCC, 0xD0, 0xDA);
    /// <summary>Latte Surface1 — selected (#BCC0CC).</summary>
    public static readonly RgbColor LatteSurface1 = (0xBC, 0xC0, 0xCC);
    /// <summary>Latte Surface2 — hover (#ACB0BE).</summary>
    public static readonly RgbColor LatteSurface2 = (0xAC, 0xB0, 0xBE);
    /// <summary>Latte Text — primary text (#4C4F69).</summary>
    public static readonly RgbColor LatteText = (0x4C, 0x4F, 0x69);
    /// <summary>Latte Subtext0 — secondary text (#6C6F85).</summary>
    public static readonly RgbColor LatteSubtext0 = (0x6C, 0x6F, 0x85);
    /// <summary>Latte Blue — primary accent (#1E66F5).</summary>
    public static readonly RgbColor LatteBlue = (0x1E, 0x66, 0xF5);
    /// <summary>Latte Lavender — secondary accent (#7287FD).</summary>
    public static readonly RgbColor LatteLavender = (0x72, 0x87, 0xFD);
    /// <summary>Latte Pink (#EA76CB).</summary>
    public static readonly RgbColor LattePink = (0xEA, 0x76, 0xCB);
    /// <summary>Latte Red — error (#D20F39).</summary>
    public static readonly RgbColor LatteRed = (0xD2, 0x0F, 0x39);
    /// <summary>Latte Green — success (#40A02B).</summary>
    public static readonly RgbColor LatteGreen = (0x40, 0xA0, 0x2B);
    /// <summary>Latte Yellow — system (#DF8E1D).</summary>
    public static readonly RgbColor LatteYellow = (0xDF, 0x8E, 0x1D);
    /// <summary>Latte Peach — warning (#FE640B).</summary>
    public static readonly RgbColor LattePeach = (0xFE, 0x64, 0x0B);
    /// <summary>Latte Teal (#179299).</summary>
    public static readonly RgbColor LatteTeal = (0x17, 0x92, 0x99);
    /// <summary>Latte Sky — chat user (#04A5EC).</summary>
    public static readonly RgbColor LatteSky = (0x04, 0xA5, 0xEC);
    /// <summary>Latte Maroon (#E64553).</summary>
    public static readonly RgbColor LatteMaroon = (0xE6, 0x45, 0x53);
    /// <summary>Latte Mauve (#8839EF).</summary>
    public static readonly RgbColor LatteMauve = (0x88, 0x39, 0xEF);
}
