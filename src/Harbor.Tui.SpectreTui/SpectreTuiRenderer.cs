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
        // Scroll "pin to newest" is handled implicitly — the reducer leaves
        // ScrollOffset untouched for agent events, so an offset of 0 always tails.
        if (_store is not null)
            _store.Dispatch(new UiMsg.Agent(@event));
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
    ///     The chat screen - owns the Spectre.TUI application loop and projects the
    ///     shared, renderer-agnostic <see cref="UiState" /> into widgets. It holds no
    ///     state and performs no I/O: every keystroke becomes a <see cref="UiMsg" />
    ///     dispatched through the single <see cref="UiReducer.Update" />, and any
    ///     returned <see cref="TuiEffect" /> is run by the host. No reducer logic,
    ///     no <c>IAgent</c> reference, no direct mutation.
    /// </summary>
    private sealed class ChatScreen : Screen
    {
        private readonly UiStore _store;
        private readonly TuiEffectHost _effects;
        private readonly ILogger _logger;
        private readonly LayoutBuilder _layout;
        private readonly ChatKeyMap _keyMap = new();
        private ApplicationContext? _app;

        // Scroll + measured geometry are kept LOCAL to the screen. Pushing them into
        // UiState every frame (via Dispatch) created a render→dispatch→render loop
        // that stalled the UI on long transcripts. The model stays the source of truth
        // for content; the screen owns pixel-level scroll position.
        private int _scroll;          // rows lifted from the tail; 0 = bottom (live)
        private int _viewport;        // measured history height (rows)
        private bool _wasRunning;     // track agent run transitions to reset scroll

        public ChatScreen(UiStore store, TuiEffectHost effects, ILogger logger)
        {
            _store = store;
            _effects = effects;
            _logger = logger;
            _layout = new LayoutBuilder();
        }

        public override void OnEnter(ApplicationContext context)
        {
            _app = context;
            var s = _store.State;
            _logger.LogDebug("OnEnter: model={Model} provider={Provider} agent={Agent}",
                s.Model, s.Provider, s.AgentName);
        }

        public override void OnMessage(ApplicationContext context, ApplicationMessage message)
        {
            if (message is not KeyMessage key) return;

            // A newline delivered as a character (multi-line paste) must not be
            // treated as the Enter key — otherwise pasting text with line breaks
            // auto-submits the moment the first newline arrives. A real Enter press
            // arrives as Key.Enter with a '\r' (or no character), so only that submits.
            if (key.Key == Key.Enter && key.Character is '\n')
                return;

            var uiKey = ToUiKey(key);
            var action = _keyMap.Resolve(uiKey);

            // Ctrl+L (clear) and Ctrl+C (abort) are reported as characters by the
            // framework; special-case them into the same message funnel.
            if (key.Character == 'l' && key.Modifiers.HasFlag(KeyModifier.Ctrl))
                action = ChatAction.Clear;
            else if (key.Character == 'c' && key.Modifiers.HasFlag(KeyModifier.Ctrl))
                action = ChatAction.Abort;

            if (action == ChatAction.None) return;

            // Scroll is handled locally (no store round-trip) and is always allowed,
            // including while the agent streams — watching the output scroll is core UX.
            if (HandleLocalScroll(action))
                return;

            // The only side-effect the screen is allowed: run the effect the pure
            // update produced. All state transitions happen inside UiReducer.Update.
            var effect = _store.Dispatch(new UiMsg.KeyInput(action, uiKey));
            if (effect is not TuiEffect.None)
                _effects.Run(effect);

            // QuitApp is also a hard screen exit (the host only flips ShouldQuit).
            if (effect is TuiEffect.QuitApp)
                _app?.Quit();
        }

        /// <summary>Apply a scroll action to the local scroll position. Returns true if handled.</summary>
        private bool HandleLocalScroll(ChatAction action)
        {
            int page = Math.Max(1, _viewport - 2);
            switch (action)
            {
                case ChatAction.ScrollUpLine:   _scroll++; break;
                case ChatAction.ScrollDownLine: _scroll = Math.Max(0, _scroll - 1); break;
                case ChatAction.ScrollUpPage:   _scroll += page; break;
                case ChatAction.ScrollDownPage: _scroll = Math.Max(0, _scroll - page); break;
                case ChatAction.ScrollTop:      _scroll = int.MaxValue; break;
                case ChatAction.ScrollBottom:   _scroll = 0; break;
                default: return false;
            }
            return true;
        }

        public override void Update(FrameInfo frame, IRenderBounds bounds)
        {
            // No per-frame mutation; state arrives via messages + events.
        }

        public override void Render(RenderContext context)
        {
            var historyArea = _layout.Layout.GetArea(context, "History");
            _viewport = historyArea.Height > 0 ? historyArea.Height : 0;

            // Reset scroll to the live tail whenever a new agent run starts, so the
            // user is not left staring at a frozen old position while new output streams.
            var state = _store.State;
            if (state.IsAgentRunning && !_wasRunning)
                _scroll = 0;
            _wasRunning = state.IsAgentRunning;

            SyncLayout();
            var widgets = _layout.BuildWidgets(_viewport);

            // Clamp the local scroll to the now-measured content height so repeated
            // PageUp/Home can never push the offset past the top of the transcript.
            int maxScroll = Math.Max(0, _layout.TotalLines - _viewport);
            if (_scroll > maxScroll) _scroll = maxScroll;

            _logger.LogTrace("Render: {WidgetCount} widgets", widgets.Count);
            foreach ((string name, var widget) in widgets)
            {
                var area = _layout.Layout.GetArea(context, name);
                if (area.Width > 0 && area.Height > 0)
                    context.Render(widget, area);
            }
        }

        /// <summary>Project the shared <see cref="UiState" /> into the LayoutBuilder.</summary>
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
            _layout.InputText = s.Input.Text;
            _layout.Focus = s.Focus;
            // Scroll + geometry are local to the screen (see fields); we only feed the
            // measured viewport so the layout can clamp + compute the percentage.
            _layout.ScrollOffset = _scroll;
            _layout.ViewportLines = _viewport;
            _layout.FooterText = BuildFooter();
        }

        /// <summary>
        ///     Assemble the footer from the single keymap source of truth, so the
        ///     help text can never drift from the actual bindings.
        /// </summary>
        private string BuildFooter()
        {
            string Label(ChatAction a) => _keyMap.Get(a).Label;
            var s = _store.State;
            string mode = s.Focus == FocusMode.Input ? "[green]INPUT[/]" : "[aqua]CHAT[/]";
            int max = Math.Max(0, _layout.TotalLines - _viewport);
            string scroll = max > 0
                ? $"scroll {(_scroll * 100 / max)}%"
                : "scroll 0%";
            return $"[grey]q[/] {Label(ChatAction.Quit)}  " +
                   $"[grey]F2[/] {Label(ChatAction.ToggleFocus)}  {mode}  " +
                   $"[grey]↑/↓/wheel[/] {Label(ChatAction.ScrollUpLine)}  " +
                   $"[grey]PgUp/PgDn[/] {Label(ChatAction.ScrollUpPage)}  " +
                   $"[grey]Home/End[/] {Label(ChatAction.ScrollTop)}  " +
                   $"[grey]Alt+↑/↓[/] {Label(ChatAction.InputHistoryPrev)}  {scroll}";
        }
        /// <summary>Map a Spectre.Tui key press onto the framework-neutral <see cref="UiKey" />.</summary>
        private static UiKey ToUiKey(KeyMessage key)
        {
            var mods = KeyModifierSet.None;
            if (key.Modifiers.HasFlag(KeyModifier.Shift)) mods |= KeyModifierSet.Shift;
            if (key.Modifiers.HasFlag(KeyModifier.Ctrl)) mods |= KeyModifierSet.Ctrl;
            if (key.Modifiers.HasFlag(KeyModifier.Alt)) mods |= KeyModifierSet.Alt;

            if (key.Character is >= (char)32 and not (char)127)
                return UiKey.ForChar(key.Character.Value, mods);

            var code = key.Key switch
            {
                Key.Up => UiKeyCode.Up,
                Key.Down => UiKeyCode.Down,
                Key.Left => UiKeyCode.Left,
                Key.Right => UiKeyCode.Right,
                Key.PageUp => UiKeyCode.PageUp,
                Key.PageDown => UiKeyCode.PageDown,
                Key.Home => UiKeyCode.Home,
                Key.End => UiKeyCode.End,
                Key.Enter => UiKeyCode.Enter,
                Key.Escape => UiKeyCode.Escape,
                Key.Backspace => UiKeyCode.Backspace,
                Key.Tab => UiKeyCode.Tab,
                Key.F1 => UiKeyCode.F1,
                Key.F2 => UiKeyCode.F2,
                Key.F3 => UiKeyCode.F3,
                Key.F4 => UiKeyCode.F4,
                _ => UiKeyCode.None
            };
            return new UiKey(code, mods);
        }
    }
}

