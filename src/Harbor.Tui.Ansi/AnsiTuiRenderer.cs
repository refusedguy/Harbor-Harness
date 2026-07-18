using Harbor.Tui.Abstractions;
using Harbor.Tui.Abstractions.Renderers;
using Harbor.Tui.Abstractions.Views;
using Microsoft.Extensions.Logging;
namespace Harbor.Tui.Ansi;
/// <summary>
///     ANSI-based TUI renderer with full color/style support.
///     Implements Strategy pattern (GOF) via BaseTuiRenderer.
/// </summary>
/// <remarks>
///     <para>
///         <b>Inheritance is intentional:</b> this class is unsealed so that
///         specialized renderers (e.g. <c>SixelTuiRenderer</c> in
///         <c>Harbor.Tui.Sixel</c>) can extend it and reuse the streaming
///         token feed while adding image-emission hooks. We chose inheritance
///         over composition here because the Sixel renderer needs to override
///         <see cref="RenderAsync" /> with minimal logic — calling
///         <c>base.RenderAsync</c> for the common case and only intercepting
///         <c>ToolExecutionEndEvent</c> payloads that may carry image bytes.
///         Composition would force a wider surface area (re-declaring
///         <c>Context</c>, <c>ReadLineAsync</c>, <c>WriteAsync</c>, etc.) for
///         no decoupling benefit since the Sixel renderer is fundamentally an
///         ANSI renderer with one extra behavior.
///     </para>
/// </remarks>
public class AnsiTuiRenderer : BaseTuiRenderer
{

    public AnsiTuiRenderer(ILogger<AnsiTuiRenderer> logger) : base(logger)
    {
        Context = new AnsiRenderContext();
    }
    public override ITuiRenderContext Context { get; }

    protected override bool ShouldRenderPlacement(TuiViewPlacement placement, AgentEvent @event)
    {
        // Chat history and the input prompt are handled live by the line REPL
        // (tokens stream directly, the prompt is written by ReadLineAsync). Only
        // the status bar and diff overlay are rendered through the builtin views.
        if (placement is TuiViewPlacement.ChatHistory or TuiViewPlacement.Input) return false;
        return base.ShouldRenderPlacement(placement, @event);
    }

    public override Task<Result> InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            Context.HideCursor();
            return base.InitializeAsync(ct);
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Failure(ex.Message));
        }
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
        Context.Clear();
        return Task.FromResult(Result.Success());
    }

    public override void Dispose()
    {
        Context.ShowCursor();
        base.Dispose();
    }
}

/// <summary>
///     ANSI-based render context — emits escape codes to Console.
/// </summary>
internal sealed class AnsiRenderContext : ITuiRenderContext
{
    public int Width => Console.WindowWidth;
    public int Height => Console.WindowHeight;
    public bool SupportsColor => true;

    public void Write(string text) => Console.Write(text);

    public void WriteLine(string? text = null) => Console.WriteLine(text ?? string.Empty);

    public void WriteColored(string text, TuiColor foreground, TuiColor? background = null)
    {
        string fgCode = $"\x1b[38;2;{foreground.R};{foreground.G};{foreground.B}m";
        string bgCode = background.HasValue
            ? $"\x1b[48;2;{background.Value.R};{background.Value.G};{background.Value.B}m"
            : string.Empty;
        Console.Write($"{fgCode}{bgCode}{text}\x1b[0m");
    }

    public void WriteStyled(string text, TuiStyle style)
    {
        var codes = new List<string>();
        if (style.HasFlag(TuiStyle.Bold)) codes.Add("1");
        if (style.HasFlag(TuiStyle.Italic)) codes.Add("3");
        if (style.HasFlag(TuiStyle.Underline)) codes.Add("4");
        if (style.HasFlag(TuiStyle.Dim)) codes.Add("2");
        if (style.HasFlag(TuiStyle.Strike)) codes.Add("9");
        if (style.HasFlag(TuiStyle.Reverse)) codes.Add("7");

        if (codes.Count == 0)
        {
            Console.Write(text);
        }
        else
        {
            Console.Write($"\x1b[{string.Join(';', codes)}m{text}\x1b[0m");
        }
    }

    public void SetCursorPosition(int row, int col) => Console.SetCursorPosition(col, row);
    public void ClearLine() => Console.Write("\x1b[2K\r");
    public void Clear() => Console.Write("\x1b[2J\x1b[H");
    public void HideCursor() => Console.Write("\x1b[?25l");
    public void ShowCursor() => Console.Write("\x1b[?25h");
    public void EnterAlternateScreen() => Console.Write("\x1b[?1049h");
    public void ExitAlternateScreen() => Console.Write("\x1b[?1049l");
    public void Flush() => Console.Out.Flush();
}
