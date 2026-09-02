using System.Collections.Immutable;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Terminal.Abstractions.Views;
using Harbor.Tui.SpectreTui.Panels;
using Harbor.Tui.SpectreTui.Panels.Builtin;
using Harbor.Tui.SpectreTui.View;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging;
using Spectre.Tui;
using Spectre.Tui.App;
namespace Harbor.Tui.SpectreTui;
/// <summary>
///     Full-screen interactive TUI renderer built on Spectre.TUI.
///     <see cref="ChatScreen" /> projects shared <see cref="UiState" /> into widgets.
///     Agent I/O goes through <see cref="TuiEffectHost" /> only.
/// </summary>
/// <remarks>
///     <para>
///         <b>TEA compliance (§FP-005):</b> all interactive state lives in
///         <see cref="UiState" /> and is mutated only by <see cref="UiReducer" /> via
///         <see cref="UiStore.Dispatch(UiMsg)" />. The renderer's <see cref="ChatScreen" />
///         is a pure view: it reads state, measures geometry, and dispatches
///         measurement messages (<see cref="UiMsg.Viewport" />,
///         <see cref="UiMsg.HistoryMeasured" />, <see cref="UiMsg.ScrollClamp" />,
///         <see cref="UiMsg.ScrollResetToTail" />). It never mutates state directly.
///     </para>
/// </remarks>
public sealed class SpectreTuiRenderer : BaseTuiRenderer, IInteractiveTuiRenderer
{
    private readonly ILogger<SpectreTuiRenderer> _logger;
    private TuiEffectHost? _effects;
    private ChatScreen? _screen;
    private Func<string, Task>? _slashHandler;
    private UiStore? _store;

    public SpectreTuiRenderer(ILogger<SpectreTuiRenderer> logger, PanelRegistry? panels = null) : base(logger)
    {
        _logger = logger;
        // Use the host-supplied registry (so plugin-contributed panels land here) or
        // construct a fresh one for tests / non-DI callers.
        Panels = panels ?? new PanelRegistry();
        Context = new SpectreTuiRenderContext();
    }

    /// <summary>
    ///     Panel registry shared with the screen and any loaded panel plugins.
    ///     Populated during host startup (builtins registered in
    ///     <see cref="RunInteractiveAsync" />, plugins via <c>ITuiPanelPlugin.RegisterPanels</c>).
    /// </summary>
    /// <remarks>
    ///     <b>Registration-only:</b> the registry holds <see cref="IPanelProvider" />
    ///     instances and nothing else. Panel <i>state</i> (visibility / focus / size)
    ///     lives in <see cref="UiState.PanelStates" /> / <see cref="UiState.FocusedPanelId" />
    ///     / <see cref="UiState.PanelSizes" /> and is mutated only by
    ///     <see cref="UiReducer" />. See <see cref="PanelRegistryView" /> for the
    ///     read-only snapshot used during render.
    /// </remarks>
    public PanelRegistry Panels { get; }

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

        // Register builtin panels if the user hasn't suppressed them
        // (env var HARBOR_TUI_NO_BUILTIN_PANELS=1 → opt-out for tests).
        if (!"1".Equals(Environment.GetEnvironmentVariable("HARBOR_TUI_NO_BUILTIN_PANELS"), StringComparison.OrdinalIgnoreCase))
        {
            Panels.Register(new HelpPanel());
            Panels.Register(new TodoListPanel());
            Panels.Register(new DiffPreviewPanel());
            Panels.Register(new FileTreePanel());
            Panels.Register(new TokenBreakdownPanel());
            Panels.Register(new DiagnosticsPanel());
            // LogsPanel: surfaces live ILogger output inside the TUI. Only
            // useful when the host attached DiagnosticsPanelLoggerProvider
            // (which happens when an interactive TUI is active — see
            // HostBuilder.ConfigureLogging). Registering it unconditionally
            // is harmless: when no IDiagnosticsPanel is in DI, the panel
            // shows a "not registered" placeholder.
            Panels.Register(new LogsPanel());
        }

