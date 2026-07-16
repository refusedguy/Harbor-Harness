using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.Abstractions;
using Harbor.Tui.Abstractions.Renderers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RazorConsole.Core;

namespace Harbor.Tui.RazorConsole;

/// <summary>
///     Full-screen interactive TUI renderer built on the RazorConsole.Core
///     Blazor-for-the-console framework. The <see cref="ChatTui" /> root component
///     owns the render/input loop (driven by <c>host.RunAsync</c>), while agent
///     activity flows into the shared <see cref="ChatBridge" /> singleton via
///     <see cref="RenderAsync" /> so the component re-renders on state changes.
/// </summary>
public sealed class RazorConsoleRenderer : BaseTuiRenderer, IInteractiveTuiRenderer
{
    private readonly ILogger<RazorConsoleRenderer> _logger;
    private Func<string, Task>? _slashHandler;
    private ChatBridge? _bridge;
    private IHost? _host;

    /// <summary>
    ///     Construct a <see cref="RazorConsoleRenderer" /> with the supplied logger.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public RazorConsoleRenderer(ILogger<RazorConsoleRenderer> logger) : base(logger)
    {
        _logger = logger;
        Context = new RazorConsoleRenderContext();
    }

    /// <inheritdoc />
    public override ITuiRenderContext Context { get; }

    void IInteractiveTuiRenderer.SetSlashHandler(Func<string, Task> handler)
        => _slashHandler = handler;

    /// <inheritdoc />
    public override Task<Result> InitializeAsync(CancellationToken ct = default)
        => base.InitializeAsync(ct);

    /// <inheritdoc />
    public override Task RenderAsync(AgentEvent @event, CancellationToken ct = default)
    {
        _bridge?.ApplyEvent(@event);
        return base.RenderAsync(@event, ct);
    }

    /// <summary>
    ///     Run the interactive chat loop. Builds a generic host configured with the
    ///     RazorConsole application (<see cref="ChatTui" />) and the shared
    ///     <see cref="ChatBridge" /> singleton, then runs it to completion.
    /// </summary>
    /// <param name="agent">The initialized agent to drive.</param>
    /// <param name="host">The service provider hosting the app.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Exit code (0 = ok).</returns>
    public async Task<int> RunInteractiveAsync(IAgent agent, IServiceProvider host, CancellationToken ct = default)
    {
        _bridge = new ChatBridge(agent, _slashHandler, _logger);

        _host = Host.CreateDefaultBuilder()
            .UseRazorConsole<ChatTui>()
            .ConfigureServices(services =>
            {
                services.AddSingleton(_bridge);
                services.Configure<ConsoleAppOptions>(ConfigureConsoleAppOptions);
            })
            .Build();

        try
        {
            await _host.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation(ex, "RazorConsole host stopped");
        }

        return 0;
    }

    /// <inheritdoc />
    public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
    {
        Context.WriteColored(prompt, TuiColor.Green);
        var line = Console.ReadLine();
        return Task.FromResult(Result.Success(line ?? string.Empty));
    }

    /// <inheritdoc />
    public override Task<Result> WriteAsync(string text, CancellationToken ct = default)
    {
        Context.Write(text);
        return Task.FromResult(Result.Success());
    }

    /// <inheritdoc />
    public override Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default)
    {
        Context.WriteLine(text);
        return Task.FromResult(Result.Success());
    }

    /// <inheritdoc />
    public override Task<Result> ClearAsync(CancellationToken ct = default)
    {
        Context.Clear();
        return Task.FromResult(Result.Success());
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _host?.Dispose();
        _host = null;
        base.Dispose();
    }

    private static void ConfigureConsoleAppOptions(ConsoleAppOptions o)
    {
        o.AutoClearConsole = false;
        o.EnableTerminalResizing = true;
    }
}

/// <summary>
///     A single line in the chat transcript.
/// </summary>
/// <param name="IsUser">Whether the line was authored by the user.</param>
/// <param name="Text">The plain text to display (the component applies color by role).</param>
public sealed record ChatLine(bool IsUser, string Text);

