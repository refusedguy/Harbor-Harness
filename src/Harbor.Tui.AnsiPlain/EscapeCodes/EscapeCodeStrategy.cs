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

    // #493 left these two interpolating on purpose. Each costs exactly one
    // string — the sequence itself — and neither has a cacheable key space:
    // truecolor is 16.7M R;G;B triples, and a cursor move is bounded only by
    // the terminal size. A cache would trade a single short-lived string for
    // unbounded retained state to save nothing on the styled-run path, which
    // is what SgrParamTable covers. The plain backend is the low-volume one
    // (CellForge.Engine/Rendering/AnsiWriter.cs is already 0-alloc); revisit
    // both together rather than alone.
    public string CursorPosition(int row, int col) =>
        $"\x1b[{Math.Max(1, row)};{Math.Max(1, col)}H";

    private static string Truecolor(string ground, TuiColor color) =>
        $"\x1b[{ground};2;{color.R};{color.G};{color.B}m";

    /// <summary>
    ///     Longest possible SGR parameter list: six single-digit codes joined
    ///     by five <c>;</c> separators.
    /// </summary>
    private const int MaxSgrLength = 11;

    /// <summary>Number of distinct <see cref="StyleFlag" /> combinations (six bits, incl. <see cref="StyleFlag.None" />).</summary>
    private const int SgrTableSize = 1 << 6;

    /// <summary>
    ///     #493: the SGR parameter list for every style, precomputed once per
    ///     process. <see cref="StyleFlag" /> is six bits wide, so the whole
    ///     input space is 64 entries and the table is <b>complete</b> — there
    ///     is no miss and therefore no fallback. <see cref="SgrParams" /> is a
    ///     single array read: the styled-run path no longer allocates a
    ///     <c>StringBuilder</c> and a result string per styled run, which a
    ///     text-heavy frame paid for once per run. The zero only holds because
    ///     <see cref="MapStyle" /> is box-free too.
    /// </summary>
    private static readonly string[] SgrParamTable = BuildSgrParamTable();

    /// <summary>
    ///     Build <see cref="SgrParamTable" /> from the same code/order the
    ///     per-call builder used, so painted bytes are unchanged (golden
    ///     frames depend on it). The <c>StringBuilder</c> this replaces ran
    ///     once per combination at type init instead of once per styled run.
    /// </summary>
    private static string[] BuildSgrParamTable()
    {
        var table = new string[SgrTableSize];

        // stackalloc is hoisted out of the loop on purpose: inside a loop body
        // it would re-stackalloc on every iteration.
        Span<char> codes = stackalloc char[MaxSgrLength];

        for (int bits = 0; bits < table.Length; bits++)
        {
            var flags = (StyleFlag)bits;
            int length = 0;
            AppendParam(codes, ref length, flags, StyleFlag.Bold, '1');
            AppendParam(codes, ref length, flags, StyleFlag.Dim, '2');
            AppendParam(codes, ref length, flags, StyleFlag.Italic, '3');
            AppendParam(codes, ref length, flags, StyleFlag.Underline, '4');
            AppendParam(codes, ref length, flags, StyleFlag.Strike, '9');
            AppendParam(codes, ref length, flags, StyleFlag.Reverse, '7');
            table[bits] = new string(codes[..length]);
        }

        return table;
    }

    /// <summary>
    ///     SGR parameter list (e.g. <c>"1;4"</c>), or empty for
    ///     <see cref="TuiStyle.None" />. Order and codes mirror the former
    ///     generated <c>FormatStyle</c> so golden frames stay stable.
    /// </summary>
    private static string SgrParams(TuiStyle style) => SgrParamTable[(int)MapStyle(style)];

    private static void AppendParam(Span<char> codes, ref int length, StyleFlag flags, StyleFlag flag, char code)
    {
        if ((flags & flag) == 0)
            return;
        if (length > 0)
            codes[length++] = ';';
        codes[length++] = code;
    }

    private const string HideCursorSeq = "\x1b[?25l";
    private const string ShowCursorSeq = "\x1b[?25h";
    private const string ClearLineSeq = "\x1b[2K\r";
    private const string ClearScreenSeq = "\x1b[2J\x1b[H";
    private const string EnterAlternateScreenSeq = "\x1b[?1049h";
    private const string ExitAlternateScreenSeq = "\x1b[?1049l";

    private static StyleFlag MapStyle(TuiStyle style)
    {
        // Bit tests, not Enum.HasFlag. HasFlag takes an Enum, so every call
        // boxes its argument — six boxes per styled run, which is what kept
        // SgrParams allocating (288 B/run, measured) even after the parameter
        // table removed the builder. For a [Flags] enum the two are equivalent.
        int bits = (int)style;
        StyleFlag flags = StyleFlag.None;
        if ((bits & (int)TuiStyle.Bold) != 0) flags |= StyleFlag.Bold;
        if ((bits & (int)TuiStyle.Dim) != 0) flags |= StyleFlag.Dim;
        if ((bits & (int)TuiStyle.Italic) != 0) flags |= StyleFlag.Italic;
        if ((bits & (int)TuiStyle.Underline) != 0) flags |= StyleFlag.Underline;
        if ((bits & (int)TuiStyle.Strike) != 0) flags |= StyleFlag.Strike;
        if ((bits & (int)TuiStyle.Reverse) != 0) flags |= StyleFlag.Reverse;
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
