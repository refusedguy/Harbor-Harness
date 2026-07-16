using System.Text;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.Abstractions;
using Harbor.Tui.Abstractions.Renderers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using R3;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tui.Termina;
/// <summary>
///     Full-screen interactive TUI renderer built on Termina 0.15.0 (R3-reactive MVVM).
///     Termina owns the application loop via the Generic Host; the renderer drives the agent
///     by forwarding events through a shared <see cref="ChatBridge" /> singleton that the
///     <see cref="ChatPage" /> subscribes to.
/// </summary>
public sealed class TerminaRenderer : BaseTuiRenderer, IInteractiveTuiRenderer
{
    private readonly ILogger<TerminaRenderer> _logger;
    private readonly TerminaRenderContext _context;
    private ChatBridge? _bridge;
    private Func<string, Task>? _slashHandler;

    public override ITuiRenderContext Context => _context;

    public TerminaRenderer(ILogger<TerminaRenderer> logger) : base(logger)
    {
        _logger = logger;
        _context = new TerminaRenderContext();
    }

    void IInteractiveTuiRenderer.SetSlashHandler(Func<string, Task> handler)
    {
        _slashHandler = handler;
    }

    public override Task<Result> InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            _context.WriteColored("⚓ Harbor (Termina) - interactive TUI\n\n", TuiColor.Cyan);
            return base.InitializeAsync(ct);
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Failure(ex.Message));
        }
    }

    public override Task RenderAsync(AgentEvent @event, CancellationToken ct = default)
    {
        _bridge?.Push(@event);
        return base.RenderAsync(@event, ct);
    }

    public Task<int> RunInteractiveAsync(IAgent agent, IServiceProvider host, CancellationToken ct = default)
    {
        _bridge = host.GetRequiredService<ChatBridge>();
        _bridge.Agent = agent;
        _bridge.SlashHandler = _slashHandler;

        return Task.FromResult(0);
    }

    public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
    {
        _context.WriteColored(prompt, TuiColor.Green);
        var line = Console.ReadLine();
        return Task.FromResult(Result.Success(line ?? string.Empty));
    }

    public override Task<Result> WriteAsync(string text, CancellationToken ct = default)
    {
        _context.Write(text);
        return Task.FromResult(Result.Success());
    }

    public override Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default)
    {
        _context.WriteLine(text);
        return Task.FromResult(Result.Success());
    }

    public override Task<Result> ClearAsync(CancellationToken ct = default)
    {
        _context.Clear();
        return Task.FromResult(Result.Success());
    }

    public override void Dispose()
    {
        _bridge?.Dispose();
        base.Dispose();
    }
}

/// <summary>
///     Shared singleton bridging the agent thread and the Termina page. The renderer pushes
///     agent events onto <see cref="OutputStream" />; the <see cref="ChatPage" /> subscribes and
///     appends to the streaming text node. Also exposes the submit entry point that runs the agent.
/// </summary>
public sealed class ChatBridge : IDisposable
{
    private readonly Subject<string> _outputStream = new();

    public IAgent? Agent { get; set; }

    public Func<string, Task>? SlashHandler { get; set; }

    public Observable<string> OutputStream => _outputStream;

    public void Push(AgentEvent @event) => _outputStream.OnNext(FormatLine(@event));

    public void PushLine(string line) => _outputStream.OnNext(line);

    public void Submit(string prompt)
    {
        if (Agent is null) return;

        if (prompt.StartsWith('/') && SlashHandler is not null)
        {
            SlashHandler(prompt).GetAwaiter().GetResult();
            return;
        }

        PushLine($"You: {prompt}");
        Agent.PromptAsync(prompt, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static string FormatLine(AgentEvent @event)
    {
        switch (@event)
        {
            case AgentStartEvent ase:
                var seed = new StringBuilder();
                foreach (var m in ase.Messages)
                    if (m is UserMessage u)
                        seed.Append($"You: {u.Content}\n");
                return seed.ToString();
            case MessageUpdateEvent mu:
                return mu.LlmEvent switch
                {
                    TextDeltaEvent td => td.Delta,
                    ThinkingDeltaEvent thd => $"🧠 {thd.Delta}",
                    ToolCallStartEvent tcs => $"\n→ {tcs.ToolName}\n",
                    _ => string.Empty
                };
            case MessageEndEvent:
                return "\n";
            case ToolExecutionStartEvent tes:
                var args = tes.Args.GetRawText();
                return string.IsNullOrEmpty(args) || args == "{}"
                    ? $"→ {tes.ToolName}\n"
                    : $"→ {tes.ToolName}  {args}\n";
            case ToolExecutionEndEvent tee:
                var label = tee.IsError ? "✗" : "✓";
                var preview = tee.Result.Output.Length > 600
                    ? tee.Result.Output[..600] + "..." : tee.Result.Output;
                return $"{label} {preview.Trim()}\n";
            case CompactionCompletedEvent cc:
                return $"[compacted: pruned {cc.PrunedMessageCount} msgs, saved ~{cc.TokensSaved} tokens]\n";
            case AgentErrorEvent err:
                return $"error: {err.Message}\n";
            default:
                return string.Empty;
        }
    }

    public void Dispose() => _outputStream.Dispose();
}

/// <summary>Render context shim over the console for non-interactive helpers.</summary>
internal sealed class TerminaRenderContext : ITuiRenderContext
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
