namespace Harbor.DesignSystem;

/// <summary>
/// One named terminal theme — the complete HDS v1 color token set
/// (14 color slots plus <paramref name="Name" />).
/// Immutable; <see cref="TerminalColorPalette.Apply" /> swaps the active
/// instance and notifies renderers via <see cref="TerminalColorPalette.ThemeChanged" />.
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
        Accent: new RgbColor(0x39, 0xBA, 0xE6),
        Success: new RgbColor(0x7F, 0xD9, 0x62),
        Warning: new RgbColor(0xFF, 0xB4, 0x54),
        Error: new RgbColor(0xFF, 0x6B, 0x6B),
        Tool: new RgbColor(0xD2, 0xA6, 0xFF),
        System: new RgbColor(0xF2, 0x96, 0x68),
        User: new RgbColor(0xB3, 0xB9, 0xC5),
        Background: new RgbColor(0x0A, 0x0E, 0x14),
        Panel: new RgbColor(0x0D, 0x11, 0x17),
        Surface: new RgbColor(0x13, 0x18, 0x20),
        Surface2: new RgbColor(0x1A, 0x1F, 0x2B),
        Border: new RgbColor(0x1F, 0x24, 0x30),
        Muted: new RgbColor(0x5C, 0x67, 0x73),
        Text: new RgbColor(0xB3, 0xB9, 0xC5));

    /// <summary>Daylight sibling: paper surfaces, darker accents for contrast.</summary>
    public static readonly HarborTheme HarborLight = new(
        "harbor-light",
        Accent: new RgbColor(0x0E, 0x74, 0x90),
        Success: new RgbColor(0x3F, 0x8F, 0x29),
        Warning: new RgbColor(0xB4, 0x53, 0x09),
        Error: new RgbColor(0xDC, 0x26, 0x26),
        Tool: new RgbColor(0x7C, 0x3A, 0xED),
        System: new RgbColor(0xC2, 0x41, 0x0C),
        User: new RgbColor(0x3A, 0x3F, 0x49),
        Background: new RgbColor(0xF5, 0xF3, 0xEF),
        Panel: new RgbColor(0xFF, 0xFF, 0xFF),
        Surface: new RgbColor(0xFA, 0xFA, 0xF8),
        Surface2: new RgbColor(0xEF, 0xED, 0xE8),
        Border: new RgbColor(0xD8, 0xD5, 0xCE),
        Muted: new RgbColor(0x8A, 0x8F, 0x98),
        Text: new RgbColor(0x2A, 0x2E, 0x37));

    /// <summary>Amber-lit dark: warm surfaces, golden accent.</summary>
    public static readonly HarborTheme HarborWarm = new(
        "harbor-warm",
        Accent: new RgbColor(0xF0, 0xA3, 0x5E),
        Success: new RgbColor(0xA3, 0xC7, 0x6D),
        Warning: new RgbColor(0xFF, 0xC8, 0x68),
        Error: new RgbColor(0xFF, 0x7A, 0x6B),
        Tool: new RgbColor(0xD9, 0xA9, 0xE6),
        System: new RgbColor(0xF2, 0x96, 0x68),
        User: new RgbColor(0xE8, 0xDC, 0xC8),
        Background: new RgbColor(0x14, 0x10, 0x0C),
        Panel: new RgbColor(0x1A, 0x14, 0x10),
        Surface: new RgbColor(0x22, 0x1A, 0x14),
        Surface2: new RgbColor(0x2B, 0x21, 0x1A),
        Border: new RgbColor(0x3A, 0x2D, 0x22),
        Muted: new RgbColor(0x8A, 0x7B, 0x6C),
        Text: new RgbColor(0xE8, 0xDC, 0xC8));

    /// <summary>Cold navy dark: steel surfaces, mint success, sky accent.</summary>
    public static readonly HarborTheme HarborCool = new(
        "harbor-cool",
        Accent: new RgbColor(0x4C, 0xC2, 0xFF),
        Success: new RgbColor(0x62, 0xD9, 0xB4),
        Warning: new RgbColor(0xFF, 0xB4, 0x54),
        Error: new RgbColor(0xFF, 0x6B, 0x8B),
        Tool: new RgbColor(0x82, 0xAA, 0xFF),
        System: new RgbColor(0x68, 0xA8, 0xF2),
        User: new RgbColor(0xB8, 0xC4, 0xD4),
        Background: new RgbColor(0x0A, 0x0F, 0x14),
        Panel: new RgbColor(0x0D, 0x12, 0x19),
        Surface: new RgbColor(0x12, 0x18, 0x26),
        Surface2: new RgbColor(0x1A, 0x22, 0x33),
        Border: new RgbColor(0x1F, 0x2A, 0x3D),
        Muted: new RgbColor(0x5C, 0x6F, 0x87),
        Text: new RgbColor(0xB8, 0xC4, 0xD4));

    /// <summary>Built-in catalog in switcher order.</summary>
    public static readonly IReadOnlyList<HarborTheme> BuiltIn =
    [
        HarborDark,
        HarborLight,
        HarborWarm,
        HarborCool,
    ];

    /// <summary>Resolves a built-in by name (case-insensitive); unknown falls back to <see cref="HarborDark" />.</summary>
    public static HarborTheme ByName(string name) =>
        BuiltIn.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? HarborDark;
}
