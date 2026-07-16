using System.Text;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.Abstractions;
using Harbor.Tui.Abstractions.Renderers;
using Microsoft.Extensions.Logging;

namespace Harbor.Tui.Ansi;

/// <summary>
/// ANSI-based TUI renderer with full color/style support.
/// Implements Strategy pattern (GOF) via BaseTuiRenderer.
/// </summary>
public sealed class AnsiTuiRenderer : BaseTuiRenderer
{
    public override ITuiRenderContext Context { get; }

    public AnsiTuiRenderer(ILogger<AnsiTuiRenderer> logger) : base(logger)
    {
        Context = new AnsiRenderContext();
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
        // For ANSI streaming, render events directly for live token streaming
        RenderEventDirectly(@event);
        return base.RenderAsync(@event, ct);
    }

    private void RenderEventDirectly(AgentEvent @event)
    {
        var ctx = Context;
        switch (@event)
        {
            case AgentStartEvent:
                ctx.WriteLine();
                break;

            case MessageStartEvent:
                ctx.WriteColored("[assistant] ", TuiColor.Cyan);
                break;

            case MessageUpdateEvent mu:
                RenderLlmEvent(mu.LlmEvent, ctx);
                break;

            case MessageEndEvent:
                ctx.WriteLine();
                break;

            case ToolExecutionStartEvent tes:
                ctx.WriteLine();
                ctx.WriteColored($"→ {tes.ToolName}", TuiColor.Blue);
                var args = tes.Args.GetRawText();
                if (!string.IsNullOrEmpty(args) && args != "{}")
                {
                    ctx.WriteStyled($" {args}", TuiStyle.Dim);
                }
                ctx.WriteLine();
                break;

            case ToolExecutionEndEvent tee:
                var color = tee.IsError ? TuiColor.Red : TuiColor.Gray;
                var label = tee.IsError ? "✗" : "✓";
                ctx.WriteColored($"  {label} ", color);
                var preview = tee.Result.Output.Length > 200
                    ? tee.Result.Output[..200] + "..."
                    : tee.Result.Output;
                ctx.WriteStyled(preview.ReplaceLineEndings("\n  "), TuiStyle.Dim);
                ctx.WriteLine();
                break;

            case CompactionStartedEvent:
                ctx.WriteLine();
                ctx.WriteStyled("[compacting context...]", TuiStyle.Dim);
                ctx.WriteLine();
                break;

            case CompactionCompletedEvent cc:
                ctx.WriteStyled($"[compacted: pruned {cc.PrunedMessageCount} msgs, saved ~{cc.TokensSaved} tokens in {cc.Duration.TotalSeconds:F1}s]", TuiStyle.Dim);
                ctx.WriteLine();
                break;

            case AgentErrorEvent err:
                ctx.WriteLine();
                ctx.WriteColored($"[error] {err.Message}", TuiColor.Red);
                ctx.WriteLine();
                break;

            case AgentEndEvent:
                ctx.WriteLine();
                break;
        }
    }

    private static void RenderLlmEvent(LlmEvent evt, ITuiRenderContext ctx)
    {
        switch (evt)
        {
            case TextDeltaEvent td:
                ctx.Write(td.Delta);
                break;

            case ThinkingDeltaEvent thd:
                ctx.WriteStyled(thd.Delta, TuiStyle.Dim | TuiStyle.Italic);
                break;

            case ToolCallStartEvent tcs:
                ctx.WriteLine();
                ctx.WriteColored($"→ {tcs.ToolName}", TuiColor.Blue);
                break;

            case ToolCallDeltaEvent tcd:
                ctx.WriteStyled(tcd.ArgsDelta, TuiStyle.Dim);
                break;

            case StepFinishEvent sf when sf.Usage is not null:
                ctx.WriteLine();
                ctx.WriteStyled($"  [tokens: {sf.Usage.InputTokens} in / {sf.Usage.OutputTokens} out]", TuiStyle.Dim);
                ctx.WriteLine();
                break;
        }
    }

    public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
    {
        Context.ShowCursor();
        Context.WriteColored(prompt, TuiColor.Green);
        var line = Console.ReadLine();
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
/// ANSI-based render context — emits escape codes to Console.
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
        var fgCode = $"\x1b[38;2;{foreground.R};{foreground.G};{foreground.B}m";
        var bgCode = background.HasValue
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
