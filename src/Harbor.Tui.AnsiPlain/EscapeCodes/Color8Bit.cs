using Harbor.CodeGen;

namespace Harbor.Tui.AnsiPlain.EscapeCodes;

/// <summary>8-bit palette slot (0 = default, 1–7 = normal, 8–15 = bright).</summary>
[TerminalEscape]
public enum Color8Bit : byte
{
    /// <summary>Default foreground/background (slot 0).</summary>
    Default,
    /// <summary>Black.</summary>
    Black,
    /// <summary>Red.</summary>
    Red,
    /// <summary>Green.</summary>
    Green,
    /// <summary>Yellow.</summary>
    Yellow,
    /// <summary>Blue.</summary>
    Blue,
    /// <summary>Magenta.</summary>
    Magenta,
    /// <summary>Cyan.</summary>
    Cyan,
    /// <summary>White.</summary>
    White,
    /// <summary>Bright black (gray).</summary>
    BrightBlack,
    /// <summary>Bright red.</summary>
    BrightRed,
    /// <summary>Bright green.</summary>
    BrightGreen,
    /// <summary>Bright yellow.</summary>
    BrightYellow,
    /// <summary>Bright blue.</summary>
    BrightBlue,
    /// <summary>Bright magenta.</summary>
    BrightMagenta,
    /// <summary>Bright cyan.</summary>
    BrightCyan,
    /// <summary>Bright white.</summary>
    BrightWhite
}
