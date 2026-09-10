using Harbor.Abstractions.Contracts;

namespace Harbor.Tui.AnsiPlain.EscapeCodes;

/// <summary>Cursor movement direction for CSI sequences.</summary>
[TerminalEscape]
public enum CursorDirection
{
    /// <summary>Move cursor up.</summary>
    Up,
    /// <summary>Move cursor down.</summary>
    Down,
    /// <summary>Move cursor forward (right).</summary>
    Forward,
    /// <summary>Move cursor backward (left).</summary>
    Back
}
