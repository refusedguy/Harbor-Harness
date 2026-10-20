using Harbor.Terminal.Abstractions.Renderers;

namespace Harbor.TestKit;

/// <summary>
///     An <see cref="ITuiRenderContext" /> that renders nothing.
/// </summary>
/// <remarks>
///     Three hand-written no-op render contexts existed across two projects,
///     character for character: 80 by 24, no colour, every write discarded.
///     A context that renders nothing is what a test needs when the thing
///     under test is the DECISION to render, not the rendering — and it is
///     also what a test needs when the rendering is asserted somewhere else.
///     (For the second case there is already a
///     <c>CaptureRenderContext</c> in <c>Harbor.Terminal.Abstractions</c>;
///     this type is its no-op twin.)
/// </remarks>
public sealed class NullRenderContext : ITuiRenderContext
{
    /// <summary>
    ///     Creates a no-op render context.
    /// </summary>
    /// <param name="width">The width the context reports.</param>
    /// <param name="height">The height the context reports.</param>
    public NullRenderContext(int width = 80, int height = 24)
    {
        Width = width;
        Height = height;
    }

    /// <inheritdoc />
    public int Width { get; }

    /// <inheritdoc />
    public int Height { get; }

    /// <inheritdoc />
    public bool SupportsColor => false;

    /// <inheritdoc />
    public void Write(string text)
    {
    }

    /// <inheritdoc />
    public void WriteLine(string? text = null)
    {
    }

    /// <inheritdoc />
    public void WriteColored(string text, TuiColor foreground, TuiColor? background = null)
    {
    }

    /// <inheritdoc />
    public void WriteStyled(string text, TuiStyle style)
    {
    }

    /// <inheritdoc />
    public void SetCursorPosition(int row, int col)
    {
    }

    /// <inheritdoc />
    public void ClearLine()
    {
    }

    /// <inheritdoc />
    public void Clear()
    {
    }

    /// <inheritdoc />
    public void HideCursor()
    {
    }

    /// <inheritdoc />
    public void ShowCursor()
    {
    }

    /// <inheritdoc />
    public void EnterAlternateScreen()
    {
    }

    /// <inheritdoc />
    public void ExitAlternateScreen()
    {
    }

    /// <inheritdoc />
    public void Flush()
    {
    }
}
