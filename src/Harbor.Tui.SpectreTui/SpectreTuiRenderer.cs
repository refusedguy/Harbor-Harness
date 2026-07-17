using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.Abstractions;
using Harbor.Tui.Abstractions.Renderers;
using Harbor.Tui.Abstractions.State;
using Harbor.Tui.Abstractions.Views;

using Harbor.Tui.SpectreTui.Helpers;
using Microsoft.Extensions.Logging;
using Spectre.Tui;
using Spectre.Tui.App;
namespace Harbor.Tui.SpectreTui;
/// <summary>
///     Full-screen interactive TUI renderer built on the real Spectre.TUI
///     widget framework. A <see cref="ChatScreen" /> owns the application loop via
///     <see cref="Application.RunAsync" /> and renders the shared, renderer-agnostic
///     <see cref="UiState" /> (produced by <see cref="UiReducer" />) as first-class
///     widgets. All agent I/O is delegated to <see cref="TuiEffectHost" /> — the
///     screen itself never references <c>IAgent</c> or <c>Harbor.Core</c>.
/// </summary>
public sealed class SpectreTuiRenderer : BaseTuiRenderer, IInteractiveTuiRenderer
{
    private readonly ILogger<SpectreTuiRenderer> _logger;
    private ChatScreen? _screen;
    private Func<string, Task>? _slashHandler;
    private UiStore? _store;
    private TuiEffectHost? _effects;

    public SpectreTuiRenderer(ILogger<SpectreTuiRenderer> logger) : base(logger)
    {
        _logger = logger;
        Context = new SpectreTuiRenderContext();
    }

