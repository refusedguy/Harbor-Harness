namespace Harbor.Terminal.Abstractions.Renderers;

/// <summary>
///     Minimal backend contract used by the generated <see cref="IRenderAsyncBackend" />
///     partial classes (RendererAdapterGenerator). Each renderer context implements
///     this interface so the shared event-dispatch override can call into the
///     renderer-specific output without the generator knowing about concrete types.
/// </summary>
public interface ITuiRenderBackend
{
    /// <summary>Write colored text (foreground, optional background).</summary>
    void WriteColored(string text, TuiColor foreground, TuiColor? background = null);

    /// <summary>Write styled text (bold, italic, underline, dim, strike, reverse).</summary>
    void WriteStyled(string text, TuiStyle style);

    /// <summary>Write a plain text line.</summary>
    void WriteLine(string? text = null);

    /// <summary>Write raw text without styling.</summary>
    void Write(string text);

    /// <summary>Show the terminal cursor.</summary>
    void ShowCursor();

    /// <summary>Hide the terminal cursor.</summary>
    void HideCursor();
}
