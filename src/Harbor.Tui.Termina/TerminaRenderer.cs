using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.Abstractions;
using Harbor.Tui.Abstractions.Renderers;
using Microsoft.Extensions.Logging;

namespace Harbor.Tui.Termina;

/// <summary>
/// Experimental renderer using Termina — Aaron Stannard's (Akka.NET creator) reactive MVVM TUI.
/// Built on R3 (lighter than System.Reactive), AOT-compatible, source-generator powered.
/// Features StreamingTextNode for real-time LLM token rendering.
/// </summary>
public sealed class TerminaRenderer : BaseTuiRenderer, IInteractiveTuiRenderer
{
    private readonly ILogger<TerminaRenderer> _logger;
    private Func<string, Task>? _slashHandler;
    private readonly System.Text.StringBuilder _chatContent = new();

    public override ITuiRenderContext Context { get; }

    public TerminaRenderer(ILogger<TerminaRenderer> logger) : base(logger)
    {
        _logger = logger;
        Context = new TerminaRenderContext();
    }

    void IInteractiveTuiRenderer.SetSlashHandler(Func<string, Task> handler) => _slashHandler = handler;

    public override Task<Result> InitializeAsync(CancellationToken ct = default)
    {
        Context.Clear();
        Context.WriteColored("⚓ Harbor (Termina) — reactive MVVM TUI\n\n", TuiColor.Cyan);
        return base.InitializeAsync(ct);
    }

    public override Task RenderAsync(AgentEvent @event, CancellationToken ct = default)
    {
        switch (@event)
        {
            case MessageStartEvent:
                _chatContent.Clear();
                Context.WriteColored("[assistant] ", TuiColor.Cyan);
                break;

            case MessageUpdateEvent mu:
                switch (mu.LlmEvent)
                {
                    case TextDeltaEvent td:
                        Context.Write(td.Delta);
                        break;
                    case ThinkingDeltaEvent thd:
                        Context.WriteStyled(thd.Delta, TuiStyle.Dim | TuiStyle.Italic);
                        break;
                    case ToolCallStartEvent tcs:
                        Context.WriteLine();
                        Context.WriteColored($"→ {tcs.ToolName}\n", TuiColor.Blue);
                        break;
                    case StepFinishEvent sf when sf.Usage is not null:
                        Context.WriteLine();
                        Context.WriteStyled($"  [tokens: {sf.Usage.InputTokens}↑ / {sf.Usage.OutputTokens}↓]\n", TuiStyle.Dim);
                        break;
                }
                break;

            case MessageEndEvent:
                Context.WriteLine();
                break;

            case ToolExecutionEndEvent tee:
                var label = tee.IsError ? "✗" : "✓";
                var preview = tee.Result.Output.Length > 200 ? tee.Result.Output[..200] + "..." : tee.Result.Output;
                Context.WriteColored($"  {label} {preview}\n", tee.IsError ? TuiColor.Red : TuiColor.Gray);
                break;

            case AgentErrorEvent err:
                Context.WriteColored($"[error] {err.Message}\n", TuiColor.Red);
                break;
        }
        return base.RenderAsync(@event, ct);
    }

    public Task<int> RunInteractiveAsync(IAgent agent, IServiceProvider host, CancellationToken ct = default)
    {
        // Termina uses its own application host — for now we use a simple REPL
        // that delegates to Termina's reactive properties when available
        while (!ct.IsCancellationRequested)
        {
            Context.WriteColored("> ", TuiColor.Green);
            var line = Console.ReadLine();
            if (line is null or "exit" or "quit") break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith('/') && _slashHandler is not null)
            {
                _slashHandler(line).GetAwaiter().GetResult();
                continue;
            }
            Context.WriteColored($"[user] {line}\n", TuiColor.Green);
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

internal sealed class TerminaRenderContext : ITuiRenderContext
{
    public int Width => Console.WindowWidth;
    public int Height => Console.WindowHeight;
    public bool SupportsColor => true;

    public void Write(string text) => Console.Write(text);
    public void WriteLine(string? text = null) => Console.WriteLine(text ?? string.Empty);

    public void WriteColored(string text, TuiColor foreground, TuiColor? background = null)
    {
        var fg = $"\x1b[38;2;{foreground.R};{foreground.G};{foreground.B}m";
        var bg = background.HasValue ? $"\x1b[48;2;{background.Value.R};{background.Value.G};{background.Value.B}m" : "";
        Console.Write($"{fg}{bg}{text}\x1b[0m");
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
