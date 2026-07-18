using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Terminal.Abstractions.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using R3;
using Termina.Hosting;
using Termina.Terminal;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tui.Termina;
/// <summary>
///     A single rendered chat line carrying its role color. The <see cref="ChatBridge" /> emits
///     these so the page can append role-appropriate, color-coded text to the streaming node.
/// </summary>
public sealed record ChatLine(string Text, Color? Color = null, bool NewLineBefore = false);

/// <summary>
///     Full-screen interactive TUI renderer built on Termina 0.15.0 (R3-reactive MVVM).
///     Termina owns the application loop via the Generic Host; the renderer drives the agent
///     by forwarding events through a shared <see cref="ChatBridge" /> singleton that the
///     <see cref="ChatPage" /> subscribes to.
/// </summary>
public sealed class TerminaRenderer : BaseTuiRenderer, IInteractiveTuiRenderer
{
    private readonly TerminaRenderContext _context;
    private readonly ILogger<TerminaRenderer> _logger;
    private ChatBridge? _bridge;
    private Func<string, Task>? _slashHandler;

    public TerminaRenderer(ILogger<TerminaRenderer> logger) : base(logger)
    {
        _logger = logger;
        _context = new TerminaRenderContext();
    }

    public override ITuiRenderContext Context => _context;

    void IInteractiveTuiRenderer.SetSlashHandler(Func<string, Task> handler) => _slashHandler = handler;

    public override Task<Result> InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            // Intentionally do NOT write a banner here: Termina owns the screen and
            // any raw Console.Write during interactive startup corrupts the layout.
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

    public async Task<int> RunInteractiveAsync(IAgent agent, IServiceProvider host, CancellationToken ct = default)
    {
        _bridge = new ChatBridge(_logger);
        _bridge.Agent = agent;
        _bridge.SlashHandler = _slashHandler;

        _logger.LogInformation("Starting Termina host with route /chat");

        var terminaHost = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(_bridge);
                services.AddTermina("/chat", termina => termina.RegisterRoute<ChatPage, ChatViewModel>("/chat"));
            })
            .ConfigureLogging(logging =>
            {
                // Clear all providers to prevent console logging from interfering
                // with Termina's terminal handling.
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Warning);
            })
            .Build();

        await terminaHost.RunAsync(ct).ConfigureAwait(false);
        return 0;
    }

    public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
    {
        _context.WriteColored(prompt, TuiColor.Green);
        string? line = Console.ReadLine();
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

    /// <summary>
    ///     Suppress placement-driven rendering — Termina's <see cref="ChatPage" /> owns the
    ///     display and subscribes to the shared <see cref="ChatBridge" />. The base class would
    ///     otherwise write status/history lines straight to the console and corrupt the screen.
    /// </summary>
    protected override bool ShouldRenderPlacement(TuiViewPlacement placement, AgentEvent @event) => false;
}

/// <summary>
///     Shared singleton bridging the agent thread and the Termina page. The renderer pushes
///     agent events onto <see cref="OutputStream" />; the <see cref="ChatPage" /> subscribes and
///     appends to the streaming text node. Also exposes the submit entry point that runs the agent.
/// </summary>
public sealed class ChatBridge : IDisposable
{
    private readonly ILogger _logger;
    private readonly Subject<ChatLine> _outputStream = new();
    private bool _awaitingAssistantLabel = true;

    public ChatBridge(ILogger logger)
    {
        _logger = logger;
    }

    public IAgent? Agent { get; set; }

    public Func<string, Task>? SlashHandler { get; set; }

    public Observable<ChatLine> OutputStream => _outputStream;

    public void Dispose() => _outputStream.Dispose();

    public void Push(AgentEvent @event)
    {
        foreach (var line in FormatLines(@event))
        {
            _logger.LogDebug("Push event: {EventType} -> {LineLength} chars", @event.GetType().Name, line.Text.Length);
            _outputStream.OnNext(line);
        }
    }

    public void PushLine(string line, Color? color = null)
    {
        _logger.LogDebug("PushLine: {Line}", line);
        _outputStream.OnNext(new ChatLine(line, color));
    }

    public void Submit(string prompt)
    {
        if (Agent is null)
        {
            _logger.LogDebug("Submit called but Agent is null — ignoring");
            return;
        }

        if (prompt.StartsWith('/') && SlashHandler is not null)
        {
            _logger.LogDebug("Executing slash command: {Command}", prompt);
            _ = Task.Run(async () =>
            {
                try
                {
                    await SlashHandler(prompt).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Slash command failed: {Command}", prompt);
                }
            });
            return;
        }

        _logger.LogDebug("Submitting prompt to agent ({Length} chars)", prompt.Length);
        _ = Task.Run(async () =>
        {
            try
            {
                await Agent.PromptAsync(prompt, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent prompt failed");
                PushLine($"error: {ex.Message}\n", Color.Red);
            }
        });
    }

    private IEnumerable<ChatLine> FormatLines(AgentEvent @event)
    {
        switch (@event)
        {
            case AgentStartEvent or TurnStartEvent:
                _awaitingAssistantLabel = true;
                yield break;
            case MessageUpdateEvent mu:
                switch (mu.LlmEvent)
                {
                    case TextDeltaEvent td:
                        if (_awaitingAssistantLabel)
                        {
                            _awaitingAssistantLabel = false;
                            yield return new ChatLine("Harbor: ", Color.Cyan, true);
                        }
                        yield return new ChatLine(td.Delta, Color.Cyan);
                        break;
                    case ThinkingDeltaEvent thd:
                        yield return new ChatLine($"🧠 {thd.Delta}", Color.Magenta);
                        break;
                    case ToolCallStartEvent:
                        _awaitingAssistantLabel = true;
                        yield return new ChatLine("\n→ calling tool…\n", Color.Yellow);
                        break;
                }
                yield break;
            case MessageEndEvent:
                _awaitingAssistantLabel = true;
                yield return new ChatLine("\n");
                yield break;
            case ToolExecutionStartEvent tes:
                string args = tes.Args.GetRawText();
                yield return new ChatLine(
                    string.IsNullOrEmpty(args) || args == "{}"
                        ? $"→ {tes.ToolName}\n"
                        : $"→ {tes.ToolName}  {args}\n",
                    Color.Yellow);
                yield break;
            case ToolExecutionEndEvent tee:
                string label = tee.IsError ? "✗" : "✓";
                string preview = tee.Result.Output.Length > 600
                    ? tee.Result.Output[..600] + "..." : tee.Result.Output;
                yield return new ChatLine($"{label} {preview.Trim()}\n", tee.IsError ? Color.Red : Color.Gray);
                yield break;
            case CompactionCompletedEvent cc:
                yield return new ChatLine(
                    $"[compacted: pruned {cc.PrunedMessageCount} msgs, saved ~{cc.TokensSaved} tokens]\n",
                    Color.DarkGray);
                yield break;
            case AgentErrorEvent err:
                _awaitingAssistantLabel = true;
                yield return new ChatLine($"error: {err.Message}\n", Color.Red, true);
                yield break;
        }
    }
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
