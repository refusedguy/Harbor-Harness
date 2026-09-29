using System.Text;
using System.Collections.Immutable;
using System.Threading.Channels;
using System.Linq;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.App.Cli.Commands;
using Harbor.App.Cli.Repl.Commands;
using Harbor.Application.Attachments;
using Harbor.Application.Configuration;
using Harbor.Application.Onboarding;
using Harbor.DesignSystem;
using Harbor.Hosting.Rendering;
using Harbor.Tui.CellForge.Capabilities;
using Harbor.Tui.CellForge.Input;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Input;
using Harbor.Ui.Framework.Rendering.Widgets;
using Harbor.Tui.CellForge.Widgets;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Cli.Repl;

/// <summary>
///     CE-4 интерактивный REPL поверх CellForge-движка: второй путь рендера
///     рядом с legacy AnsiTuiRenderer. Фасад над тремя SRP-коллабораторами
///     (G2 split, issue #174): <see cref="ReplLifecycle"/> владеет жизненным
///     циклом экрана и кадровым циклом, <see cref="ReplInputLoop"/> — вводом
///     и submit-маршрутизацией, <see cref="ReplCommandHost"/> — палитрой,
///     слэш-командами и leader-аккордами. Здесь остаются только ctor,
///     <see cref="IReplHost"/>-поверхность, общее состояние и тонкие
///     делегаты — поведение 1-в-1, публичные контракты не меняются.
/// </summary>
/// <remarks>
///     <para>
///         <b>Кадры:</b> event-driven + ENG7-gate — пробуждение (ввод /
///         событие агента / спиннер-тик 80 мс в Running) лишь запрашивает
///         кадр; рендер идёт через 60 fps тикер при изменившейся модели
///         (store revision + dirty seq + геометрия), идентичные кадры
///         закорачиваются без записи в бэкенд. Порядок кадра — как в
///         golden-тестах: solve → prepare → begin → paint × panels → flush.
///     </para>
///     <para>
///         <b>Ctrl+C:</b> во время хода агента — прерывание через существующий
///         механизм <see cref="IAgentRunner.RequestAbort()"/>; в idle — двукратное
///         нажатие выходит из REPL (первое печатает подсказку).
///     </para>
/// </remarks>
internal sealed class CellForgeReplRunner(
    IConfigStore configStore,
    IProviderRegistry providerRegistry,
    IAgentRegistry agentRegistry,
    AuthStore authStore,
    ISessionStore? sessionStore,
    IRendererPipeline? rendererPipeline,
    IEventBus eventBus,
    ITokenTracker? tokens,
    LegacySlashRunner legacySlash,
    IAgent agent,
    Session sessionModel,
    ScreenSession screenSession,
    ChatScreen screen,
    ChatScreenBridge bridge,
    TerminalInputSource inputSource,
    ITerminalModeController modeController,
    ITerminalBackend backend,
    ILogger<CellForgeReplRunner> logger,
    // #49: injected, not service-located — the class already takes its deps
    // via ctor; resolving per abort gesture was a step back.
    IApprovalCoordinator coordinator,
    Harbor.Hosting.PluginReloadService? pluginReload = null,
    IProviderHealthCheck? healthCheck = null,
    Harbor.Ui.Framework.Panels.IPanelRegistry? panelRegistry = null,
    Harbor.Application.Diagnostics.DiagnosticsAggregator? diagnosticsAggregator = null)
    : IReplHost
{
    /// <summary>
    /// Interactive enter/leave sequences (issue #36, decision: full grab).
    /// Composed from <see cref="TerminalQueries"/> single-source-of-truth
    /// constants so the byte order can never drift from the PTY contract:
    /// enter = alt-screen + hide-cursor + paste + full mouse grab, leave =
    /// paste-off + show-cursor + alt-screen-off + mouse-off in reverse order.
    /// Internal for the grab-lifecycle unit tests (InternalsVisibleTo).
    /// </summary>
    internal const string SeqEnterAltScreen = "\x1B[?1049h\x1B[?25l\x1B[?2004h" + TerminalQueries.MouseFullEnable;
    internal const string SeqLeaveAltScreen = "\x1B[?2004l\x1B[?25h\x1B[?1049l" + TerminalQueries.MouseDisable;

    internal readonly Channel<object?> _wake = Channel.CreateUnbounded<object?>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    /// <summary>Real bus events marshalled onto the frame-loop thread: the
    /// bridge (and thus the whole timeline) is touched from THIS thread only.</summary>
    internal readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    /// <summary>TEA accumulator (epic C): every agent event is dual-written here
    /// alongside the bridge. Nothing paints from it yet (status does in step 3) —
    /// it only accumulates Lines/Cost/Status so projection has real state later.</summary>
    internal readonly UiStore _replStore = new();

    /// <summary>
    ///     Pushes the headless core's diagnostic snapshots into
    ///     <see cref="_replStore" /> (#674). Held for its <see cref="IDisposable" />
    ///     lifetime: the aggregator is a process singleton, so an un-unsubscribed
    ///     handler here would keep dispatching into a store the REPL has left.
    ///     Null when the host registered no aggregator — the panel then reads an
    ///     empty snapshot, which is honest rather than stale.
    /// </summary>
    internal readonly DiagnosticsSync? _diagnosticsSync =
        diagnosticsAggregator is null ? null : new DiagnosticsSync(_replStore, diagnosticsAggregator, logger);

    internal readonly StatusViewModel _status = screen.Status.Vm;
    internal readonly ComposerController _composer = screen.Composer.Composer;
    internal readonly VirtualizedChatTimeline _timeline = screen.Timeline.Timeline;
    internal readonly CommandPaletteView _palette = new();
    internal readonly LeaderKeyRouter _leader = new();
    internal readonly ChatKeyMap _keyMap = new();
    internal readonly VimComposerMode _vim = new();
    internal readonly SelectionEngine _selection = new();

    /// <summary>
    ///     Runs the tab-strip effects the REPL does not handle inline (#389).
    ///     The REPL executes submit / abort / quit as gestures rather than from
    ///     store effects (see <c>ReplInputLoop</c>), so the generic effect host
    ///     would double-fire them; this narrow runner is scoped to the session
    ///     effects only, which have no gesture equivalent.
    /// </summary>
    private ReplTabEffectRunner? _effectsRunner;
    internal ReplTabEffectRunner Effects => _effectsRunner ??= new ReplTabEffectRunner(
        // Issue #389: the activate effect lands on the very coordinator the
        // sessions palette and the quick-switch chords already use, so a
        // keyboard switch and a palette switch cannot drift apart.
        id => Sessions.SwitchToSessionAsync(id, CancellationToken.None),
        () => Sessions.OpenSessionsPalette(),
        ex => logger.LogError(ex, "Tab-strip effect failed"));

    // ── IReplHost (Command pattern seam; transitional, see IReplHost.cs) ──
    IAgent IReplHost.Agent => agent;

    Session IReplHost.SessionModel
    {
        get => sessionModel;
        set => sessionModel = value;
    }

    ChatScreenBridge IReplHost.Bridge => bridge;
    UiStore IReplHost.Store => _replStore;
    CommandPaletteView IReplHost.Palette => _palette;
    StatusViewModel IReplHost.Status => _status;
    ChatScreen IReplHost.Screen => screen;
    SelectionEngine IReplHost.Selection => _selection;
    VirtualizedChatTimeline IReplHost.Timeline => _timeline;
    ComposerController IReplHost.Composer => _composer;
    void IReplHost.WakeUp() => _wake.Writer.TryWrite(null);
    void IReplHost.OpenSlashPalette() => Commands.OpenSlashPalette();
    void IReplHost.ToggleVimMode() => Commands.ToggleVimMode();
    void IReplHost.ScrollTimelineToEnd() => _timeline.ScrollToEnd(Math.Max(1, _timelineViewportH));
    void IReplHost.RequestQuit(int exitCode)
    {
        _slashExitCode = exitCode;
        _quitRequested = true;
    }
    Task IReplHost.SwitchToSessionAsync(string sessionId, CancellationToken ct) => Sessions.SwitchToSessionAsync(sessionId, ct);
    Task IReplHost.ExecutePaletteItemAsync(CommandItem item, CancellationToken ct) => Commands.ExecutePaletteItemAsync(item, ct);
    Task IReplHost.ExecuteInfoAsync(string text, CancellationToken ct) => Commands.ExecuteInfoCommandAsync(text, ct);
    Task IReplHost.SyncSessionsToStoreAsync(CancellationToken ct) => Sessions.SyncSessionsToStoreAsync(ct);
    Task<int> IReplHost.ResolveContextWindowAsync(string providerId, string modelId, CancellationToken ct)
        => Lifecycle.ResolveContextWindowAsync(providerId, modelId, ct);
    IConfigStore IReplHost.ConfigStore => configStore;
    IProviderRegistry IReplHost.ProviderRegistry => providerRegistry;
    IAgentRegistry IReplHost.AgentRegistry => agentRegistry;
    AuthStore IReplHost.AuthStore => authStore;
    ISessionStore? IReplHost.SessionStore => sessionStore;
    IRendererPipeline? IReplHost.RendererPipeline => rendererPipeline;
    Harbor.Hosting.PluginReloadService? IReplHost.PluginReload => pluginReload;
    IProviderHealthCheck? IReplHost.HealthCheck => healthCheck;
    Harbor.Ui.Framework.Panels.IPanelRegistry? IReplHost.PanelRegistry => panelRegistry;

    /// <summary>Images staged by <c>/attach</c> for the next user turn (#386).</summary>
    internal readonly ImageAttachmentStash _attachments = new();

    internal readonly ReplCommandCatalog _catalog =
        ReplCommandCatalog.CreateDefault(new ImageAttachmentReader(providerRegistry, logger));

    // ── Extracted collaborators (SRP: the runner owns the shared state and
    // the IReplHost surface; sessions/titles/prompts/input/commands/lifecycle
    // live in focused classes that collaborate through it).
    internal LegacySlashRunner LegacySlash => legacySlash;
    private PromptPipeline? _pipeline;
    internal PromptPipeline Pipeline => _pipeline ??= new PromptPipeline(
        this, _catalog, logger, Tokens, new Lazy<LegacySlashRunner>(() => LegacySlash), Setup);
    internal void DisposePipeline() => _pipeline?.Dispose();
    private SessionSwitchManager? _sessions;
    internal SessionSwitchManager Sessions => _sessions ??= new SessionSwitchManager(this, Pipeline.ClearQueue);
    private SessionTitleService? _titles;
    internal SessionTitleService Titles => _titles ??= new SessionTitleService(this, logger);
    internal ThemeFileWatcher? _themeWatcher;
    private ReplInputLoop? _input;
    internal ReplInputLoop Input => _input ??= new ReplInputLoop(this);
    private ReplCommandHost? _commands;
    internal ReplCommandHost Commands => _commands ??= new ReplCommandHost(this);
    private ReplLifecycle? _lifecycle;
    internal ReplLifecycle Lifecycle => _lifecycle ??= new ReplLifecycle(this);

    private SetupChecklistController? _setup;
    /// <summary>
    /// Setup-guide checklist (KILLER_FEATURES §2.7 Feature 9, issue #383):
    /// detection → completion snapshot → the modal overlay seated on
    /// <see cref="Screen.SetupChecklist" />, plus first-run gating.
    /// </summary>
    internal SetupChecklistController Setup => _setup ??= new SetupChecklistController(
        this,
        new SetupChecklistDetector(configStore, authStore, healthCheck));

    // ── Internal accessors for the collaborators (G2 split seam): captured
    // ctor parameters are invisible outside this class, so the input loop,
    // command host and lifecycle reach them through these. IReplHost stays
    // frozen — test FakeHost/StubHost implementations keep compiling.
    internal IEventBus EventBus => eventBus;
    internal TerminalInputSource InputSource => inputSource;
    internal ITerminalModeController ModeController => modeController;
    internal ITerminalBackend Backend => backend;
    internal ILogger<CellForgeReplRunner> Log => logger;
    internal IApprovalCoordinator Coordinator => coordinator;
    internal ScreenSession ScreenSession => screenSession;
    internal ChatScreen Screen => screen;
    internal ChatScreenBridge Bridge => bridge;
    internal IAgent Agent => agent;
    internal Session SessionModel => sessionModel;
    internal IConfigStore ConfigStore => configStore;
    internal IProviderRegistry ProviderRegistry => providerRegistry;
    internal ITokenTracker? Tokens => tokens;
    ImageAttachmentStash? IReplHost.Attachments => _attachments;

    /// <summary>Leader chord hand-off for async slash commands: the chord resolves
    /// into catalog execution on the frame loop (async work can't run inside Bind actions).</summary>
    internal string? _leaderSlash;

    /// <summary>Leader digit hand-off: the quick-switch chord resolves into a
    /// session switch on the frame loop (async work can't run inside Bind actions).</summary>
    internal char? _quickSwitchChord;

    /// <summary>Theme live-reload hand-off: the watcher's poll timer thread
    /// writes the line, the frame loop drains and appends it — the bridge is
    /// touched from the frame thread only.</summary>
    internal volatile string? _themeReloadLine;

    /// <summary>
    /// Setup-checklist damage hand-off (issue #383): a completion detected off
    /// the frame thread stages the checklist box rect behind the pending flag;
    /// the frame loop applies it via <see cref="ScreenSession.Damage" /> — the
    /// diff engine's hint list is render-thread owned and must never be touched
    /// from the probe thread.
    /// </summary>
    internal volatile bool _setupChecklistDamagePending;

    /// <summary>Staged checklist box rect (read only after the pending flag).</summary>
    internal Rect _setupChecklistDamage;

    /// <summary>Inline-image protocol for this session (osc-sprint §1337):
    /// detected once at startup — kitty → APC, iTerm2/WezTerm/Konsole/mintty
    /// → OSC 1337, everything else keeps the text description card.</summary>
    internal readonly InlineImageKind _inlineImage = InlineImageProbe.Detect();

    /// <summary>
    /// Fullscreen image zoom viewer (KILLER_FEATURES §2.7 Feature 12, issue
    /// #387). Seated on the z-stack by <c>Screen.SyncOverlays</c>; the host
    /// only opens it — zoom, dismiss and the input barrier live in the overlay
    /// and its layer, so no key can slip past to the agent while it is up.
    /// </summary>
    internal ImageViewerOverlay Images => Screen.ImageViewer;

    /// <summary>True when a custom theme file exists — it owns the palette and
    /// the OSC 11 auto-detect must not override it (file wins, P3.2 > P3.3).</summary>
    internal bool _themeFileApplied;

    internal int _timelineViewportH;

    /// <summary>Memoized status snapshot (projector fast-path on quiet frames).</summary>
    internal UiState? _lastStatusSnapshot;

    /// <summary>
    /// ENG7 (issue #278) FPS ticker + frame short-circuit: the 60 fps gate
    /// over the wake-driven loop (render on tick, not on event).
    /// <see cref="_frameDirtySeq"/> is bumped once per loop iteration that
    /// mutated paint state outside the TEA store; the pair
    /// (store revision, dirty seq) plus geometry is the frame's model
    /// version — a wake with an unchanged version renders nothing, so idle
    /// produces zero backend writes. Animation/resize force through the
    /// version check but still pace through the ticker.
    /// </summary>
    internal readonly FrameTicker _frameTicker = new();
    internal long _frameDirtySeq;
    internal long _lastFrameStoreRevision = -1;
    internal long _lastFrameDirtySeq = -1;
    internal int _lastFrameCols = -1;
    internal int _lastFrameRows = -1;

    /// <summary>Last viewport geometry pushed to the TEA store (changed-only).</summary>
    internal int _lastStoreViewport = -1;

    /// <summary>Last total-lines count pushed to the TEA store (changed-only).</summary>
    internal int _lastStoreTotal = -1;

    /// <summary>Partial-scan damage ledger (renderer-moat sprint): frames
    /// triggered by user input or event-driven state changes repaint via the
    /// plain full scan; only quiet animation frames (spinner, gate pulse,
    /// entrance fades) narrow the diff to hinted rects.</summary>
    internal bool _broadDamageNextFrame = true;
    internal readonly Rect[] _fxDamageScratch = new Rect[VirtualizedChatTimeline.MaxFxDamage];

    /// <summary>Post-render glow slots (renderer-moat T3): preallocated effect
    /// instances + the frame's glow-region scratch — the pipeline itself lives
    /// on the <see cref="ScreenSession"/> and is refreshed per frame.</summary>
    internal readonly GlowEffect[] _glowEffects = new GlowEffect[VirtualizedChatTimeline.MaxFxDamage];
    internal readonly GlowRegion[] _glowScratch = new GlowRegion[VirtualizedChatTimeline.MaxFxDamage];

    /// <summary>Width the sidebar spring policy was last applied for (P1.6
    /// spring resize): 0 until the first frame so cold start snaps instead
    /// of replaying the static geometry as motion.</summary>
    internal int _sidebarPolicyCols;

    /// <summary>False until the first policy application — cold start snaps
    /// (static solve already matches the targets), only real width changes
    /// afterwards are animated.</summary>
    internal bool _sidebarPolicyWasApplied;

    // -1, NOT long.MinValue: TickCount64 is non-negative uptime ms, so
    // `now − long.MinValue` overflows to a NEGATIVE value and the first idle
    // Ctrl+C would satisfy the quit-window check immediately (CE-5 PTY-suite
    // finding: paste scenario exited on the FIRST press with no hint).
    internal long _lastIdleAbortMs = -1;
    internal bool _quitRequested;
    internal int? _slashExitCode;

    /// <summary>
    ///     Runs the REPL until quit. Returns the exit code
    ///     (slash <c>/exit</c> wins over the loop's own, mirroring legacy).
    /// </summary>
    public Task<int> RunAsync(CancellationToken ct = default) => Lifecycle.RunAsync(ct);

    /// <summary>
    /// Heartbeat condition (#170): the Running/Compacting spinner plus any
    /// mascot reaction/latch still playing out in Idle — the error/success face
    /// revives by itself once the latch expires instead of freezing mid-sequence
    /// forever. Internal for the heartbeat unit tests (InternalsVisibleTo).
    /// </summary>
    internal static bool ShouldKeepHeartbeat(StatusBarMode mode, bool mascotAnimating) =>
        ReplLifecycle.ShouldKeepHeartbeat(mode, mascotAnimating);
}
