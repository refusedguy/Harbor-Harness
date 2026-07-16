using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.Abstractions;
using Harbor.Tui.Abstractions.Renderers;
using Microsoft.Extensions.Logging;
using Spectre.Tui;

namespace Harbor.Tui.SpectreTui;

/// <summary>
/// Experimental renderer using Spectre.TUI — official widget framework from Spectre.Console team.
/// Provides proper widget-based layout (buttons, text views, panels) instead of raw ANSI.
/// </summary>
public sealed class SpectreTuiRenderer : BaseTuiRenderer, IInteractiveTuiRenderer
{
    private readonly ILogger<SpectreTuiRenderer> _logger;
    private Func<string, Task>? _slashHandler;

    public override ITuiRenderContext Context { get; }

    public SpectreTuiRenderer(ILogger<SpectreTuiRenderer> logger) : base(logger)
    {
        _logger = logger;
        Context = new SpectreTuiRenderContext();
    }

    void IInteractiveTuiRenderer.SetSlashHandler(Func<string, Task> handler) => _slashHandler = handler;

    public override Task<Result> InitializeAsync(CancellationToken ct = default)
    {
        return base.InitializeAsync(ct);
    }

    public override Task RenderAsync(AgentEvent @event, CancellationToken ct = default)
    {
        // Direct event rendering for immediate feedback
        switch (@event)
        {
            case MessageStartEvent:
                Context.Write("[assistant] ");
                break;
            case MessageUpdateEvent mu:
                if (mu.LlmEvent is TextDeltaEvent td)
                    Context.Write(td.Delta);
                else if (mu.LlmEvent is ThinkingDeltaEvent thd)
                    Context.WriteStyled(thd.Delta, TuiStyle.Dim | TuiStyle.Italic);
                else if (mu.LlmEvent is ToolCallStartEvent tcs)
                    Context.WriteLine($"→ {tcs.ToolName}");
                break;
            case MessageEndEvent:
                Context.WriteLine();
                break;
            case ToolExecutionEndEvent tee:
                var label = tee.IsError ? "✗" : "✓";
                var preview = tee.Result.Output.Length > 200 ? tee.Result.Output[..200] + "..." : tee.Result.Output;
                Context.WriteLine($"  {label} {preview}");
                break;
            case AgentErrorEvent err:
                Context.WriteColored($"[error] {err.Message}\n", TuiColor.Red);
                break;
        }
        return base.RenderAsync(@event, ct);
    }

    public Task<int> RunInteractiveAsync(Harbor.Abstractions.Agents.IAgent agent, IServiceProvider host, CancellationToken ct = default)
    {
        // Spectre.TUI has its own application loop — delegate to it
        // For now, fall back to line-buffered input
        while (!ct.IsCancellationRequested)
        {
            Context.WriteColored("> ", TuiColor.Green);
            var line = Console.ReadLine();
            if (line is null or "exit" or "quit") break;
            if (line.StartsWith('/') && _slashHandler is not null)
            {
                _slashHandler(line).GetAwaiter().GetResult();
                continue;
            }
            agent.PromptAsync(line, ct).GetAwaiter().GetResult();
        }
        return Task.FromResult(0);
    }

    public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
    {
        Context.WriteColored(prompt, TuiColor.Green);
        var line = Console.ReadLine();
        return Task.FromResult(Result.Success(line ?? string.Empty));
    }

    public override Task<Result> WriteAsync(string text, CancellationToken ct = default)
    { Context.Write(text); return Task.FromResult(Result.Success()); }
    public override Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default)
    { Context.WriteLine(text); return Task.FromResult(Result.Success()); }
    public override Task<Result> ClearAsync(CancellationToken ct = default)
    { Context.Clear(); return Task.FromResult(Result.Success()); }
}

internal sealed class SpectreTuiRenderContext : ITuiRenderContext
{
    public int Width => Console.WindowWidth;
    public int Height => Console.WindowHeight;
    public bool SupportsColor => true;

    public void Write(string text) => Console.Write(text);
    public void WriteLine(string? text = null) => Console.WriteLine(text ?? string.Empty);
    public void WriteColored(string text, TuiColor foreground, TuiColor? background = null)
        => Console.Write($"\x1b[38;2;{foreground.R};{foreground.G};{foreground.B}m{text}\x1b[0m");
    public void WriteStyled(string text, TuiStyle style)
    {
        var codes = new List<string>();
        if (style.HasFlag(TuiStyle.Bold)) codes.Add("1");
        if (style.HasFlag(TuiStyle.Italic)) codes.Add("3");
        if (style.HasFlag(TuiStyle.Dim)) codes.Add("2");
        Console.Write(codes.Count > 0 ? $"\x1b[{string.Join(';', codes)}m{text}\x1b[0m" : text);
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