/// <summary>
///     Shared observable state bridging the agent, the slash-command handler and the
///     <see cref="ChatTui" /> root component. Registered as a DI singleton so the
///     component can inject it, subscribe to <see cref="StateChanged" /> and re-render.
/// </summary>
public sealed class ChatBridge
{
    private readonly ILogger _logger;
    private readonly List<ChatLine> _messages = new();
    private string _streamBuffer = string.Empty;
    private string _thinkBuffer = string.Empty;

    /// <summary>
    ///     Construct a <see cref="ChatBridge" />.
    /// </summary>
    /// <param name="agent">The agent to drive.</param>
    /// <param name="slash">Optional slash-command handler.</param>
    /// <param name="logger">The logger.</param>
    public ChatBridge(IAgent agent, Func<string, Task>? slash, ILogger logger)
    {
        Agent = agent;
        Slash = slash;
        _logger = logger;
        Model = agent.State.Agent.Model;
        Provider = agent.State.Agent.ProviderId;
        AgentName = agent.State.Agent.Name.Value;
        Status = "idle";
    }

    /// <summary>The agent driven by this bridge.</summary>
    public IAgent Agent { get; }

    /// <summary>Optional slash-command handler.</summary>
    public Func<string, Task>? Slash { get; }

    /// <summary>The current chat transcript (append-only).</summary>
    public IReadOnlyList<ChatLine> Messages => _messages;

    /// <summary>Whether a prompt is currently in flight.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Human-readable status (idle / running / compacting / error).</summary>
    public string Status { get; private set; }

    /// <summary>The active model id.</summary>
    public string Model { get; }

    /// <summary>The active provider id.</summary>
    public string Provider { get; }

    /// <summary>The active agent name.</summary>
    public string AgentName { get; }

    /// <summary>Whether the user has requested to quit.</summary>
    public bool QuitRequested { get; private set; }

    /// <summary>Raised whenever observable state changes; the component re-renders.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    ///     Handle a submitted input line: quit commands, slash commands, or a prompt.
    /// </summary>
    /// <param name="text">The raw input text.</param>
    public async Task SendAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var trimmed = text.Trim();

        if (trimmed is "exit" or "quit" or "q" or ":q")
        {
            QuitRequested = true;
            RaiseChanged();
            Agent.AbortSource.Cancel();
            return;
        }

