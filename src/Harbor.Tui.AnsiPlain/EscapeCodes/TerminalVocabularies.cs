using Harbor.CodeGen;

namespace Harbor.Tui.AnsiPlain.EscapeCodes;

/// <summary>
///     Terminal escape-code vocabularies consumed by the
///     <c>EscapeCodeGenerator</c> — annotating an enum with
///     <c>[TerminalEscape]</c> makes the generator emit the zero-allocation
///     <see cref="EscapeCodes" /> static class (precomputed
///     <see cref="System.ReadOnlySpan{T}" /> ECMA-48 tables and stack-only
///     CSI/SGR formatters) into this namespace.
/// </summary>

/// <summary>8-bit SGR palette slot (ECMA-48 SGR 30–37 standard / 90–97 bright).</summary>
[TerminalEscape]
public enum Color8Bit
{
    /// <summary>Terminal default foreground/background (SGR 39/49).</summary>
    Default = 0,

    /// <summary>Standard black (SGR 30).</summary>
    Black = 30,

    /// <summary>Standard red (SGR 31).</summary>
    Red = 31,

    /// <summary>Standard green (SGR 32).</summary>
    Green = 32,

    /// <summary>Standard yellow (SGR 33).</summary>
    Yellow = 33,

    /// <summary>Standard blue (SGR 34).</summary>
    Blue = 34,

    /// <summary>Standard magenta (SGR 35).</summary>
    Magenta = 35,

    /// <summary>Standard cyan (SGR 36).</summary>
    Cyan = 36,

    /// <summary>Standard white (SGR 37).</summary>
    White = 37,

    /// <summary>Bright black / gray (SGR 90).</summary>
    BrightBlack = 90,

    /// <summary>Bright red (SGR 91).</summary>
    BrightRed = 91,

    /// <summary>Bright green (SGR 92).</summary>
    BrightGreen = 92,

    /// <summary>Bright yellow (SGR 93).</summary>
    BrightYellow = 93,

    /// <summary>Bright blue (SGR 94).</summary>
    BrightBlue = 94,

    /// <summary>Bright magenta (SGR 95).</summary>
    BrightMagenta = 95,

    /// <summary>Bright cyan (SGR 96).</summary>
    BrightCyan = 96,

    /// <summary>Bright white (SGR 97).</summary>
    BrightWhite = 97,
}

/// <summary>Cursor movement direction (ECMA-48 CSI n A/B/C/D).</summary>
[TerminalEscape]
public enum CursorDirection
{
    /// <summary>Cursor up — <c>ESC[n A</c>.</summary>
    Up = 0,

    /// <summary>Cursor down — <c>ESC[n B</c>.</summary>
    Down = 1,

    /// <summary>Cursor forward — <c>ESC[n C</c>.</summary>
    Forward = 2,

    /// <summary>Cursor backward — <c>ESC[n D</c>.</summary>
    Backward = 3,
}

/// <summary>
///     SGR decoration style bits — member <i>values</i> are distinct flags,
///     while the emitted SGR parameter codes (1/2/3/4/9/7) are fixed by the
///     generated <see cref="EscapeCodes.FormatStyle" /> table keyed by member
///     name. Bits never collide with SGR semantics.
/// </summary>
[Flags]
[TerminalEscape]
public enum StyleFlag
{
    /// <summary>No decoration.</summary>
    None = 0,

    /// <summary>Bold / increased intensity (SGR 1).</summary>
    Bold = 1 << 0,

    /// <summary>Dim / decreased intensity (SGR 2).</summary>
    Dim = 1 << 1,

    /// <summary>Italic (SGR 3).</summary>
    Italic = 1 << 2,

    /// <summary>Underline (SGR 4).</summary>
    Underline = 1 << 3,

    /// <summary>Reverse video (SGR 7).</summary>
    Reverse = 1 << 4,

    /// <summary>Strikethrough (SGR 9).</summary>
    Strike = 1 << 5,
}