        // Seed registered panel ids + default Hidden states + default sizes into
        // UiState. After this the reducer is the single source of truth for all
        // panel state; the registry only holds the provider list.
        SeedPanelRegistryIntoState();

        _screen = new ChatScreen(_store, _effects, _logger, Panels, host, new DefaultUiProjector());

        var settings = new ApplicationSettings
        {
            Terminal = Spectre.Tui.Terminal.Create(new FullscreenMode())
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
    ///     Seed the registered panel ids + default Hidden states + default sizes into
    ///     <see cref="UiState" />. Call this whenever panels are registered or
    ///     unregistered at runtime. After seeding, the reducer is the single source of
    ///     truth — there is no runtime state mirror in <see cref="PanelRegistry" />
    ///     (TEA compliance, §FP-005 / §FP-007).
    /// </summary>
    public void SeedPanelRegistryIntoState()
    {
        if (_store is null) return;
        var idsBuilder = ImmutableArray.CreateBuilder<string>(Panels.All.Count);
        var statesBuilder = ImmutableDictionary.CreateBuilder<string, TuiPanelState>(StringComparer.Ordinal);
        var sizesBuilder = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        var current = _store.State;
        foreach (var p in Panels.All)
        {
            idsBuilder.Add(p.Id);
            // Preserve any already-known state (in case panels were re-registered at runtime).
            statesBuilder.Add(p.Id, current.PanelStates.TryGetValue(p.Id, out var s)
                ? s
                : TuiPanelState.Hidden);
            sizesBuilder.Add(p.Id, current.PanelSizes.TryGetValue(p.Id, out int sz)
                ? sz
                : p.DefaultSize);
        }
        // Dispatch via UiMsg (TEA: no Transition escape hatch from renderers).
        _store.Dispatch(new UiMsg.SeedPanels(
            idsBuilder.MoveToImmutable(),
            statesBuilder.ToImmutable(),
            sizesBuilder.ToImmutable()));
    }

    protected override bool ShouldRenderPlacement(TuiViewPlacement placement, AgentEvent @event)
        => false;

    /// <summary>
    ///     Pure TEA view: keys → <see cref="UiMsg" />, effects → host,
    ///     <see cref="UiState" /> → <see cref="LayoutBuilder" />. State lives ONLY in
    ///     <see cref="UiStore" /> — there are no local mutable scroll / viewport /
    ///     was-running fields. Scroll is rows-from-bottom (0 = live tail).
    /// </summary>
    private sealed class ChatScreen : Screen
    {

        private readonly TuiEffectHost _effects;
        private readonly ChatKeyMap _keyMap = new();
        private readonly ChatViewProjector _layout;
        private readonly SpectreUiViewport _viewport;
        private readonly ILogger _logger;
        private readonly PanelViewProjector _panels;
        private readonly PanelLayoutShell _panelShell;
        private readonly SpectreTuiRenderer _parent;
        private readonly PanelRegistry _registry;
        private readonly IServiceProvider _services;
        private readonly UiStore _store;
        private readonly IUiProjector _projector;
        private ApplicationContext? _app;

        public ChatScreen(UiStore store, TuiEffectHost effects, ILogger logger,
            PanelRegistry registry, IServiceProvider services, IUiProjector projector,
            SpectreTuiRenderer? parent = null)
        {
            _store = store;
            _effects = effects;
            _logger = logger;
            _registry = registry;
            _services = services;
            _projector = projector;
            _layout = new ChatViewProjector();
            _viewport = new SpectreUiViewport(_layout);
            _panels = new PanelViewProjector(_layout, registry);
            _panelShell = new PanelLayoutShell(registry);
            _parent = parent!;
        }

        public ChatScreen(UiStore store, TuiEffectHost effects, ILogger logger,
            PanelRegistry registry, IServiceProvider services)
            : this(store, effects, logger, registry, services, new DefaultUiProjector(), null)
        {
            // Backwards-compatible ctor for tests that don't pass a parent.
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
            // '?' → toggle help panel.
            else if (key.Character == '?' && !key.Modifiers.HasFlag(KeyModifier.Ctrl))
                action = ChatAction.HelpPanel;

            // Handle panel-specific actions before falling through to the reducer.
            if (HandlePanelAction(action, uiKey))
                return;

            // If a panel currently owns focus, route the key to it first.
            // The panel may consume (return true) or fall through to the host.
            var s = _store.State;
            if (s.FocusedPanelId is { } focusedId && _registry.Get(focusedId) is { } focusedPanel)
            {
                // Esc / 'q' while a panel is focused → close panel (return to chat).
                if (action is ChatAction.ClosePanel
                    || uiKey.Code == UiKeyCode.Escape
                    || uiKey.Code == UiKeyCode.Char && uiKey.Character == 'q'
                                                    && !uiKey.Mods.HasFlag(KeyModifierSet.Ctrl))
                {
                    _store.Dispatch(new UiMsg.FocusPanel(null));
                    return;
                }

                var ctx = new PanelContext(s, 80, 24, _services);
                try
                {
                    if (focusedPanel.OnKey(uiKey, ctx))
                        return; // consumed
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Panel {Id} OnKey threw", focusedId);
                }
            }

            if (action == ChatAction.None)
                return;

            // TEA: every action — scroll, focus, edit, submit, abort — flows through
            // the single UiReducer.Update. No local scroll handling (§FP-005 fix).
            var effect = _store.Dispatch(new UiMsg.KeyInput(action, uiKey));
            if (effect is not TuiEffect.None)
                _effects.Run(effect);

            if (effect is TuiEffect.QuitApp)
                _app?.Quit();
        }

        /// <summary>
        ///     Handle panel-specific actions: <see cref="ChatAction.TogglePanelSlot" />,
        ///     <see cref="ChatAction.CyclePanelFocus" />,
        ///     <see cref="ChatAction.ResizePanelGrow" /> /
        ///     <see cref="ChatAction.ResizePanelShrink" />,
        ///     <see cref="ChatAction.HelpPanel" />. Returns <see langword="true" /> if
        ///     the action was consumed (host should NOT fall through to the reducer).
        /// </summary>
        private bool HandlePanelAction(ChatAction action, UiKey key)
        {
            switch (action)
            {
                case ChatAction.TogglePanelSlot:
                {
                    // Alt+1..Alt+9 — toggle the Nth registered panel.
                    if (key.Code != UiKeyCode.Char || key.Character is not ({ } c and >= '1' and <= '9'))
                        return false;
                    int slot = c - '1';
                    var providers = _registry.All;
                    if (slot >= providers.Count)
                        return true; // consume even if out of range
                    string id = providers[slot].Id;
                    _store.Dispatch(new UiMsg.TogglePanel(id));
                    return true;
                }

                case ChatAction.CyclePanelFocus:
                    _store.Dispatch(new UiMsg.CyclePanelFocus());
                    return true;

                case ChatAction.ResizePanelGrow:
                case ChatAction.ResizePanelShrink:
                {
                    var s = _store.State;
                    if (s.FocusedPanelId is not { } id)
                        return false;
                    int delta = action == ChatAction.ResizePanelGrow ? 1 : -1;
                    _store.Dispatch(new UiMsg.ResizePanel(id, delta));
                    return true;
                }

                case ChatAction.HelpPanel:
                {
                    var help = _registry.Get("help");
                    if (help is null) return false;
                    _store.Dispatch(new UiMsg.TogglePanel("help"));
                    return true;
                }

                case ChatAction.ToggleLogsPanel:
                {
                    // F12 — toggle the live ILogger output panel. Falls through
                    // (return false) when no "logs" panel is registered (e.g.
                    // tests with HARBOR_TUI_NO_BUILTIN_PANELS=1) so the keystroke
                    // doesn't get swallowed.
                    if (_registry.Get("logs") is null)
                        return false;
                    _store.Dispatch(new UiMsg.TogglePanel("logs"));
                    return true;
                }
            }
            return false;
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
                    _store.State.ScrollOffset, _store.State.ViewportLines, _store.State.Lines.Length);
            }
        }

