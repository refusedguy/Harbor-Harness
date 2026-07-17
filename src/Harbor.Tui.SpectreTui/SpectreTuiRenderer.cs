using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.Abstractions;
using Harbor.Tui.Abstractions.Renderers;
using Harbor.Tui.Abstractions.Views;
using Harbor.Tui.SpectreTui.Components;
using Harbor.Tui.SpectreTui.Helpers;
using Microsoft.Extensions.Logging;
using Spectre.Tui;
using Spectre.Tui.App;

namespace Harbor.Tui.SpectreTui;

/// <summary>
///     Full-screen interactive TUI renderer built on the real Spectre.TUI
///     widget framework (Spectre.Tui + Spectre.Tui.App). A <see cref="ChatScreen" />
///     owns the application loop via <see cref="Application.RunAsync" /> and
///     renders chat history, a streaming indicator, an input box and a help
///     footer using first-class widgets (ScrollViewWidget, BoxWidget,
///     SpinnerWidget, HelpWidget, Layout).
/// </summary>
public sealed class SpectreTuiRenderer : BaseTuiRenderer, IInteractiveTuiRenderer
{
    private readonly ILogger<SpectreTuiRenderer> _logger;
    private Func<string, Task>? _slashHandler;
    private ChatScreen? _screen;

    public override ITuiRenderContext Context { get; }

    public SpectreTuiRenderer(ILogger<SpectreTuiRenderer> logger) : base(logger)
    {
        _logger = logger;
        Context = new SpectreTuiRenderContext();
    }

    void IInteractiveTuiRenderer.SetSlashHandler(Func<string, Task> handler)
        => _slashHandler = handler;