    public override ITuiRenderContext Context { get; }

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
        // Single funnel: every agent event goes through the shared pure reducer.
        _store?.Dispatch(@event);
        // Keep the history pinned to the newest line unless the user scrolled up.
        if (_screen is not null && _screen.IsPinnedToBottom)
            _screen.ScrollToBottom();
        return base.RenderAsync(@event, ct);
    }

    public async Task<int> RunInteractiveAsync(IAgent agent, IServiceProvider host, CancellationToken ct = default)
    {
        _store = new UiStore();
        _effects = new TuiEffectHost(agent, _store, _slashHandler, ct);
        _store.BindSession(agent.State.Agent.Model, agent.State.Agent.ProviderId, agent.State.Agent.Name.Value);
        _screen = new ChatScreen(_store, _effects, _logger);

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
        Context.Clear();
        return Task.FromResult(Result.Success());
    }

    /// <summary>
    ///     Suppress placement-driven rendering — the <see cref="ChatScreen" /> owns the display
    ///     and renders via its widget tree. The base class would otherwise write status/history
    ///     lines straight to the console and corrupt the fullscreen layout.
    /// </summary>
    protected override bool ShouldRenderPlacement(TuiViewPlacement placement, AgentEvent @event) => false;

    /// <summary>
    ///     The chat screen - owns the Spectre.TUI application loop and projects
    ///     <see cref="UiState" /> into widgets. It holds no agent state and performs
    ///     no I/O: keystrokes become <see cref="InputMsg" /> transitions and input
    ///     submission becomes a <see cref="TuiEffect" />.
    /// </summary>
    private sealed class ChatScreen : Screen
    {
        private readonly UiStore _store;
        private readonly TuiEffectHost _effects;
        private readonly ILogger _logger;
        private readonly LayoutBuilder _layout;
        private InputModel _input = InputModel.Empty;
        private ApplicationContext? _app;
        private int _lastHistoryHeight;

        public ChatScreen(UiStore store, TuiEffectHost effects, ILogger logger)
        {
            _store = store;
            _effects = effects;
            _logger = logger;
            _layout = new LayoutBuilder();
        }

        /// <summary>Pin the history to the newest line (tail-follow) on new activity.</summary>
        public void ScrollToBottom() => _layout.ScrollToBottom();

        /// <summary>True when the user has not scrolled back into history.</summary>
        public bool IsPinnedToBottom => _layout.ScrollOffset == 0;

        public override void OnEnter(ApplicationContext context)
        {
            _app = context;
            _logger.LogDebug("OnEnter: model={Model} provider={Provider} agent={Agent}",
                _store.State.Model, _store.State.Provider, _store.State.AgentName);
        }

        public override void OnMessage(ApplicationContext context, ApplicationMessage message)
        {
            if (message is not KeyMessage key) return;

            var state = _store.State;
            if (state.IsAgentRunning)
            {
                // While running only abort is accepted.
                if (key.Key == Key.Escape ||
                    (key.Character == 'c' && key.Modifiers.HasFlag(KeyModifier.Ctrl)))
                {
                    _effects.Run(new TuiEffect.AbortAgent());
                    _store.Transition(s => s.AddLine(ChatRole.System, "[yellow]⏹ Aborted.[/]"));
                }

                return;
            }

            if (key.Key == Key.Escape)
            {
                _effects.Run(new TuiEffect.QuitApp());
                _app?.Quit();
                return;
            }

            // History scroll (when not editing input history with Up/Down).
            if (key.Key == Key.PageUp)
            {
                _layout.ScrollBy(+Math.Max(1, _lastHistoryHeight / 2));
                return;
            }
            if (key.Key == Key.PageDown)
            {
                _layout.ScrollBy(-Math.Max(1, _lastHistoryHeight / 2));
                return;
            }

            if (key.Key == Key.Enter)
            {
                Submit();
                return;
            }

            if (key.Key == Key.Backspace)
            {
                _input = InputMsg.Update(_input, new InputMsg.Backspace());
            }
            else if (key.Key == Key.Up)
            {
                _input = InputMsg.Update(_input, new InputMsg.HistoryUp());
            }
            else if (key.Key == Key.Down)
            {
                _input = InputMsg.Update(_input, new InputMsg.HistoryDown());
            }
            else if (key.Key == Key.Tab && _input.Text.StartsWith('/'))
            {
                _input = InputMsg.Update(_input, new InputMsg.Autocomplete(TuiEffectHost.KnownSlashCommands));
            }
            else if (key.Character == 'l' && key.Modifiers.HasFlag(KeyModifier.Ctrl))
            {
                _store.Reset();
            }
            else if (key.Character is >= (char)32 and not (char)127)
            {
                _input = InputMsg.Update(_input, new InputMsg.Char(key.Character.Value));
            }
        }

        private void Submit()
        {
            var (next, submitted) = _input.Consume();
            _input = next;
            if (submitted is null) return;

            _logger.LogInformation("Submitting: {Text}", submitted);
            if (!submitted.StartsWith('/'))
                _store.Transition(s => s.AddLine(ChatRole.User, submitted));
            _effects.Run(TuiEffectHost.ToEffect(submitted));
        }

        public override void Update(FrameInfo frame, IRenderBounds bounds)
        {
            // No per-frame mutation needed; state arrives via events.
        }

        public override void Render(RenderContext context)
        {
            SyncLayout();
            var historyArea = _layout.Layout.GetArea(context, "History");
            _lastHistoryHeight = historyArea.Height > 0 ? historyArea.Height : 0;
            var widgets = _layout.BuildWidgets(_lastHistoryHeight);
            _logger.LogTrace("Render: {WidgetCount} widgets", widgets.Count);
            foreach ((string name, var widget) in widgets)
            {
                var area = _layout.Layout.GetArea(context, name);
                if (area.Width > 0 && area.Height > 0)
                    context.Render(widget, area);
            }
        }

        /// <summary>Project the shared <see cref="UiState" /> + local input into the LayoutBuilder.</summary>
        private void SyncLayout()
        {
            var s = _store.State;
            _layout.Model = s.Model;
            _layout.Provider = s.Provider;
            _layout.Agent = s.AgentName;
            _layout.Status = s.Status;
            _layout.TokensIn = (int)s.Cost.TokensIn;
            _layout.TokensOut = (int)s.Cost.TokensOut;
            _layout.Cost = s.Cost.CostUsd;
            _layout.IsStreaming = s.IsStreaming;
            _layout.StreamBuffer = s.Active.TextBuffer;
            _layout.ThinkBuffer = s.Active.ThinkBuffer;
            _layout.IsReadingInput = !s.IsAgentRunning;
            _layout.SetLines(s.Lines, s.IsStreaming, s.Active);
            _layout.InputText = _input.Text;
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
