using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Tui.Abstractions;
using Harbor.Tui.Abstractions.Renderers;
using Harbor.Tui.Abstractions.State;
using Harbor.Tui.Abstractions.Views;
using Harbor.Tui.SpectreTui.View;
using Microsoft.Extensions.Logging;
using Spectre.Tui;
using Spectre.Tui.App;

namespace Harbor.Tui.SpectreTui;
/// <summary>
///     Full-screen interactive TUI renderer built on Spectre.TUI.
///     <see cref="ChatScreen" /> projects shared <see cref="UiState" /> into widgets.
///     Agent I/O goes through <see cref="TuiEffectHost" /> only.
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
            // Spectre owns the alternate screen — no raw banner writes.
            return base.InitializeAsync(ct);
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Failure(ex.Message));
        }
    }

    public override Task RenderAsync(AgentEvent @event, CancellationToken ct = default)
    {
        try
        {
            if (_store is not null)
            {
                _store.Dispatch(new UiMsg.Agent(@event));
                _logger.LogTrace(
                    "RenderAsync: {EventType} lines={Lines} running={Running}",
                    @event.GetType().Name,
                    _store.State.Lines.Length,
                    _store.State.IsAgentRunning);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RenderAsync failed for {EventType}", @event.GetType().Name);
        }

        return base.RenderAsync(@event, ct);
    }

    public async Task<int> RunInteractiveAsync(
        IAgent agent,
        IServiceProvider host,
        CancellationToken ct = default)
    {
        _store = new UiStore();
        _effects = new TuiEffectHost(agent, _store, _slashHandler, ct);
        _store.BindSession(
            agent.State.Agent.Model,
            agent.State.Agent.ProviderId,
            agent.State.Agent.Name.Value);
        _screen = new ChatScreen(_store, _effects, _logger);

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

    protected override bool ShouldRenderPlacement(TuiViewPlacement placement, AgentEvent @event)
        => false;

    /// <summary>
    ///     Thin TEA view: keys → <see cref="UiMsg"/>, effects → host,
    ///     <see cref="UiState"/> → <see cref="LayoutBuilder"/>.
    ///     Local scroll is display-rows-from-bottom (0 = live tail).
    /// </summary>
    private sealed class ChatScreen : Screen
    {
        private readonly UiStore _store;
        private readonly TuiEffectHost _effects;
        private readonly ILogger _logger;
        private readonly ChatViewProjector _layout;
        private readonly ChatKeyMap _keyMap = new();
        private ApplicationContext? _app;

        // Display-rows lifted from the bottom. 0 = pinned to newest (live).
        // Kept local so we never Dispatch geometry every frame.
        private int _scroll;
        private int _viewport;
        private bool _wasRunning;

        public ChatScreen(UiStore store, TuiEffectHost effects, ILogger logger)
        {
            _store = store;
            _effects = effects;
            _logger = logger;
            _layout = new ChatViewProjector();
        }

        public override void OnEnter(ApplicationContext context)
        {
            _app = context;
            var s = _store.State;
            _logger.LogDebug(
                "OnEnter: model={Model} provider={Provider} agent={Agent}",
                s.Model, s.Provider, s.AgentName);
        }

        public override void OnMessage(ApplicationContext context, ApplicationMessage message)
        {
            try
            {
                if (message is KeyMessage key)
                    OnKeyMessage(key);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OnMessage failed");
            }
        }

        private void OnKeyMessage(KeyMessage key)
        {
            // Multi-line paste: '\n' as character must not submit.
            // Real Enter is Key.Enter (often with '\r' or no char).
            if (key.Key == Key.Enter && key.Character is '\n')
                return;

            var uiKey = ToUiKey(key);
            var action = _keyMap.Resolve(uiKey);

            // Framework reports these as characters, not key codes.
            if (key.Character == 'l' && key.Modifiers.HasFlag(KeyModifier.Ctrl))
                action = ChatAction.Clear;
            else if (key.Character == 'c' && key.Modifiers.HasFlag(KeyModifier.Ctrl))
                action = ChatAction.Abort;

            if (action == ChatAction.None)
                return;

            // Scroll stays local (always allowed, including while streaming).
            if (HandleLocalScroll(action))
                return;

            var effect = _store.Dispatch(new UiMsg.KeyInput(action, uiKey));
            if (effect is not TuiEffect.None)
                _effects.Run(effect);

            if (effect is TuiEffect.QuitApp)
                _app?.Quit();
        }

        /// <summary>
        ///     <c>_scroll</c> = display-rows up from bottom; 0 = live tail.
        ///     Clamp happens in <see cref="LayoutBuilder.BuildWidgets"/> /
        ///     <see cref="LayoutBuilder.EffectiveScroll"/>.
        /// </summary>
        private bool HandleLocalScroll(ChatAction action)
        {
            int page = Math.Max(1, _viewport - 2);
            switch (action)
            {
                case ChatAction.ScrollUpLine:
                    _scroll++;
                    break;
                case ChatAction.ScrollDownLine:
                    _scroll = Math.Max(0, _scroll - 1);
                    break;
                case ChatAction.ScrollUpPage:
                    _scroll += page;
                    break;
                case ChatAction.ScrollDownPage:
                    _scroll = Math.Max(0, _scroll - page);
                    break;
                case ChatAction.ScrollTop:
                    _scroll = int.MaxValue; // LayoutBuilder clamps to MaxScroll
                    break;
                case ChatAction.ScrollBottom:
                    _scroll = 0;
                    break;
                default:
                    return false;
            }

            return true;
        }

        public override void Update(FrameInfo frame, IRenderBounds bounds)
        {
            // Geometry is measured in Render via layout areas.
        }

        public override void Render(RenderContext context)
        {
            try
            {
                RenderCore(context);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Render failed; scroll={Scroll} viewport={Viewport} lines={Lines}",
                    _scroll, _viewport, _store.State.Lines.Length);
            }
        }

        private void RenderCore(RenderContext context)
        {
            var historyArea = _layout.Layout.GetArea(context, "History");
            _viewport = historyArea.Height > 0 ? historyArea.Height : 0;

            var state = _store.State;

            // New agent run → pin to live tail so streaming is visible.
            if (state.IsAgentRunning && !_wasRunning)
                _scroll = 0;
            _wasRunning = state.IsAgentRunning;

            // Soft pre-clamp using last frame's TotalLines (may be 0 on first frame).
            // Final clamp is LayoutBuilder.EffectiveScroll after expand+stream.
            if (_layout.TotalLines > 0)
            {
                int maxPrev = Math.Max(0, _layout.TotalLines - _viewport);
                if (_scroll != int.MaxValue)
                    _scroll = Math.Clamp(_scroll, 0, maxPrev);
            }

            SyncLayout(state);
            _layout.ScrollOffset = _scroll;

            var widgets = _layout.BuildWidgets(_viewport);

            // Authoritative clamp in display-row units (includes pinned stream height).
            _scroll = _layout.EffectiveScroll;
            _viewport = _layout.ViewportLines;

            // Footer after measure so scroll % matches this frame.
            _layout.FooterText = BuildFooter();

            _logger.LogTrace(
                "Render: scroll={Scroll}/{Max} total={Total} viewport={Viewport} lines={Lines}",
                _scroll, _layout.MaxScroll, _layout.TotalLines, _viewport, state.Lines.Length);

            foreach (var (name, widget) in widgets)
            {
                // Rebuild footer widget if we updated FooterText after BuildWidgets.
                // Cheapest approach: if footer text changed post-build, only the Footer
                // entry is stale — re-render path below uses the dict from build.
                // So rebuild footer entry only:
                _ = name;
            }

            // Footer text was updated after BuildWidgets → rebuild footer widget only.
            // (Avoid rebuilding the whole tree just for the % label.)
            var footerWidget = ParagraphFromFooter(_layout.FooterText);

            foreach (var (name, widget) in widgets)
            {
                var area = _layout.Layout.GetArea(context, name);
                if (area.Width <= 0 || area.Height <= 0)
                    continue;

                if (name == "Footer")
                    context.Render(footerWidget, area);
                else
                    context.Render(widget, area);
            }
        }

        private void SyncLayout(UiState s)
        {
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
            // Do NOT assign TotalLines / ViewportLines / SourceCount —
            // LayoutBuilder owns those (private set) during BuildHistory.
            // ScrollOffset is set in RenderCore right before BuildWidgets.
        }

        private string BuildFooter()
        {
            string Label(ChatAction a) => _keyMap.Get(a).Label;
            var s = _store.State;
            string mode = s.Focus == FocusMode.Input ? "[green]INPUT[/]" : "[aqua]CHAT[/]";

            int max = _layout.MaxScroll;
            string scroll = max > 0
                ? $"scroll {(_scroll * 100 / max)}%"
                : "scroll 0%";

            // Esc is quit (see keymap); show it honestly.
            return $"[grey]esc[/] {Label(ChatAction.Quit)}  " +
                   $"[grey]F2[/] {Label(ChatAction.ToggleFocus)}  {mode}  " +
                   $"[grey]↑/↓[/] {Label(ChatAction.ScrollUpLine)}  " +
                   $"[grey]PgUp/PgDn[/] {Label(ChatAction.ScrollUpPage)}  " +
                   $"[grey]Home/End[/] {Label(ChatAction.ScrollTop)}  " +
                   $"[grey]Alt+↑/↓[/] {Label(ChatAction.InputHistoryPrev)}  {scroll}";
        }

        private static IWidget ParagraphFromFooter(string markup)
        {
            // Same shape LayoutBuilder uses for footer.
            return Paragraph.FromMarkup(
                string.IsNullOrEmpty(markup) ? " " : markup).Centered();
        }

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
