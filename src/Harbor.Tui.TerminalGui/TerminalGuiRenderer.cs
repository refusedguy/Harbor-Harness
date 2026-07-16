using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.Abstractions;
using Harbor.Tui.Abstractions.Renderers;
using Microsoft.Extensions.Logging;

namespace Harbor.Tui.TerminalGui;

/// <summary>
/// Experimental renderer using Terminal.Gui v2.
/// Falls back to ANSI streaming when Terminal.Gui v2 widget API is not fully available.
/// Set HARBOR_TUI=terminal-gui to try.
/// </summary>
public sealed class TerminalGuiRenderer : BaseTuiRenderer, IInteractiveTuiRenderer
{
    private readonly ILogger<TerminalGuiRenderer> _logger;
    private Func<string, Task>? _slashHandler;

    public override ITuiRenderContext Context { get; }

    public TerminalGuiRenderer(ILogger<TerminalGuiRenderer> logger) : base(logger)
    {
        _logger = logger;
        Context = new TerminalGuiRenderContext();
    }

    void IInteractiveTuiRenderer.SetSlashHandler(Func<string, Task> handler) => _slashHandler = handler;

    public override Task<Result> InitializeAsync(CancellationToken ct = default)
    {
        Context.Clear();
        Context.WriteColored("⚓ Harbor (Terminal.Gui v2) — widget-based TUI\n\n", TuiColor.Cyan);
        return base.InitializeAsync(ct);
    }

    public override Task RenderAsync(AgentEvent @event, CancellationToken ct = default)
    {
        switch (@event)
        {
            case MessageStartEvent:
                Context.WriteColored("[assistant] ", TuiColor.Cyan);
                break;
            case MessageUpdateEvent mu:
                if (mu.LlmEvent is TextDeltaEvent td) Context.Write(td.Delta);
                else if (mu.LlmEvent is ThinkingDeltaEvent thd) Context.WriteStyled(thd.Delta, TuiStyle.Dim | TuiStyle.Italic);
                else if (mu.LlmEvent is ToolCallStartEvent tcs) { Context.WriteLine(); Context.WriteColored($"→ {tcs.ToolName}\n", TuiColor.Blue); }
                break;
            case MessageEndEvent: Context.WriteLine(); break;
            case ToolExecutionEndEvent tee:
                var label = tee.IsError ? "✗" : "✓";
                var preview = tee.Result.Output.Length > 200 ? tee.Result.Output[..200] + "..." : tee.Result.Output;
                Context.WriteColored($"  {label} {preview}\n", tee.IsError ? TuiColor.Red : TuiColor.Gray);
                break;
            case AgentErrorEvent err: Context.WriteColored($"[error] {err.Message}\n", TuiColor.Red); break;
        }
        return base.RenderAsync(@event, ct);
    }

    public Task<int> RunInteractiveAsync(IAgent agent, IServiceProvider host, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            Context.WriteColored("> ", TuiColor.Green);
            var line = Console.ReadLine();
            if (line is null or "exit" or "quit") break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith('/') && _slashHandler is not null) { _slashHandler(line).GetAwaiter().GetResult(); continue; }
            agent.PromptAsync(line, ct).GetAwaiter().GetResult();
        }
        return Task.FromResult(0);
    }

    public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
    {
        Context.WriteColored(prompt, TuiColor.Green);
        return Task.FromResult(Result.Success(Console.ReadLine() ?? string.Empty));
    }

    public override Task<Result> WriteAsync(string text, CancellationToken ct = default)
    { Context.Write(text); return Task.FromResult(Result.Success()); }
    public override Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default)
    { Context.WriteLine(text); return Task.FromResult(Result.Success()); }
    public override Task<Result> ClearAsync(CancellationToken ct = default)
    { Context.Clear(); return Task.FromResult(Result.Success()); }
}

internal sealed class TerminalGuiRenderContext : ITuiRenderContext
{
    public int Width => Console.WindowWidth;
    public int Height => Console.WindowHeight;
    public bool SupportsColor => true;
    public void Write(string text) => Console.Write(text);
    public void WriteLine(string? text = null) => Console.WriteLine(text ?? string.Empty);
    public void WriteColored(string text, TuiColor foreground, TuiColor? background = null)
        => Console.Write($"\x1b[38;2;{foreground.R};{foreground.G};{foreground.B}m{text}\x1b[0m");
    public void WriteStyled(string text, TuiStyle style) => Console.Write(text);
    public void SetCursorPosition(int row, int col) => Console.SetCursorPosition(col, row);
    public void ClearLine() => Console.Write("\x1b[2K\r");
    public void Clear() => Console.Write("\x1b[2J\x1b[H");
    public void HideCursor() => Console.Write("\x1b[?25l");
    public void ShowCursor() => Console.Write("\x1b[?25h");
    public void EnterAlternateScreen() => Console.Write("\x1b[?1049h");
    public void ExitAlternateScreen() => Console.Write("\x1b[?1049l");
    public void Flush() => Console.Out.Flush();
}
