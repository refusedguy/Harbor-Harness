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
    // raw sequences, e.g. QR/OSC emission paths). These are compile-time
    // constants by contract; the runtime emission paths below source their
    // sequences from the generated EscapeCodes tables instead.
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

    public string Foreground(TuiColor color)
    {
        Span<char> buf = stackalloc char[EscapeCodes.RgbFormatLength];
        int n = EscapeCodes.FormatForeground(color.R, color.G, color.B, buf);
        return new string(buf[..n]);
    }

    public string Background(TuiColor color)
    {
        Span<char> buf = stackalloc char[EscapeCodes.RgbFormatLength];
        int n = EscapeCodes.FormatBackground(color.R, color.G, color.B, buf);
        return new string(buf[..n]);
    }

    public string Style(TuiStyle style)
    {
        StyleFlag flag = StyleFlag.None;
        if ((style & TuiStyle.Bold) != 0) flag |= StyleFlag.Bold;
        if ((style & TuiStyle.Dim) != 0) flag |= StyleFlag.Dim;
        if ((style & TuiStyle.Italic) != 0) flag |= StyleFlag.Italic;
        if ((style & TuiStyle.Underline) != 0) flag |= StyleFlag.Underline;
        if ((style & TuiStyle.Strike) != 0) flag |= StyleFlag.Strike;
        if ((style & TuiStyle.Reverse) != 0) flag |= StyleFlag.Reverse;

        Span<char> buf = stackalloc char[EscapeCodes.StyleFormatLength];
        int n = EscapeCodes.FormatStyle(flag, buf);
        return n == 0 ? string.Empty : new string(buf[..n]);
    }

    // ── Generated-table-sourced control sequences ─────────────────────────
    // Cached once from the EscapeCodeGenerator's [TerminalEscape] tables —
    // the generated class is the single source of truth for these bytes.
    private static readonly string s_hideCursor = new string(EscapeCodes.HideCursor);
    private static readonly string s_showCursor = new string(EscapeCodes.ShowCursor);
    private static readonly string s_clearLine = new string(EscapeCodes.ClearLine);
    private static readonly string s_clearScreen = new string(EscapeCodes.ClearScreen);
    private static readonly string s_enterAltScreen = new string(EscapeCodes.EnterAlternateScreen);
    private static readonly string s_exitAltScreen = new string(EscapeCodes.ExitAlternateScreen);

    public string HideCursor => s_hideCursor;
    public string ShowCursor => s_showCursor;
    public string ClearLine => s_clearLine;
    public string ClearScreen => s_clearScreen;
    public string EnterAlternateScreen => s_enterAltScreen;
    public string ExitAlternateScreen => s_exitAltScreen;
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