    public override Task<Result> InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            // Intentionally do NOT write a banner here: Spectre.Tui owns the screen
            // (fullscreen mode) and any raw Console.Write corrupts the layout.
            return base.InitializeAsync(ct);
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Failure(ex.Message));
        }
    }

    public override Task RenderAsync(AgentEvent @event, CancellationToken ct = default)
    {
        _screen?.ApplyEvent(@event);
        return base.RenderAsync(@event, ct);
    }

    /// <summary>
    ///     Suppress placement-driven rendering — the <see cref="ChatScreen" /> owns the display
    ///     and renders via its widget tree. The base class would otherwise write status/history
    ///     lines straight to the console and corrupt the fullscreen layout.
    /// </summary>
    protected override bool ShouldRenderPlacement(TuiViewPlacement placement, AgentEvent @event) => false;

    public async Task<int> RunInteractiveAsync(IAgent agent, IServiceProvider host, CancellationToken ct = default)
    {
        _screen = new ChatScreen(agent, _slashHandler, _logger);

        // Use fullscreen mode so the framework drives an alternate screen buffer that
        // is cleared and diffed every frame. Without this the app runs in inline mode
        // and streamed output simply appends to the terminal (text overlaps).
        var settings = new ApplicationSettings
        {
            Terminal = Terminal.Create(new FullscreenMode())
        };

        await Application.Create(settings).RunAsync(_screen).ConfigureAwait(false);
        return 0;
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

    /// <summary>The chat screen - owns the Spectre.TUI application loop.</summary>
    private sealed class ChatScreen : Screen
    {
        private readonly IAgent _agent;
        private readonly Func<string, Task>? _slash;
        private readonly ILogger _logger;
        private readonly ChatState _chat = new();
        private readonly InputState _input = new();
        private readonly LayoutBuilder _layout;
        private readonly HashSet<string> _slashCommands = new()
        {
            "/help", "/exit", "/setup", "/auth", "/model", "/agent", "/config",
            "/providers", "/sessions", "/tui", "/storage", "/clear"
        };

        private bool _streaming;
        private string _streamBuffer = string.Empty;
        private string _thinkBuffer = string.Empty;
        private decimal _cost;

        public ChatScreen(IAgent agent, Func<string, Task>? slash, ILogger logger)
        {
            _agent = agent;
            _slash = slash;
            _logger = logger;
            _layout = new LayoutBuilder(_chat, _input);
        }

        public void ApplyEvent(AgentEvent @event)
        {
            _logger.LogDebug("ApplyEvent: {EventType}", @event.GetType().Name);
            switch (@event)
            {
                case AgentStartEvent ase:
                    _layout.Status = "running";
                    if (_chat.Count == 0)
                        foreach (var m in ase.Messages)
                            if (m is UserMessage u) _chat.Add("user", u.Content);
                    break;
                case MessageStartEvent:
                    _layout.Status = "running";
                    _streaming = true;
                    _streamBuffer = string.Empty;
                    _thinkBuffer = string.Empty;
                    break;
                case MessageUpdateEvent mu:
                    switch (mu.LlmEvent)
                    {
                        case TextDeltaEvent td: _streamBuffer += td.Delta; break;
                        case ThinkingDeltaEvent thd: _thinkBuffer += thd.Delta; break;
                        case ToolCallStartEvent tcs: _chat.Add("tool", $"→ {tcs.ToolName}"); break;
                        case StepFinishEvent sf when sf.Usage is not null:
                            _layout.TokensIn += sf.Usage.InputTokens;
                            _layout.TokensOut += sf.Usage.OutputTokens;
                            _cost += EstimateCost(sf.Usage.InputTokens, sf.Usage.OutputTokens);
                            _layout.Cost = _cost;
                            break;
                    }
                    break;
                case MessageEndEvent:
                    if (!string.IsNullOrEmpty(_thinkBuffer)) _chat.Add("thinking", _thinkBuffer.Trim());
                    if (!string.IsNullOrEmpty(_streamBuffer)) _chat.Add("assistant", _streamBuffer.Trim());
                    _streamBuffer = string.Empty;
                    _thinkBuffer = string.Empty;
                    _streaming = false;
                    break;
                case ToolExecutionStartEvent tes:
                    var args = tes.Args.GetRawText();
                    _chat.Add("tool", string.IsNullOrEmpty(args) || args == "{}"
                        ? $"→ {tes.ToolName}"
                        : $"→ {tes.ToolName}  [dim]{Escape(args)}[/]");
                    break;
                case ToolExecutionEndEvent tee:
                    var label = tee.IsError ? "[red]✗[/]" : "[green]✓[/]";
                    var preview = tee.Result.Output.Length > 600
                        ? tee.Result.Output[..600] + "..." : tee.Result.Output;
                    _chat.Add("tool-result", $"{label} {Escape(preview.Trim())}");
                    break;
                case CompactionStartedEvent: _layout.Status = "compacting"; break;
                case CompactionCompletedEvent cc:
                    _layout.Status = "running";
                    _chat.Add("system", $"[dim]compacted: pruned {cc.PrunedMessageCount} msgs, saved ~{cc.TokensSaved} tokens[/]");
                    break;
                case AgentErrorEvent err:
                    _layout.Status = "error";
                    _chat.Add("error", err.Message);
                    break;
                case AgentEndEvent: _layout.Status = "idle"; break;
            }

            _layout.IsStreaming = _streaming;
            _layout.StreamBuffer = _streamBuffer;
            _layout.ThinkBuffer = _thinkBuffer;
        }

        private static string Escape(string text)
            => (text ?? string.Empty).Replace("[", "\\[", StringComparison.Ordinal);

        private static decimal EstimateCost(int inTok, int outTok)
            => (decimal)inTok / 1_000_000m * 3m + (decimal)outTok / 1_000_000m * 15m;

        public override void OnEnter(ApplicationContext context)
        {
            _layout.Model = _agent.State.Agent.Model;
            _layout.Provider = _agent.State.Agent.ProviderId;
            _layout.Agent = _agent.State.Agent.Name.Value;
            _logger.LogDebug("OnEnter: model={Model} provider={Provider} agent={Agent}", _layout.Model, _layout.Provider, _layout.Agent);
        }

        public override void OnMessage(ApplicationContext context, ApplicationMessage message)
        {
            if (message is not KeyMessage key) return;

            if (_agent.State.IsRunning)
            {
                HandleRunningKey(context, key);
                return;
            }

            if (key.Key == Key.Escape) { context.Quit(); return; }

            if (key.Character is >= (char)32 and not (char)127)
            {
                _input.Append(key.Character.Value);
            }
            else if (key.Key == Key.Enter)
            {
                SubmitCurrent(context);
            }
            else if (key.Key == Key.Backspace)
            {
                _input.Backspace();
            }
            else if (key.Key == Key.Up)
            {
                _input.NavigateUp();
            }
            else if (key.Key == Key.Down)
            {
                _input.NavigateDown();
            }
            else if (key.Key == Key.Tab && _input.Text.StartsWith('/'))
            {
                Autocomplete();
            }
            else if (key.Character == 'l' && key.Modifiers.HasFlag(KeyModifier.Ctrl))
            {
                _chat.Clear();
            }
        }

        private void HandleRunningKey(ApplicationContext context, KeyMessage key)
        {
            if (key.Key == Key.Escape || (key.Character == 'c' && key.Modifiers.HasFlag(KeyModifier.Ctrl)))
            {
                _logger.LogWarning("Agent aborted by user");
                _agent.AbortSource.Cancel();
                _chat.Add("system", "[yellow]⏹ Aborted.[/]");
                _ = Task.Run(async () => await _agent.WaitForIdleAsync(CancellationToken.None).ConfigureAwait(false));
            }
        }

        private void SubmitCurrent(ApplicationContext context)
        {
            var text = _input.Consume();
            _logger.LogInformation("Submitting: {Text}", text);
            if (string.IsNullOrWhiteSpace(text)) return;
            if (text is "exit" or "quit" or ":q") { context.Quit(); return; }

            if (text.StartsWith('/') && _slash is not null)
            {
                _ = Task.Run(async () => await _slash(text).ConfigureAwait(false));
                _chat.Add("system", $"[dim]{Escape(text[1..])}[/]");
                return;
            }

            _chat.Add("user", text);
            _layout.Status = "running";
            _ = Task.Run(async () => await _agent.PromptAsync(text, CancellationToken.None).ConfigureAwait(false));
        }

        private void Autocomplete()
        {
            var current = _input.Text;
            var match = _slashCommands.FirstOrDefault(c => c.StartsWith(current, StringComparison.OrdinalIgnoreCase));
            if (match is null) return;
            _input.Clear();
            foreach (var c in match) _input.Append(c);
            _input.Append(' ');
        }

        public override void Update(FrameInfo frame, IRenderBounds bounds)
        {
            _layout.IsReadingInput = !_agent.State.IsRunning;
        }

        public override void Render(RenderContext context)
        {
            var widgets = _layout.BuildWidgets();
            _logger.LogTrace("Render: {WidgetCount} widgets", widgets.Count);
            foreach (var (name, widget) in widgets)
            {
                var area = _layout.Layout.GetArea(context, name);
                if (area.Width > 0 && area.Height > 0)
                    context.Render(widget, area);
            }
        }
    }
}

/// <summary>Render context shim over the console for non-interactive helpers.</summary>
internal sealed class SpectreTuiRenderContext : ITuiRenderContext
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
