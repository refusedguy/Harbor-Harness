using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Tui.Abstractions;
using Harbor.Tui.Abstractions.Renderers;
using Harbor.Tui.Abstractions.Views;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Tui.Plain;
/// <summary>
///     Plain text TUI renderer — no ANSI escape codes, no colors.
///     Use for: piping to other commands, CI logs, accessibility, files.
/// </summary>
public sealed class PlainTuiRenderer : BaseTuiRenderer
{
    private readonly bool _ownsWriter;
    private readonly TextWriter _writer;

    public PlainTuiRenderer(TextWriter? writer = null) : base(NullLogger.Instance)
    {
        _writer = writer ?? Console.Out;
        _ownsWriter = writer is not null;
        Context = new PlainRenderContext(_writer);
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

    public override Task RenderAsync(AgentEvent @event, CancellationToken ct = default)
    {
        var ctx = Context;
        switch (@event)
        {
            case MessageStartEvent:
                ctx.Write("[assistant] ");
                break;

            case MessageUpdateEvent mu:
                RenderLiveToken(mu.LlmEvent, ctx);
                break;

            case MessageEndEvent:
                ctx.WriteLine();
                break;

            // Emitted as live lines (not accumulated in the chat history view) so they
            // appear inline without repainting the whole log.
            case ToolExecutionStartEvent tes:
                ctx.WriteLine();
                ctx.WriteLine($"→ {tes.ToolName} {tes.Args.GetRawText()}");
                break;

            case CompactionCompletedEvent cc:
                ctx.WriteLine($"[compacted: pruned {cc.PrunedMessageCount} msgs, saved ~{cc.TokensSaved} tokens]");
                break;

            case AgentErrorEvent err:
                ctx.WriteLine($"[ERROR] {err.Message}");
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
                ctx.Write(thd.Delta);
                break;
            case ToolCallDeltaEvent tcd:
                ctx.Write(tcd.ArgsDelta);
                break;
        }
    }

    public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
    {
        Context.Write(prompt);
        Context.Flush();
        string? line = Console.ReadLine();
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
        Console.Clear();
        return Task.FromResult(Result.Success());
    }

    public override void Dispose()
    {
        if (_ownsWriter) _writer.Dispose();
        else _writer.Flush();
        base.Dispose();
    }
}

internal sealed class PlainRenderContext : ITuiRenderContext
{
    private readonly TextWriter _writer;

    public PlainRenderContext(TextWriter writer)
    {
        _writer = writer;
    }
    public int Width => 80;
    public int Height => 24;
    public bool SupportsColor => false;

    public void Write(string text) => _writer.Write(text);
    public void WriteLine(string? text = null) => _writer.WriteLine(text ?? string.Empty);
    public void WriteColored(string text, TuiColor foreground, TuiColor? background = null) => _writer.Write(text);
    public void WriteStyled(string text, TuiStyle style) => _writer.Write(text);
    public void SetCursorPosition(int row, int col) { }
    public void ClearLine() { }
    public void Clear() { }
    public void HideCursor() { }
    public void ShowCursor() { }
    public void EnterAlternateScreen() { }
    public void ExitAlternateScreen() { }
    public void Flush() => _writer.Flush();
}
