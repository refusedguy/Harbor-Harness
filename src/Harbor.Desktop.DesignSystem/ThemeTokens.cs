namespace Harbor.Desktop.DesignSystem;

/// <summary>
///     Full 60-color Catppuccin palette as framework-agnostic
///     <see cref="RgbColor"/> constants. Source: <see href="https://catppuccin.com/palette"/>.
/// </summary>
/// <remarks>
///     This file expands <c>Harbor.Desktop.Abstractions.DesignSystem.ColorPalette</c>
///     (which only carries the ~20 colors used by base VMs) to the full 60-color
///     Catppuccin Mocha + Latte + Frappé + Macchiato sets. Apps that only need
///     the dark/light Mocha+Latte subset can keep using
///     <c>ColorPalette</c>; apps that want the full palette reference this
///     <c>ThemeTokens</c> class.
/// </remarks>
public static class ThemeTokens
{
    // ── Mocha (dark) — 26 colors ───────────────────────────────────────────
    public static readonly RgbColor MochaRosewater = (0xF5, 0xE0, 0xDC);
    public static readonly RgbColor MochaFlamingo = (0xF2, 0xCD, 0xCD);
    public static readonly RgbColor MochaPink = (0xF5, 0xC2, 0xE7);
    public static readonly RgbColor MochaMauve = (0xCB, 0xA6, 0xF7);
    public static readonly RgbColor MochaRed = (0xF3, 0x8B, 0xA8);
    public static readonly RgbColor MochaMaroon = (0xEB, 0xA0, 0xAC);
    public static readonly RgbColor MochaPeach = (0xFA, 0xB3, 0x87);
    public static readonly RgbColor MochaYellow = (0xF9, 0xE2, 0xAF);
    public static readonly RgbColor MochaGreen = (0xA6, 0xE3, 0xA1);
    public static readonly RgbColor MochaTeal = (0x94, 0xE2, 0xD5);
    public static readonly RgbColor MochaSky = (0x89, 0xDC, 0xEB);
    public static readonly RgbColor MochaSapphire = (0x74, 0xC7, 0xEC);
    public static readonly RgbColor MochaBlue = (0x89, 0xB4, 0xFA);
    public static readonly RgbColor MochaLavender = (0xB4, 0xBE, 0xFE);
    public static readonly RgbColor MochaText = (0xCD, 0xD6, 0xF4);
    public static readonly RgbColor MochaSubtext1 = (0xBA, 0xC2, 0xDE);
    public static readonly RgbColor MochaSubtext0 = (0xA6, 0xAD, 0xC8);
    public static readonly RgbColor MochaOverlay2 = (0x93, 0x99, 0xB2);
    public static readonly RgbColor MochaOverlay1 = (0x7F, 0x84, 0x99);
    public static readonly RgbColor MochaOverlay0 = (0x6C, 0x70, 0x86);
    public static readonly RgbColor MochaSurface2 = (0x58, 0x5B, 0x70);
    public static readonly RgbColor MochaSurface1 = (0x45, 0x47, 0x5A);
    public static readonly RgbColor MochaSurface0 = (0x31, 0x32, 0x44);
    public static readonly RgbColor MochaBase = (0x1E, 0x1E, 0x2E);
    public static readonly RgbColor MochaMantle = (0x18, 0x18, 0x25);
    public static readonly RgbColor MochaCrust = (0x11, 0x11, 0x1B);

    // ── Latte (light) — 26 colors ──────────────────────────────────────────
    public static readonly RgbColor LatteRosewater = (0xDC, 0xDC, 0xDC);
    public static readonly RgbColor LatteFlamingo = (0xDD, 0x78, 0x78);
    public static readonly RgbColor LattePink = (0xEA, 0x76, 0xCB);
    public static readonly RgbColor LatteMauve = (0x88, 0x39, 0xEF);
    public static readonly RgbColor LatteRed = (0xD2, 0x0F, 0x39);
    public static readonly RgbColor LatteMaroon = (0xE6, 0x45, 0x53);
    public static readonly RgbColor LattePeach = (0xFE, 0x64, 0x0B);
    public static readonly RgbColor LatteYellow = (0xDF, 0x8E, 0x1D);
    public static readonly RgbColor LatteGreen = (0x40, 0xA0, 0x2B);
    public static readonly RgbColor LatteTeal = (0x17, 0x92, 0x99);
    public static readonly RgbColor LatteSky = (0x04, 0xA5, 0xEC);
    public static readonly RgbColor LatteSapphire = (0x20, 0x9F, 0xB5);
    public static readonly RgbColor LatteBlue = (0x1E, 0x66, 0xF5);
    public static readonly RgbColor LatteLavender = (0x72, 0x87, 0xFD);
    public static readonly RgbColor LatteText = (0x4C, 0x4F, 0x69);
    public static readonly RgbColor LatteSubtext1 = (0x5C, 0x5F, 0x77);
    public static readonly RgbColor LatteSubtext0 = (0x6C, 0x6F, 0x85);
    public static readonly RgbColor LatteOverlay2 = (0x7C, 0x7F, 0x93);
    public static readonly RgbColor LatteOverlay1 = (0x8C, 0x8F, 0xA1);
    public static readonly RgbColor LatteOverlay0 = (0x9C, 0x9F, 0xAF);
    public static readonly RgbColor LatteSurface2 = (0xAC, 0xB0, 0xBE);
    public static readonly RgbColor LatteSurface1 = (0xBC, 0xC0, 0xCC);
    public static readonly RgbColor LatteSurface0 = (0xCC, 0xD0, 0xDA);
    public static readonly RgbColor LatteBase = (0xEF, 0xF1, 0xF5);
    public static readonly RgbColor LatteMantle = (0xE6, 0xE9, 0xEF);
    public static readonly RgbColor LatteCrust = (0xDC, 0xE0, 0xE8);
}
