using System.Text;
namespace Harbor.Terminal.Abstractions.Renderers;
/// <summary>
///     Render context — abstraction over the actual output device (console, file, buffer).
///     Views render to this context, not directly to Console. This enables:
///     - Swapping renderers (ANSI, Plain, Spectre, test buffer)
///     - Capturing output for tests
///     - Composing views in a layout
/// </summary>
/// <remarks>
///     Implementations MUST be thread-safe for the <c>Write*</c> family of methods. Cursor
///     management methods (<see cref="SetCursorPosition" />, <see cref="ClearLine" />, etc.) may
///     be no-ops on contexts that don't support absolute positioning (e.g. streaming renderers).
/// </remarks>
public interface ITuiRenderContext
{
    /// <summary>Width in characters (0 = unknown/unlimited).</summary>
    public int Width { get; }

    /// <summary>Height in characters (0 = unknown/unlimited).</summary>
    public int Height { get; }

    /// <summary>Whether colors are supported.</summary>
    public bool SupportsColor { get; }

    /// <summary>Write raw text.</summary>
    /// <param name="text">The text to write.</param>
    public void Write(string text);

    /// <summary>Write a line.</summary>
    /// <param name="text">Optional text to write before the line terminator.</param>
    public void WriteLine(string? text = null);

    /// <summary>Write colored text. Colors are ANSI codes or named.</summary>
    /// <param name="text">The text to write.</param>
    /// <param name="foreground">Foreground color.</param>
    /// <param name="background">Optional background color.</param>
    public void WriteColored(string text, TuiColor foreground, TuiColor? background = null);

    /// <summary>Write styled text (bold, italic, underline, dim).</summary>
    /// <param name="text">The text to write.</param>
    /// <param name="style">Style flags.</param>
    public void WriteStyled(string text, TuiStyle style);

    /// <summary>Move cursor.</summary>
    /// <param name="row">Target row (0-based).</param>
    /// <param name="col">Target column (0-based).</param>
    public void SetCursorPosition(int row, int col);

    /// <summary>Clear current line.</summary>
    public void ClearLine();

    /// <summary>Clear screen.</summary>
    public void Clear();

    /// <summary>Hide the cursor.</summary>
    public void HideCursor();

    /// <summary>Show the cursor.</summary>
    public void ShowCursor();

    /// <summary>Enter the alternate screen buffer.</summary>
    public void EnterAlternateScreen();

    /// <summary>Exit the alternate screen buffer.</summary>
    public void ExitAlternateScreen();

    /// <summary>Flush any buffered output.</summary>
    public void Flush();
}

/// <summary>
///     Color representation (24-bit RGB).
/// </summary>
/// <param name="R">Red channel (0–255).</param>
/// <param name="G">Green channel (0–255).</param>
/// <param name="B">Blue channel (0–255).</param>
public readonly record struct TuiColor(byte R, byte G, byte B)
{
    /// <summary>The default terminal color.</summary>
    public static readonly TuiColor Default = default;

    /// <summary>Black.</summary>
    public static readonly TuiColor Black = new(0, 0, 0);

    /// <summary>White.</summary>
    public static readonly TuiColor White = new(255, 255, 255);

    /// <summary>Red.</summary>
    public static readonly TuiColor Red = new(255, 0, 0);

    /// <summary>Green.</summary>
    public static readonly TuiColor Green = new(0, 255, 0);

    /// <summary>Blue.</summary>
    public static readonly TuiColor Blue = new(0, 0, 255);

    /// <summary>Yellow.</summary>
    public static readonly TuiColor Yellow = new(255, 255, 0);

    /// <summary>Cyan.</summary>
    public static readonly TuiColor Cyan = new(0, 255, 255);

    /// <summary>Magenta.</summary>
    public static readonly TuiColor Magenta = new(255, 0, 255);

    /// <summary>Mid gray.</summary>
    public static readonly TuiColor Gray = new(128, 128, 128);

    /// <summary>Dark gray.</summary>
    public static readonly TuiColor DarkGray = new(64, 64, 64);

    /// <summary>
    ///     Construct a <see cref="TuiColor" /> from 0–255 RGB integers.
    /// </summary>
    /// <param name="r">Red channel.</param>
    /// <param name="g">Green channel.</param>
    /// <param name="b">Blue channel.</param>
    /// <returns>A new <see cref="TuiColor" />.</returns>
    public static TuiColor FromRgb(int r, int g, int b) => new((byte)r, (byte)g, (byte)b);

    /// <inheritdoc />
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
///     Text style flags.
/// </summary>
[Flags]
public enum TuiStyle
{
    /// <summary>No styling.</summary>
    None = 0,

    /// <summary>Bold.</summary>
    Bold = 1,

    /// <summary>Italic.</summary>
    Italic = 2,

    /// <summary>Underlined.</summary>
    Underline = 4,

    /// <summary>Dimmed (lower brightness).</summary>
    Dim = 8,

    /// <summary>Strikethrough.</summary>
    Strike = 16,

    /// <summary>Reverse video (swap foreground/background).</summary>
    Reverse = 32
}

/// <summary>
///     Capture-based render context for tests. Collects all output in a StringBuilder.
/// </summary>
public sealed class CaptureRenderContext : ITuiRenderContext
{
    private readonly StringBuilder _sb = new();

    /// <summary>
    ///     The captured output so far.
    /// </summary>
    public string Output => _sb.ToString();

    /// <inheritdoc />
    public int Width => 80;

    /// <inheritdoc />
    public int Height => 24;

    /// <inheritdoc />
    public bool SupportsColor => false;

    /// <inheritdoc />
    public void Write(string text) => _sb.Append(text);

    /// <inheritdoc />
    public void WriteLine(string? text = null) => _sb.AppendLine(text ?? string.Empty);

    /// <inheritdoc />
    public void WriteColored(string text, TuiColor foreground, TuiColor? background = null) => _sb.Append(text);

    /// <inheritdoc />
    public void WriteStyled(string text, TuiStyle style) => _sb.Append(text);

    /// <inheritdoc />
    public void SetCursorPosition(int row, int col) { }

    /// <inheritdoc />
    public void ClearLine() { }

    /// <inheritdoc />
    public void Clear() => _sb.Clear();

    /// <inheritdoc />
    public void HideCursor() { }

    /// <inheritdoc />
    public void ShowCursor() { }

    /// <inheritdoc />
    public void EnterAlternateScreen() { }

    /// <inheritdoc />
    public void ExitAlternateScreen() { }

    /// <inheritdoc />
    public void Flush() { }
}