        if (trimmed.StartsWith('/') && Slash is not null)
        {
            try
            {
                await Slash(trimmed).ConfigureAwait(false);
                PushLine(false, trimmed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Slash command failed: {Command}", trimmed);
                Status = "error";
                PushLine(false, ex.Message);
            }

            return;
        }

        PushLine(true, trimmed);
        IsRunning = true;
        Status = "running";
        RaiseChanged();

        try
        {
            var result = await Agent.PromptAsync(trimmed, Agent.AbortSource.Token).ConfigureAwait(false);
            if (result.IsFailure)
            {
                Status = "error";
                PushLine(false, result.Error);
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation(ex, "Prompt canceled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Prompt failed");
            Status = "error";
            PushLine(false, ex.Message);
        }
        finally
        {
            IsRunning = false;
            if (Status == "running")
            {
                Status = "idle";
            }

            RaiseChanged();
        }
    }

    /// <summary>
    ///     Append a line to the transcript and notify subscribers.
    /// </summary>
    /// <param name="isUser">Whether the line is a user line.</param>
    /// <param name="text">The line text.</param>
    public void PushLine(bool isUser, string text)
    {
        _messages.Add(new ChatLine(isUser, text));
        RaiseChanged();
    }

    /// <summary>
    ///     Mirror agent activity into observable state (buffers streaming text, pushes
    ///     completed messages, tool lines and status transitions).
    /// </summary>
    /// <param name="event">The agent event.</param>
    public void ApplyEvent(AgentEvent @event)
    {
        switch (@event)
        {
            case AgentStartEvent ase:
                Status = "running";
                if (_messages.Count == 0)
                {
                    foreach (var m in ase.Messages)
                    {
                        if (m is UserMessage u)
                        {
                            _messages.Add(new ChatLine(true, u.Content));
                        }
                    }
                }

                break;
            case MessageStartEvent:
                Status = "running";
                _streamBuffer = string.Empty;
                _thinkBuffer = string.Empty;
                break;
            case MessageUpdateEvent mu:
                switch (mu.LlmEvent)
                {
                    case TextDeltaEvent td:
                        _streamBuffer += td.Delta;
                        break;
                    case ThinkingDeltaEvent thd:
                        _thinkBuffer += thd.Delta;
                        break;
                    case ToolCallStartEvent tcs:
                        _messages.Add(new ChatLine(false, $"-> {tcs.ToolName}"));
                        break;
                }

                break;
            case MessageEndEvent:
                if (!string.IsNullOrWhiteSpace(_thinkBuffer))
                {
                    _messages.Add(new ChatLine(false, _thinkBuffer.Trim()));
                }

                if (!string.IsNullOrWhiteSpace(_streamBuffer))
                {
                    _messages.Add(new ChatLine(false, _streamBuffer.Trim()));
                }

                _streamBuffer = string.Empty;
                _thinkBuffer = string.Empty;
                break;
            case ToolExecutionStartEvent tes:
                var args = tes.Args.GetRawText();
                _messages.Add(new ChatLine(false, string.IsNullOrEmpty(args) || args == "{}"
                    ? $"-> {tes.ToolName}"
                    : $"-> {tes.ToolName} {args}"));
                break;
            case ToolExecutionEndEvent tee:
                var label = tee.IsError ? "x" : "v";
                var output = tee.Result.Output;
                var preview = output.Length > 600 ? output[..600] + "..." : output;
                _messages.Add(new ChatLine(false, $"{label} {preview.Trim()}"));
                break;
            case CompactionStartedEvent:
                Status = "compacting";
                break;
            case CompactionCompletedEvent cc:
                Status = "running";
                _messages.Add(new ChatLine(false,
                    $"compacted: pruned {cc.PrunedMessageCount} msgs, saved ~{cc.TokensSaved} tokens"));
                break;
            case AgentErrorEvent err:
                Status = "error";
                _messages.Add(new ChatLine(false, err.Message));
                break;
            case AgentEndEvent:
                Status = "idle";
                break;
        }

        RaiseChanged();
    }

    private void RaiseChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
///     Render context shim over the console for non-interactive helpers.
/// </summary>
internal sealed class RazorConsoleRenderContext : ITuiRenderContext
{
    /// <inheritdoc />
    public int Width => Console.WindowWidth;

    /// <inheritdoc />
    public int Height => Console.WindowHeight;

    /// <inheritdoc />
    public bool SupportsColor => true;

    /// <inheritdoc />
    public void Write(string text) => Console.Write(text);

    /// <inheritdoc />
    public void WriteLine(string? text = null) => Console.WriteLine(text ?? string.Empty);

    /// <inheritdoc />
    public void WriteColored(string text, TuiColor foreground, TuiColor? background = null)
        => Console.Write($"\x1b[38;2;{foreground.R};{foreground.G};{foreground.B}m{text}\x1b[0m");

    /// <inheritdoc />
    public void WriteStyled(string text, TuiStyle style) => Console.Write(text);

    /// <inheritdoc />
    public void SetCursorPosition(int row, int col) => Console.SetCursorPosition(col, row);

    /// <inheritdoc />
    public void ClearLine() => Console.Write("\x1b[2K\r");

    /// <inheritdoc />
    public void Clear() => Console.Write("\x1b[2J\x1b[H");

    /// <inheritdoc />
    public void HideCursor() => Console.Write("\x1b[?25l");

    /// <inheritdoc />
    public void ShowCursor() => Console.Write("\x1b[?25h");

    /// <inheritdoc />
    public void EnterAlternateScreen() => Console.Write("\x1b[?1049h");

    /// <inheritdoc />
    public void ExitAlternateScreen() => Console.Write("\x1b[?1049l");

    /// <inheritdoc />
    public void Flush() => Console.Out.Flush();
}