        /// <summary>
        ///     Pure TEA render: read state, measure geometry, dispatch measurement msgs
        ///     (<see cref="UiMsg.Viewport" />, <see cref="UiMsg.HistoryMeasured" />,
        ///     <see cref="UiMsg.ScrollResetToTail" />, <see cref="UiMsg.ScrollClamp" />),
        ///     build widgets, render. Never mutates state directly.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Dispatching messages from render is an acceptable TEA pattern (called
        ///         "subscription" in Elm) — what's forbidden is mutating state directly.
        ///         All state changes go through the reducer.
        ///     </para>
        /// </remarks>
        private void RenderCore(RenderContext context)
        {
            // Rebuild the layout tree if the visible-set / size / streaming flag changed.
            var state = _store.State;
            _panelShell.Ensure(state, state.IsStreaming);

            // ── 1. Measure viewport (history area height) and report it to the reducer.
            var historyArea = _panelShell.Layout.GetArea(context, "History");
            int viewport = historyArea.Height > 0 ? historyArea.Height : 0;
            if (state.ViewportLines != viewport)
            {
                _store.Dispatch(new UiMsg.Viewport(viewport));
                state = _store.State;
            }

            // ── 2. Rising-edge: agent just started a new run → pin scroll to live tail.
            //    The reducer already did this on AgentStartEvent; this is a belt-and-
            //    braces dispatch for cases where IsAgentRunning was flipped via the
            //    effect host's Transition (e.g. PromptAgent effect) instead of an
            //    AgentStartEvent.
            if (state.IsAgentRunning && !state.WasRunning)
            {
                _store.Dispatch(new UiMsg.ScrollResetToTail());
                state = _store.State;
            }

            // ── 3. Project UiState → UiScreenModel → apply to Spectre widgets.
            var screen = _projector.Project(state);
            _viewport.Apply(screen);
            _layout.ScrollOffset = state.ScrollOffset;

            // ── 4. Build widgets (this measures TotalLines / MaxScroll / EffectiveScroll).
            var widgets = _panels.BuildWidgets(viewport, state);

            // ── 5. Report measured TotalLines to the reducer.
            if (_layout.TotalLines != state.TotalLines)
            {
                _store.Dispatch(new UiMsg.HistoryMeasured(_layout.TotalLines));
                state = _store.State;
            }

            // ── 6. Clamp scroll to MaxScroll (the renderer is the only one that knows
            //    the post-layout MaxScroll, which depends on wrapped rows + pinned stream).
            if (state.ScrollOffset > _layout.MaxScroll)
            {
                _store.Dispatch(new UiMsg.ScrollClamp(_layout.MaxScroll));
                state = _store.State;
                _layout.ScrollOffset = state.ScrollOffset;
            }

            // Footer text was set by the viewport in Apply(screen).

            _logger.LogTrace(
                "Render: scroll={Scroll}/{Max} total={Total} viewport={Viewport} lines={Lines}",
                state.ScrollOffset, _layout.MaxScroll, _layout.TotalLines, viewport, state.Lines.Length);

            // Footer text was updated after BuildWidgets → rebuild footer widget only.
            // (Avoid rebuilding the whole tree just for the % label.)
            var footerWidget = ParagraphFromFooter(_layout.FooterText);

            foreach ((string name, var widget) in widgets)
            {
                var area = _panelShell.Layout.GetArea(context, name);
                if (area.Width <= 0 || area.Height <= 0)
                    continue;

                if (name == "Footer")
                    context.Render(footerWidget, area);
                else
                    context.Render(widget, area);
            }
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
                Key.F5 => UiKeyCode.F5,
                Key.F6 => UiKeyCode.F6,
                Key.F7 => UiKeyCode.F7,
                Key.F8 => UiKeyCode.F8,
                Key.F9 => UiKeyCode.F9,
                Key.F10 => UiKeyCode.F10,
                Key.F11 => UiKeyCode.F11,
                Key.F12 => UiKeyCode.F12,
                _ => UiKeyCode.None
            };
            return new UiKey(code, mods);
        }
    }
}
