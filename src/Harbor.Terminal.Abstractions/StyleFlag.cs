using Harbor.Abstractions.Contracts;

namespace Harbor.Terminal.Abstractions;

/// <summary>
///     SGR style flags emitted by the <see cref="EscapeCodeGenerator" />.
///     Each value maps to a single ECMA-48 SGR parameter.
/// </summary>
[TerminalEscape]
public enum StyleFlag : byte
{
    /// <summary>No style (SGR 0 / reset).</summary>
    Reset = 0,

    /// <summary>Bold / increased intensity (SGR 1).</summary>
    Bold = 1,

    /// <summary>Dim / decreased intensity (SGR 2).</summary>
    Dim = 2,

    /// <summary>Italic (SGR 3).</summary>
    Italic = 3,

    /// <summary>Underline (SGR 4).</summary>
    Underline = 4,

    /// <summary>Blink (SGR 5).</summary>
    Blink = 5,

    /// <summary>Reverse video (SGR 7).</summary>
    Reverse = 7,

    /// <summary>Hidden (SGR 8).</summary>
    Hidden = 8,

    /// <summary>Strikethrough (SGR 9).</summary>
    Strike = 9,
}
