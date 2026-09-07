namespace Harbor.DesignSystem;

/// <summary>
///     One named terminal theme — the complete HDS v1 color token set
///     (14 color slots plus <paramref name="Name" />).
///     Immutable; <see cref="TerminalColorPalette.Apply" /> swaps the active
///     instance and notifies renderers via <see cref="TerminalColorPalette.ThemeChanged" />.
/// </summary>
public sealed record HarborTheme(
    string Name,
    RgbColor Accent,
    RgbColor Success,
    RgbColor Warning,
    RgbColor Error,
    RgbColor Tool,
    RgbColor System,
    RgbColor User,
    RgbColor Background,
    RgbColor Panel,
    RgbColor Surface,
    RgbColor Surface2,
    RgbColor Border,
    RgbColor Muted,
    RgbColor Text)
{
    /// <summary>HDS v1 default (matches docs/design-system-report-20260827.html exactly).</summary>
    public static readonly HarborTheme HarborDark = new(
        "harbor-dark",
        new RgbColor(0x39, 0xBA, 0xE6),
        new RgbColor(0x7F, 0xD9, 0x62),
        new RgbColor(0xFF, 0xB4, 0x54),
        new RgbColor(0xFF, 0x6B, 0x6B),
        new RgbColor(0xD2, 0xA6, 0xFF),
        new RgbColor(0xF2, 0x96, 0x68),
        new RgbColor(0xB3, 0xB9, 0xC5),
        new RgbColor(0x0A, 0x0E, 0x14),
        new RgbColor(0x0D, 0x11, 0x17),
        new RgbColor(0x13, 0x18, 0x20),
        new RgbColor(0x1A, 0x1F, 0x2B),
        new RgbColor(0x1F, 0x24, 0x30),
        new RgbColor(0x5C, 0x67, 0x73),
        new RgbColor(0xB3, 0xB9, 0xC5));

    /// <summary>Daylight sibling: paper surfaces, darker accents for contrast.</summary>
    public static readonly HarborTheme HarborLight = new(
        "harbor-light",
        new RgbColor(0x0E, 0x74, 0x90),
        new RgbColor(0x3F, 0x8F, 0x29),
        new RgbColor(0xB4, 0x53, 0x09),
        new RgbColor(0xDC, 0x26, 0x26),
        new RgbColor(0x7C, 0x3A, 0xED),
        new RgbColor(0xC2, 0x41, 0x0C),
        new RgbColor(0x3A, 0x3F, 0x49),
        new RgbColor(0xF5, 0xF3, 0xEF),
        new RgbColor(0xFF, 0xFF, 0xFF),
        new RgbColor(0xFA, 0xFA, 0xF8),
        new RgbColor(0xEF, 0xED, 0xE8),
        new RgbColor(0xD8, 0xD5, 0xCE),
        new RgbColor(0x8A, 0x8F, 0x98),
        new RgbColor(0x2A, 0x2E, 0x37));

    /// <summary>Amber-lit dark: warm surfaces, golden accent.</summary>
    public static readonly HarborTheme HarborWarm = new(
        "harbor-warm",
        new RgbColor(0xF0, 0xA3, 0x5E),
        new RgbColor(0xA3, 0xC7, 0x6D),
        new RgbColor(0xFF, 0xC8, 0x68),
        new RgbColor(0xFF, 0x7A, 0x6B),
        new RgbColor(0xD9, 0xA9, 0xE6),
        new RgbColor(0xF2, 0x96, 0x68),
        new RgbColor(0xE8, 0xDC, 0xC8),
        new RgbColor(0x14, 0x10, 0x0C),
        new RgbColor(0x1A, 0x14, 0x10),
        new RgbColor(0x22, 0x1A, 0x14),
        new RgbColor(0x2B, 0x21, 0x1A),
        new RgbColor(0x3A, 0x2D, 0x22),
        new RgbColor(0x8A, 0x7B, 0x6C),
        new RgbColor(0xE8, 0xDC, 0xC8));

    /// <summary>Cold navy dark: steel surfaces, mint success, sky accent.</summary>
    public static readonly HarborTheme HarborCool = new(
        "harbor-cool",
        new RgbColor(0x4C, 0xC2, 0xFF),
        new RgbColor(0x62, 0xD9, 0xB4),
        new RgbColor(0xFF, 0xB4, 0x54),
        new RgbColor(0xFF, 0x6B, 0x8B),
        new RgbColor(0x82, 0xAA, 0xFF),
        new RgbColor(0x68, 0xA8, 0xF2),
        new RgbColor(0xB8, 0xC4, 0xD4),
        new RgbColor(0x0A, 0x0F, 0x14),
        new RgbColor(0x0D, 0x12, 0x19),
        new RgbColor(0x12, 0x18, 0x26),
        new RgbColor(0x1A, 0x22, 0x33),
        new RgbColor(0x1F, 0x2A, 0x3D),
        new RgbColor(0x5C, 0x6F, 0x87),
        new RgbColor(0xB8, 0xC4, 0xD4));

    /// <summary>Built-in catalog in switcher order.</summary>
    public static readonly IReadOnlyList<HarborTheme> BuiltIn =
    [
        HarborDark,
        HarborLight,
        HarborWarm,
        HarborCool
    ];

    /// <summary>Resolves a built-in by name (case-insensitive); unknown falls back to <see cref="HarborDark" />.</summary>
    public static HarborTheme ByName(string name) =>
        BuiltIn.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? HarborDark;
}
