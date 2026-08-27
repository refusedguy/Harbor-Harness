namespace Harbor.DesignSystem;

/// <summary>
/// Terminal-specific design tokens matching the HTML design-system report.
/// These are the exact colors specified for ConsoleEx and TUI rendering.
/// </summary>
public static class TerminalColorPalette
{
    public static readonly RgbColor Accent = new(0x39, 0xBA, 0xE6);
    public static readonly RgbColor Success = new(0x7F, 0xD9, 0x62);
    public static readonly RgbColor Warning = new(0xFF, 0xB4, 0x54);
    public static readonly RgbColor Error   = new(0xFF, 0x6B, 0x6B);
    public static readonly RgbColor Tool    = new(0xD2, 0xA6, 0xFF);
    public static readonly RgbColor System  = new(0xF2, 0x96, 0x68);
    public static readonly RgbColor User    = new(0xB3, 0xB9, 0xC5);

    public static readonly RgbColor Background = new(0x0A, 0x0E, 0x14);
    public static readonly RgbColor Panel      = new(0x0D, 0x11, 0x17);
    public static readonly RgbColor Surface    = new(0x13, 0x18, 0x20);
    public static readonly RgbColor Surface2   = new(0x1A, 0x1F, 0x2B);
    public static readonly RgbColor Border     = new(0x1F, 0x24, 0x30);
    public static readonly RgbColor Muted      = new(0x5C, 0x67, 0x73);

    public static readonly RgbColor Text      = new(0xB3, 0xB9, 0xC5);
    public static readonly RgbColor TextDim   = Muted;
}
