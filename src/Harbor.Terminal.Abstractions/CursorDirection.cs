using Harbor.Abstractions.Contracts;

namespace Harbor.Terminal.Abstractions;

/// <summary>
///     Cursor movement direction for the <see cref="EscapeCodeGenerator" />.
/// </summary>
[TerminalEscape]
public enum CursorDirection : byte
{
    /// <summary>Cursor up (CUU).</summary>
    Up = 0,

    /// <summary>Cursor down (CUD).</summary>
    Down = 1,

    /// <summary>Cursor forward (CUF).</summary>
    Forward = 2,

    /// <summary>Cursor back (CUB).</summary>
    Back = 3,

    /// <summary>Cursor next line (CNL).</summary>
    NextLine = 4,

    /// <summary>Cursor previous line (CPL).</summary>
    PreviousLine = 5,

    /// <summary>Cursor horizontal absolute (CHA).</summary>
    HorizontalAbsolute = 6,

    /// <summary>Cursor position (CUP) — row + column.</summary>
    Position = 7,

    /// <summary>Erase in display (ED).</summary>
    EraseDisplay = 8,

    /// <summary>Erase in line (EL).</summary>
    EraseLine = 9,

    /// <summary>Scroll up (SU).</summary>
    ScrollUp = 10,

    /// <summary>Scroll down (SD).</summary>
    ScrollDown = 11,

    /// <summary>Save cursor position (SCP / DECSC).</summary>
    Save = 12,

    /// <summary>Restore cursor position (RCP / DECRC).</summary>
    Restore = 13,

    /// <summary>Hide cursor (DECTCEM).</summary>
    Hide = 14,

    /// <summary>Show cursor (DECTCEM).</summary>
    Show = 15,

    /// <summary>Enter alternate screen buffer (DECSET 1047/1048/1049).</summary>
    EnterAlternateScreen = 16,

    /// <summary>Exit alternate screen buffer (DECRST 1047/1048/1049).</summary>
    ExitAlternateScreen = 17,
}
