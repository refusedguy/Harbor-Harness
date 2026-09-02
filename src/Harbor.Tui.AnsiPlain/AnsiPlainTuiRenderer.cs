namespace Harbor.Tui.AnsiPlain;

using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Terminal.Abstractions.Views;
using Harbor.Tui.AnsiPlain.EscapeCodes;
using Microsoft.Extensions.Logging;

/// <summary>
///     Unified streaming TUI renderer — the shared event-driven pipeline
///     behind the <c>ansi</c> and <c>plain</c> backends (renderer-unification
///     sprint Phase 4). All styling and terminal control flow through an
///     <see cref="IEscapeCodeStrategy"/>, so the render logic exists exactly
///     once: <see cref="AnsiTuiRenderer"/> selects
///     <see cref="AnsiEscapeStrategy"/> for real terminals, and
///     <see cref="PlainTuiRenderer"/> selects <see cref="NullEscapeStrategy"/>
///     for pipes, CI logs, accessibility and files.
/// </summary>
/// <remarks>
///     <para>
///         <b>Inheritance is intentional:</b> this class is unsealed so that
///         specialized renderers (e.g. <c>SixelTuiRenderer</c> in
///         <c>Harbor.Tui.Sixel</c>) can extend the ANSI mode and reuse the
///         streaming token feed while adding image-emission hooks.
///     </para>
/// </remarks>
public class AnsiPlainTuiRenderer : BaseTuiRenderer
{
    private readonly bool _ownsWriter;
    private readonly TextWriter _writer;

    private protected AnsiPlainTuiRenderer(
        TextWriter writer,
        bool ownsWriter,
        IEscapeCodeStrategy strategy,
        ILogger logger)
        : base(logger)
    {
        _writer = writer;
        _ownsWriter = ownsWriter;
        Context = new AnsiPlainRenderContext(writer, strategy);
    }

    /// <summary>The escape-code strategy driving this renderer instance.</summary>
    public IEscapeCodeStrategy Strategy => ((AnsiPlainRenderContext)Context).Strategy;

    public override ITuiRenderContext Context { get; }

    protected override bool ShouldRenderPlacement(TuiViewPlacement placement, AgentEvent @event)
    {
        // Chat history and the input prompt are handled live by the line REPL
        // (tokens stream directly, the prompt is written by ReadLineAsync). Only
        // the status bar and diff overlay are rendered through the builtin views.
        if (placement is TuiViewPlacement.ChatHistory or TuiViewPlacement.Input) return false;
        return base.ShouldRenderPlacement(placement, @event);
    }

    public override Task RenderAsync(AgentEvent @event, CancellationToken ct = default)
    {
        // Live token streaming is written directly for a smooth character-by-character
        // feed; everything else (status bar, finalized chat history, diff overlay) is
        // rendered through the builtin views in BaseTuiRenderer.
        switch (@event)
        {
            case MessageStartEvent:
                Context.WriteColored("[assistant] ", TuiColor.Cyan);
                break;

            case MessageUpdateEvent mu:
                RenderLiveToken(mu.LlmEvent, Context);
                break;

            case MessageEndEvent:
                Context.WriteLine();
                break;

            // The following are emitted as live lines (not accumulated in the chat
            // history view) so they appear inline without repainting the whole log.
            case ToolExecutionStartEvent tes:
                Context.WriteLine();
                Context.WriteColored($"→ {tes.ToolName}", TuiColor.Blue);
                string args = tes.Args.GetRawText();
                if (!string.IsNullOrEmpty(args) && args != "{}")
                {
                    Context.WriteStyled($" {args}", TuiStyle.Dim);
                }

                Context.WriteLine();
                break;

            case CompactionStartedEvent:
                Context.WriteLine();
                Context.WriteStyled("[compacting context...]", TuiStyle.Dim);
                Context.WriteLine();
                break;

            case CompactionCompletedEvent cc:
                Context.WriteStyled($"[compacted: pruned {cc.PrunedMessageCount} msgs, saved ~{cc.TokensSaved} tokens in {cc.Duration.TotalSeconds:F1}s]", TuiStyle.Dim);
                Context.WriteLine();
                break;

            case AgentErrorEvent err:
                Context.WriteLine();
                Context.WriteColored($"[error] {err.Message}", TuiColor.Red);
                Context.WriteLine();
                break;
        }

        return base.RenderAsync(@event, ct);
    }

