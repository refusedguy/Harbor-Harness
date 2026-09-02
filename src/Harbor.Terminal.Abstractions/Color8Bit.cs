using Harbor.Abstractions.Contracts;

namespace Harbor.Terminal.Abstractions;

/// <summary>
///     256-color palette indices for the <see cref="EscapeCodeGenerator" />.
///     Standard 0–15 system colors plus 16–231 6×6×6 RGB cube and 232–255
///     grayscale ramp.
/// </summary>
[TerminalEscape]
public enum Color8Bit : byte
{
    /// <summary>System color: Black.</summary>
    Black = 0,
    /// <summary>System color: Red.</summary>
    Red = 1,
    /// <summary>System color: Green.</summary>
    Green = 2,
    /// <summary>System color: Yellow.</summary>
    Yellow = 3,
    /// <summary>System color: Blue.</summary>
    Blue = 4,
    /// <summary>System color: Magenta.</summary>
    Magenta = 5,
    /// <summary>System color: Cyan.</summary>
    Cyan = 6,
    /// <summary>System color: White (light gray).</summary>
    White = 7,
    /// <summary>High-intensity: Bright Black (gray).</summary>
    BrightBlack = 8,
    /// <summary>High-intensity: Bright Red.</summary>
    BrightRed = 9,
    /// <summary>High-intensity: Bright Green.</summary>
    BrightGreen = 10,
    /// <summary>High-intensity: Bright Yellow.</summary>
    BrightYellow = 11,
    /// <summary>High-intensity: Bright Blue.</summary>
    BrightBlue = 12,
    /// <summary>High-intensity: Bright Magenta.</summary>
    BrightMagenta = 13,
    /// <summary>High-intensity: Bright Cyan.</summary>
    BrightCyan = 14,
    /// <summary>High-intensity: Bright White.</summary>
    BrightWhite = 15,

    /// <summary>First RGB-cube color (16).</summary>
    Rgb0 = 16,
    /// <summary>Last RGB-cube color (231).</summary>
    RgbMax = 231,

    /// <summary>First grayscale color (232).</summary>
    Gray0 = 232,
    /// <summary>Last grayscale color (255).</summary>
    GrayMax = 255,
}
