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

    public string Reset => StyleFlagEscapeCodes.Reset;

    public string Foreground(TuiColor color) => Color8BitEscapeCodes.ForegroundRgb(color.R, color.G, color.B);

    public string Background(TuiColor color) => Color8BitEscapeCodes.BackgroundRgb(color.R, color.G, color.B);

    public string Style(TuiStyle style) => StyleFlagEscapeCodes.Style(MapStyle(style));

    public string HideCursor => Color8BitEscapeCodes.HideCursor;
    public string ShowCursor => Color8BitEscapeCodes.ShowCursor;
    public string ClearLine => Color8BitEscapeCodes.ClearLine;
    public string ClearScreen => Color8BitEscapeCodes.ClearScreen;
    public string EnterAlternateScreen => Color8BitEscapeCodes.EnterAlternateScreen;
    public string ExitAlternateScreen => Color8BitEscapeCodes.ExitAlternateScreen;

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