    private static void RenderLiveToken(LlmEvent evt, ITuiRenderContext ctx)
    {
        switch (evt)
        {
            case TextDeltaEvent td:
                ctx.Write(td.Delta);
                break;

            case ThinkingDeltaEvent thd:
                ctx.WriteStyled(thd.Delta, TuiStyle.Dim | TuiStyle.Italic);
                break;

            case ToolCallDeltaEvent tcd:
                ctx.WriteStyled(tcd.ArgsDelta, TuiStyle.Dim);
                break;
        }
    }

    public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
    {
        Context.ShowCursor();
        Context.WriteColored(prompt, TuiColor.Green);
        Context.Flush();
        string? line = Console.ReadLine();
        Context.HideCursor();
        return Task.FromResult(Result.Success(line ?? string.Empty));
    }

    public override Task<Result> WriteAsync(string text, CancellationToken ct = default)
    {
        Context.Write(text);
        return Task.FromResult(Result.Success());
    }

    public override Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default)
    {
        Context.WriteLine(text);
        return Task.FromResult(Result.Success());
    }

    public override Task<Result> ClearAsync(CancellationToken ct = default)
    {
        // Strategy-aware: ANSI mode emits the clear-screen sequence; the null
        // strategy yields a no-op, which is the safe behavior for redirected
        // output (Console.Clear throws on pipes).
        Context.Clear();
        return Task.FromResult(Result.Success());
    }

    public override void Dispose()
    {
        Context.ShowCursor();
        if (_ownsWriter)
        {
            _writer.Dispose();
        }
        else
        {
            _writer.Flush();
        }

        base.Dispose();
    }
}

/// <summary>
///     Render context for the unified renderer — writes every styled/cursor
///     operation through an <see cref="IEscapeCodeStrategy"/> into a
///     <see cref="TextWriter"/>.
/// </summary>
internal sealed class AnsiPlainRenderContext : ITuiRenderContext
{
    private readonly TextWriter _writer;
    private readonly Func<int> _width;
    private readonly Func<int> _height;

    public AnsiPlainRenderContext(TextWriter writer, IEscapeCodeStrategy strategy)
        : this(writer, strategy, static () => Console.WindowWidth, static () => Console.WindowHeight)
    {
    }

    public AnsiPlainRenderContext(
        TextWriter writer,
        IEscapeCodeStrategy strategy,
        Func<int> width,
        Func<int> height)
    {
        _writer = writer;
        _width = width;
        _height = height;
        Strategy = strategy;
    }

    public IEscapeCodeStrategy Strategy { get; }

    public int Width
    {
        get
        {
            try
            {
                return _width();
            }
            catch
            {
                return 80;
            }
        }
    }

    public int Height
    {
        get
        {
            try
            {
                return _height();
            }
            catch
            {
                return 24;
            }
        }
    }

    public bool SupportsColor => Strategy.SupportsColor;

    public void Write(string text) => _writer.Write(text);

    public void WriteLine(string? text = null) => _writer.WriteLine(text ?? string.Empty);

    public void WriteColored(string text, TuiColor foreground, TuiColor? background = null)
    {
        if (!Strategy.SupportsColor)
        {
            _writer.Write(text);
            return;
        }

        string fg = Strategy.Foreground(foreground);
        string bg = background.HasValue ? Strategy.Background(background.Value) : string.Empty;
        _writer.Write($"{fg}{bg}{text}{Strategy.Reset}");
    }

    public void WriteStyled(string text, TuiStyle style)
    {
        string codes = Strategy.Style(style);
        _writer.Write(codes.Length == 0 ? text : $"\x1b[{codes}m{text}{Strategy.Reset}");
    }

    public void SetCursorPosition(int row, int col) => _writer.Write($"\x1b[{row};{col}H");

    public void ClearLine() => _writer.Write(Strategy.ClearLine);

    public void Clear() => _writer.Write(Strategy.ClearScreen);

    public void HideCursor() => _writer.Write(Strategy.HideCursor);

    public void ShowCursor() => _writer.Write(Strategy.ShowCursor);

    public void EnterAlternateScreen() => _writer.Write(Strategy.EnterAlternateScreen);

    public void ExitAlternateScreen() => _writer.Write(Strategy.ExitAlternateScreen);

    public void Flush() => _writer.Flush();
}
