using Harbor.Abstractions.Contracts;

namespace Harbor.Tui.AnsiPlain.EscapeCodes;

/// <summary>SGR text style flags.</summary>
[TerminalEscape]
[Flags]
public enum StyleFlag
{
    /// <summary>No style.</summary>
    None = 0,
    /// <summary>Bold / increased intensity.</summary>
    Bold = 1,
    /// <summary>Dim / decreased intensity.</summary>
    Dim = 2,
    /// <summary>Italic.</summary>
    Italic = 4,
    /// <summary>Underline.</summary>
    Underline = 8,
    /// <summary>Strikethrough.</summary>
    Strike = 16,
    /// <summary>Reverse video.</summary>
    Reverse = 32
}
