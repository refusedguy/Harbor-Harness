namespace Harbor.Tui.AnsiPlain.EscapeCodes;

using Harbor.Terminal.Abstractions.Renderers;
using Terminal = Harbor.Terminal.Abstractions;

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

    /// <summary>
    ///     CUP cursor positioning (<c>ESC[row;colH</c>, 1-based) or empty when
    ///     the sink cannot move the cursor.
    /// </summary>
    string CursorPosition(int row, int col);
}

/// <summary>
///     ANSI SGR escape-code strategy. Style/reset constants come from the
///     generated <c>StyleFlagEscapeCodes</c> (Terminal.Abstractions); truecolor
///     and DEC private-mode sequences stay hand-written here by design — they
///     are not enum-based (see CODEGEN_BOILERPLATE.md §2 Constraints).
/// </summary>
public sealed class AnsiEscapeStrategy : IEscapeCodeStrategy
{
    /// <summary>Singleton instance — the strategy is stateless.</summary>
    public static readonly AnsiEscapeStrategy Instance = new();

    public bool SupportsColor => true;

    public string Reset => Terminal.StyleFlagEscapeCodes.Reset;

    public string Foreground(TuiColor color) => Truecolor("38", color);

    public string Background(TuiColor color) => Truecolor("48", color);

    public string Style(TuiStyle style) => SgrParams(style);

    public string HideCursor => HideCursorSeq;
    public string ShowCursor => ShowCursorSeq;
    public string ClearLine => ClearLineSeq;
    public string ClearScreen => ClearScreenSeq;
    public string EnterAlternateScreen => EnterAlternateScreenSeq;
    public string ExitAlternateScreen => ExitAlternateScreenSeq;

    public string CursorPosition(int row, int col) =>
        $"\x1b[{Math.Max(1, row)};{Math.Max(1, col)}H";

    private static string Truecolor(string ground, TuiColor color) =>
        $"\x1b[{ground};2;{color.R};{color.G};{color.B}m";

    /// <summary>
    ///     SGR parameter list (e.g. <c>"1;4"</c>), or empty for
    ///     <see cref="TuiStyle.None" />. Order and codes mirror the former
    ///     generated <c>FormatStyle</c> so golden frames stay stable.
    /// </summary>
    private static string SgrParams(TuiStyle style)
    {
        StyleFlag flags = MapStyle(style);
        if (flags == StyleFlag.None)
            return string.Empty;

        StringBuilder sb = new(11);
        AppendParam(ref sb, flags, StyleFlag.Bold, '1');
        AppendParam(ref sb, flags, StyleFlag.Dim, '2');
        AppendParam(ref sb, flags, StyleFlag.Italic, '3');
        AppendParam(ref sb, flags, StyleFlag.Underline, '4');
        AppendParam(ref sb, flags, StyleFlag.Strike, '9');
        AppendParam(ref sb, flags, StyleFlag.Reverse, '7');
        return sb.ToString();
    }

    private static void AppendParam(ref StringBuilder sb, StyleFlag flags, StyleFlag flag, char code)
    {
        if (!flags.HasFlag(flag))
            return;
        if (sb.Length > 0)
            sb.Append(';');
        sb.Append(code);
    }

    private const string HideCursorSeq = "\x1b[?25l";
    private const string ShowCursorSeq = "\x1b[?25h";
    private const string ClearLineSeq = "\x1b[2K\r";
    private const string ClearScreenSeq = "\x1b[2J\x1b[H";
    private const string EnterAlternateScreenSeq = "\x1b[?1049h";
    private const string ExitAlternateScreenSeq = "\x1b[?1049l";

    private static StyleFlag MapStyle(TuiStyle style)
    {
        StyleFlag flags = StyleFlag.None;
        if (style.HasFlag(TuiStyle.Bold)) flags |= StyleFlag.Bold;
        if (style.HasFlag(TuiStyle.Dim)) flags |= StyleFlag.Dim;
        if (style.HasFlag(TuiStyle.Italic)) flags |= StyleFlag.Italic;
        if (style.HasFlag(TuiStyle.Underline)) flags |= StyleFlag.Underline;
        if (style.HasFlag(TuiStyle.Strike)) flags |= StyleFlag.Strike;
        if (style.HasFlag(TuiStyle.Reverse)) flags |= StyleFlag.Reverse;
        return flags;
    }
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
    public string CursorPosition(int row, int col) => string.Empty;
}
