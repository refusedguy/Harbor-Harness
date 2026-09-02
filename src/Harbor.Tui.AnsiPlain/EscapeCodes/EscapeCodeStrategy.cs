namespace Harbor.Tui.AnsiPlain.EscapeCodes;

using Harbor.Terminal.Abstractions.Renderers;

/// <summary>
///     Escape-code strategy for the unified <c>AnsiPlain</c> renderer
///     (renderer-unification sprint Phase 4). The renderer emits every styled
///     or cursor-affecting write through this strategy, so the same render
///     pipeline serves real ANSI terminals and plain-text sinks (pipes, CI
///     logs, accessibility, files) without duplicating the render logic.
/// </summary>
/// <remarks>
///     <para>
///         Strategy pattern (GoF): <see cref="AnsiEscapeStrategy"/> produces
///         ECMA-48 SGR/CSI sequences; <see cref="NullEscapeStrategy"/> returns
///         empty strings for every code, collapsing all styling to raw text.
///         All members are pure string factories — no Console side effects —
///         which keeps the strategy AOT-safe and unit-testable.
///     </para>
/// </remarks>
public interface IEscapeCodeStrategy
{
    /// <summary>Whether this strategy actually styles output.</summary>
    bool SupportsColor { get; }

    /// <summary>SGR reset (or empty for the null strategy).</summary>
    string Reset { get; }

    /// <summary>SGR 24-bit foreground sequence for <paramref name="color"/>.</summary>
    string Foreground(TuiColor color);

    /// <summary>SGR 24-bit background sequence for <paramref name="color"/>.</summary>
    string Background(TuiColor color);

    /// <summary>
    ///     SGR parameter list for <paramref name="style"/> (e.g. <c>"1;3"</c>),
    ///     or empty when the style is empty / unsupported.
    /// </summary>
    string Style(TuiStyle style);

    string HideCursor { get; }
    string ShowCursor { get; }
    string ClearLine { get; }
    string ClearScreen { get; }
    string EnterAlternateScreen { get; }
    string ExitAlternateScreen { get; }
}

/// <summary>
///     ANSI SGR escape-code strategy. Foreground/background are emitted as
///     truecolor (24-bit) sequences — the same encoding the former
///     <c>Harbor.Tui.Ansi</c> renderer used inline.
/// </summary>
public sealed class AnsiEscapeStrategy : IEscapeCodeStrategy
{
    /// <summary>Singleton instance — the strategy is stateless.</summary>
    public static readonly AnsiEscapeStrategy Instance = new();

    public bool SupportsColor => true;

    // Reset / decoration (kept as named constants for callers that compose
    // raw sequences, e.g. QR/OSC emission paths).
    public const string ResetSeq = "\x1b[0m";
    public const string Bold = "\x1b[1m";
    public const string Dim = "\x1b[2m";
    public const string Italic = "\x1b[3m";
    public const string Underline = "\x1b[4m";
    public const string Strike = "\x1b[9m";

    // 16-color foreground palette (legacy callers).
    public const string Red = "\x1b[31m";
    public const string Green = "\x1b[32m";
    public const string Yellow = "\x1b[33m";
    public const string Blue = "\x1b[34m";
    public const string Magenta = "\x1b[35m";
    public const string Cyan = "\x1b[36m";

    public string Reset => ResetSeq;

    public string Foreground(TuiColor color) => $"\x1b[38;2;{color.R};{color.G};{color.B}m";

    public string Background(TuiColor color) => $"\x1b[48;2;{color.R};{color.G};{color.B}m";

    public string Style(TuiStyle style)
    {
        var codes = new List<string>(6);
        if (style.HasFlag(TuiStyle.Bold)) codes.Add("1");
        if (style.HasFlag(TuiStyle.Dim)) codes.Add("2");
        if (style.HasFlag(TuiStyle.Italic)) codes.Add("3");
        if (style.HasFlag(TuiStyle.Underline)) codes.Add("4");
        if (style.HasFlag(TuiStyle.Strike)) codes.Add("9");
        if (style.HasFlag(TuiStyle.Reverse)) codes.Add("7");
        return codes.Count == 0 ? string.Empty : string.Join(';', codes);
    }

    public string HideCursor => "\x1b[?25l";
    public string ShowCursor => "\x1b[?25h";
    public string ClearLine => "\x1b[2K\r";
    public string ClearScreen => "\x1b[2J\x1b[H";
    public string EnterAlternateScreen => "\x1b[?1049h";
    public string ExitAlternateScreen => "\x1b[?1049l";
}

/// <summary>
///     Null escape-code strategy — every code collapses to the empty string,
///     so the unified renderer degrades to pure plain text (pipes, CI,
///     accessibility, files).
/// </summary>
public sealed class NullEscapeStrategy : IEscapeCodeStrategy
{
    /// <summary>Singleton instance — the strategy is stateless.</summary>
    public static readonly NullEscapeStrategy Instance = new();

    public bool SupportsColor => false;

    public string Reset => string.Empty;
    public string Foreground(TuiColor color) => string.Empty;
    public string Background(TuiColor color) => string.Empty;
    public string Style(TuiStyle style) => string.Empty;
    public string HideCursor => string.Empty;
    public string ShowCursor => string.Empty;
    public string ClearLine => string.Empty;
    public string ClearScreen => string.Empty;
    public string EnterAlternateScreen => string.Empty;
    public string ExitAlternateScreen => string.Empty;
}
