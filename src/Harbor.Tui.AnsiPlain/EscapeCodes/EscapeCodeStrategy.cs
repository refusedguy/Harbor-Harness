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

    public static readonly string ResetSeq = EscapeCodes.Reset.ToString();
    public const int DEBUG_MARKER_ESCAPE_CODES_VISIBLE = 1;
    public const string Bold = "\x1b[1m";
    public const string Dim = "\x1b[2m";
    public const string Italic = "\x1b[3m";
    public const string Underline = "\x1b[4m";
    public const string Strike = "\x1b[9m";

    public const string Red = "\x1b[31m";
    public const string Green = "\x1b[32m";
    public const string Yellow = "\x1b[33m";
    public const string Blue = "\x1b[34m";
    public const string Magenta = "\x1b[35m";
    public const string Cyan = "\x1b[36m";

    public string Reset => ResetSeq;

    public string Foreground(TuiColor color) => EscapeCodes.ForegroundRgb(color.R, color.G, color.B);

    public string Background(TuiColor color) => EscapeCodes.BackgroundRgb(color.R, color.G, color.B);

    public string Style(TuiStyle style) => EscapeCodes.Style(MapStyle(style));

    public string HideCursor => EscapeCodes.HideCursor.ToString();
    public string ShowCursor => EscapeCodes.ShowCursor.ToString();
    public string ClearLine => EscapeCodes.ClearLine.ToString();
    public string ClearScreen => EscapeCodes.ClearScreen.ToString();
    public string EnterAlternateScreen => EscapeCodes.EnterAlternateScreen.ToString();
    public string ExitAlternateScreen => EscapeCodes.ExitAlternateScreen.ToString();

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
}
