namespace Harbor.DesignSystem;

/// <summary>
/// Terminal-specific design tokens matching the HTML design-system report.
/// These are the exact colors specified for ConsoleEx and TUI rendering.
/// </summary>
public static class TerminalColorPalette
{
    // Primary semantic colors from HTML spec :root
    public static readonly RgbColor Accent = (0x39, 0xBA, 0xE6);   // --accent
    public static readonly RgbColor Success = (0x7F, 0xD9, 0x62);  // --ok
    public static readonly RgbColor Warning = (0xFF, 0xB4, 0x54);  // --warn
    public static readonly RgbColor Error   = (0xFF, 0x6B, 0x6B);  // --err
    public static readonly RgbColor Tool    = (0xD2, 0xA6, 0xFF);  // --tool
    public static readonly RgbColor System  = (0xF2, 0x96, 0x68);  // --system
    public static readonly RgbColor User    = (0xB3, 0xB9, 0xC5);  // --user

    // Surfaces from HTML spec
    public static readonly RgbColor Background = (0x0A, 0x0E, 0x14); // --bg
    public static readonly RgbColor Panel      = (0x0D, 0x11, 0x17); // --panel
    public static readonly RgbColor Surface    = (0x13, 0x18, 0x20); // --surface
    public static readonly RgbColor Surface2   = (0x1A, 0x1F, 0x2B); // --surface2
    public static readonly RgbColor Border     = (0x1F, 0x24, 0x30); // --border
    public static readonly RgbColor Muted      = (0x5C, 0x67, 0x73); // --dim

    // Text
    public static readonly RgbColor Text      = (0xB3, 0xB9, 0xC5); // --text
    public static readonly RgbColor TextDim   = Muted;
}
