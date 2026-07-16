using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.Abstractions;
using Harbor.Tui.Abstractions.Renderers;
using Microsoft.Extensions.Logging;

namespace Harbor.Tui.RazorConsole;

/// <summary>
/// Experimental renderer using RazorConsole — define TUI layouts with Razor templates.
/// Allows mixing C# logic with HTML-like markup for terminal output.
/// </summary>
public sealed class RazorConsoleRenderer : BaseTuiRenderer, IInteractiveTuiRenderer
{
    private readonly ILogger<RazorConsoleRenderer> _logger;
    private Func<string, Task>? _slashHandler;
    private readonly List<string> _chatLines = new();
    private string _streamingText = string.Empty;
    private int _tokensIn, _tokensOut;
    private string _status = "idle";
    private decimal _cost;

    public override ITuiRenderContext Context { get; }

    public RazorConsoleRenderer(ILogger<RazorConsoleRenderer> logger) : base(logger)
    {
        _logger = logger;
        Context = new RazorConsoleRenderContext();
    }

    void IInteractiveTuiRenderer.SetSlashHandler(Func<string, Task> handler) => _slashHandler = handler;

    public override Task<Result> InitializeAsync(CancellationToken ct = default)
    {
        Context.Clear();
        RenderHeader();
        return base.InitializeAsync(ct);
    }

    private void RenderHeader()
    {
        Context.WriteColored("⚓ Harbor (RazorConsole) — template-based TUI\n\n", TuiColor.Cyan);
        Context.WriteStyled("─", TuiStyle.Dim);
        Context.WriteLine();
    }

    public override Task RenderAsync(AgentEvent @event, CancellationToken ct = default)
    {
        switch (@event)
        {
            case AgentStartEvent:
                _status = "running";
                break;

            case MessageStartEvent:
                _streamingText = "";
                Context.WriteColored("[assistant] ", TuiColor.Cyan);
                break;

            case MessageUpdateEvent mu:
                switch (mu.LlmEvent)
                {
                    case TextDeltaEvent td:
                        _streamingText += td.Delta;
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
                        _tokensIn += sf.Usage.InputTokens; _cost += (decimal)sf.Usage.InputTokens / 1_000_000m * 3m + (decimal)sf.Usage.OutputTokens / 1_000_000m * 15m;
                        _tokensOut += sf.Usage.OutputTokens;
                        break;
                }
                break;

            case MessageEndEvent:
                if (!string.IsNullOrEmpty(_streamingText))
                    _chatLines.Add(_streamingText);
                _streamingText = "";
                _status = "idle";
                Context.WriteLine();
                break;

            case ToolExecutionEndEvent tee:
                var label = tee.IsError ? "✗" : "✓";
                var preview = tee.Result.Output.Length > 300 ? tee.Result.Output[..300] + "..." : tee.Result.Output;
                Context.WriteColored($"  {label} {preview}\n", tee.IsError ? TuiColor.Red : TuiColor.Gray);
                break;

            case AgentErrorEvent err:
                _status = "error";
                Context.WriteColored($"[error] {err.Message}\n", TuiColor.Red);
                break;

            case AgentEndEvent:
                _status = "idle";
                break;
        }
        return base.RenderAsync(@event, ct);
    }

    public Task<int> RunInteractiveAsync(IAgent agent, IServiceProvider host, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            // Status line
            Context.ClearLine();
            Context.WriteColored($"[{_status}] ", _status == "error" ? TuiColor.Red : _status == "running" ? TuiColor.Cyan : TuiColor.Gray);
            Context.WriteColored($"${_cost:F4} ({_tokensIn}↑/{_tokensOut}↓) ", TuiColor.Green);
            Context.WriteColored("> ", TuiColor.Green);

            var line = Console.ReadLine();
            if (line is null or "exit" or "quit") break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith('/') && _slashHandler is not null)
            {
                _slashHandler(line).GetAwaiter().GetResult();
                continue;
            }

            _chatLines.Add(line);
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

internal sealed class RazorConsoleRenderContext : ITuiRenderContext
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
        if (style.HasFlag(TuiStyle.Dim)) codes.Add("2");
        if (style.HasFlag(TuiStyle.Underline)) codes.Add("4");
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
