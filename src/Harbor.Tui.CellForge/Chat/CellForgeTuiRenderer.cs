using System.Collections.Immutable;
using System.ComponentModel;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Tui;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Terminal.Abstractions.Views;
using Harbor.Tui.CellForge.Panels;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Sessions;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Harbor.Tui.CellForge.Capabilities;
using Microsoft.Extensions.Logging;

namespace Harbor.Tui.CellForge;

/// <summary>
///     Adapter that exposes the CellForge engine through the standard
///     <see cref="ITuiRenderer" /> / <see cref="BaseTuiRenderer" /> contract
///     (renderer-unification sprint, Phase 2 + TEA integration). The interactive
///     raw-mode entry point remains <c>CellForgeReplRunner</c>; this adapter serves
///     the non-interactive/event-driven path so <c>HARBOR_TUI=cellforge</c>
///     produces a first-class CellForge renderer instead of falling back to
///     the Ansi backend. Output goes through <see cref="AnsiWriter"/>'s SGR
///     automaton: styles are diffed against the writer's tracked state, so
///     redundant escape codes are never emitted.
/// </summary>
[Harbor.Abstractions.Contracts.TuiRenderer(Backend = "cellforge")]
public sealed partial class CellForgeTuiRenderer : BaseTuiRenderer
{
    /// <summary>Input placeholder while the agent is idle and waiting for a prompt.</summary>
    internal const string IdlePlaceholder = "Type your message...";

    /// <summary>Input placeholder while the agent is running a prompt.</summary>
    internal const string BusyPlaceholder = "Agent is running… (Esc to stop)";

    /// <summary>
    /// CF-D-002: host-provided <see cref="ChatScreen"/> so
    /// <see cref="ProjectStateIntoWidgets"/> can feed projected state into
    /// <see cref="Widgets.StatusPanel.ProjectedState"/>. Null (default) means
    /// the renderer is running in a context without a layout tree
    /// (e.g. event-driven non-interactive path) — projected state is skipped.
    /// </summary>
    public ChatScreen? Screen { get; set; }

    private readonly UiStore _store;
    private readonly ISessionManager? _sessions;

    /// <summary>
    ///     Typed dependencies handed to every <see cref="PanelContext" /> this
    ///     renderer builds (#470). Built once from what the host injected — the
    ///     per-frame context no longer carries a container. Named
    ///     <c>PanelDeps</c> rather than <c>PanelServices</c> so the member never
    ///     shadows the type inside this class.
    /// </summary>
    public PanelServices PanelDeps { get; }
    private UiStore? _subscribedStore;
    private readonly StatusBarViewModel _statusVm;
    private readonly ChatHistoryViewModel _chatVm;
    private readonly InputViewModel _inputVm;
    private readonly ComposerController _composer = new();
    private readonly QuickSwitchSlots _quickSwitchSlots = new();
    private bool _syncingInput;

    /// <summary>
    ///     Revision of the last applied <see cref="UiStore" /> notification
    ///     (#94 stale-drop: CAS success and <c>Changed</c> delivery are not
    ///     atomic across threads, so a delayed notification must not rewind
    ///     the widgets — see <see cref="UiStateChangedEventArgs.IsStale" />).
    /// </summary>
    private long _lastProjectedRevision;

    /// <summary>
    ///     Newest store state awaiting the frame tick (#466). Written by the
    ///     event thread, drained by the render thread; the reference swap is
    ///     interlocked so the pair (<c>_pendingProjection</c>,
    ///     <c>_projectionPending</c>) is never observed torn. A torn read can
    ///     only be conservative: <see cref="PumpProjection" /> returning false
    ///     leaves the state parked for the next tick.
    /// </summary>
    private UiState? _pendingProjection;

    private int _projectionPending;

    /// <summary>
    ///     Sidebar projection memo (#466) — the sidebar's inputs cannot move on
    ///     a text delta, but its projection was recomputed per notification.
    /// </summary>
    private readonly SideBarProjectionCache _sideBarCache = new();

    private StatusSlice _lastStatusSlice;
    private StreamingSlice _lastStreamingSlice;
    private SessionSlice _lastSessionSlice;

    /// <summary>
    /// CF-E-002 wiring (TOP-1 #27): renderer-owned panel registry holding the 9
    /// cell-native builtin providers (see <see cref="RegisterBuiltinPanels"/>).
    /// Registration order is significant — Alt+1..9 hotkey slots follow it.
    /// All visibility / focus / size state lives in <see cref="UiState"/> (seeded
    /// via <see cref="CellForgePanelRegistry.EnsureSeeded"/> in
    /// <see cref="InitializeAsync"/>); the registry itself is registration-only.
    /// </summary>
    public CellForgePanelRegistry Panels { get; }

    public CellForgeTuiRenderer(
        ILogger<CellForgeTuiRenderer> logger,
        StatusBarViewModel? statusVm = null,
        ChatHistoryViewModel? chatVm = null,
        InputViewModel? inputVm = null,
        UiStore? store = null,
        ISessionManager? sessions = null,
        PanelServices? panelServices = null)
        : base(logger)
    {
        // Issue #77: the DI-shared store is injected by the composition root
        // (TuiModule) so chat writes land in the same instance the
        // RendererPipeline restores from. Null keeps the previous behaviour
        // (private store) for tests and non-composed hosts.
        _store = store ?? new UiStore();
        // Track D (#27): per-session store from the session manager when a
        // composed host provides one; otherwise the fallback above.
        _sessions = sessions;
        // #470: an ISessionManager IS an IPanelSessionGateway, so the renderer
        // can fill the typed panel bag itself; a host that wants the session
        // store / registry / diagnostics too passes a prebuilt PanelServices.
        PanelDeps = panelServices
            ?? new PanelServices { Store = _store, Sessions = sessions };
        Panels = new CellForgePanelRegistry();
        RegisterBuiltinPanels(Panels);
        _statusVm = statusVm ?? ViewModels.Get<StatusBarViewModel>("status-bar")!;
        _chatVm = chatVm ?? ViewModels.Get<ChatHistoryViewModel>("chat-history")!;
        _inputVm = ResolveInputVm(inputVm);
        Context = new CellForgeRenderContext();
    }

    /// <summary>Golden-frame test seam. The backend must be an
    /// <see cref="ISyncTerminalBackend"/> — the context flushes synchronously
    /// (issue #468).</summary>
    public CellForgeTuiRenderer(
        ILogger<CellForgeTuiRenderer> logger,
        ISyncTerminalBackend backend,
        StatusBarViewModel? statusVm = null,
        ChatHistoryViewModel? chatVm = null,
        InputViewModel? inputVm = null,
        UiStore? store = null,
        ISessionManager? sessions = null,
        PanelServices? panelServices = null)
        : base(logger)
    {
        // Issue #77: see the primary ctor — injected shared store or a
        // private one when null.
        _store = store ?? new UiStore();
        _sessions = sessions;
        // #470: see the primary ctor.
        PanelDeps = panelServices
            ?? new PanelServices { Store = _store, Sessions = sessions };
        Panels = new CellForgePanelRegistry();
        RegisterBuiltinPanels(Panels);
        _statusVm = statusVm ?? ViewModels.Get<StatusBarViewModel>("status-bar")!;
        _chatVm = chatVm ?? ViewModels.Get<ChatHistoryViewModel>("chat-history")!;
        _inputVm = ResolveInputVm(inputVm);
        Context = new CellForgeRenderContext(backend);
    }

    /// <summary>
    /// CF-E-002: registers the 10 cell-native builtin panels. Slot order mirrors
    /// <c>SpectreTuiRenderer.RunInteractiveAsync</c> (Alt+1..9 follow registration
    /// order): help, todo-list, diff-preview, file-tree, token-breakdown,
    /// diagnostics, logs, session-sidebar, jump. Subagents rides last with no
    /// Alt slot (opened from the panel list); Alt+1..9 stay stable.
    /// </summary>
    private static void RegisterBuiltinPanels(CellForgePanelRegistry panels)
    {
        panels.Register(new CellForgeHelpPanel()); // Alt+1
        panels.Register(new CellForgeTodoListPanel()); // Alt+2
        panels.Register(new CellForgeDiffPreviewPanel()); // Alt+3
        panels.Register(new CellForgeFileTreePanel()); // Alt+4
        panels.Register(new CellForgeTokenBreakdownPanel()); // Alt+5
        panels.Register(new CellForgeDiagnosticsPanel()); // Alt+6
        panels.Register(new CellForgeLogsPanel()); // Alt+7
        panels.Register(new CellForgeSessionSidebarPanel()); // Alt+8
        panels.Register(new CellForgeJumpPalettePanel()); // Alt+9
        panels.Register(new CellForgeSubagentsPanel());
    }

    /// <summary>
    ///     Resolves the input view model (explicit instance or the registry default),
    ///     keeps field/registry/bound-view coherent, and wires the two-way binding
    ///     with the composer buffer.
    /// </summary>
    private InputViewModel ResolveInputVm(InputViewModel? inputVm)
    {
        var resolved = inputVm ?? ViewModels.Get<InputViewModel>("input")!;
        ViewModels.Register(resolved);
        resolved.PropertyChanged += OnInputVmChanged;
        return resolved;
    }

    public override ITuiRenderContext Context { get; }

    // CF-F-001: intentionally no ShouldRenderPlacement override — the base filter
    // already paints the ChatHistory/Input placements, and both builtin views write
    // only through ITuiRenderContext (CellForgeRenderContext/AnsiWriter), so no
    // CellForge fallback painter is needed.

    public override Task<Result> InitializeAsync(CancellationToken ct = default)
        => InitializeGuardedAsync(() =>
        {
            EnsureSubscribedToActiveStore();
            Context.HideCursor();
        }, ct: ct);

    /// <summary>
    ///     The store this renderer currently reads and writes: the active
    ///     session's <see cref="SessionContext.Store" /> when a session
    ///     manager with an active context is present, otherwise the fallback
    ///     <see cref="_store" /> (injected shared store or private test seam).
    /// </summary>
    private UiStore ActiveStore => _sessions?.ActiveContext?.Store ?? _store;

    /// <summary>
    ///     (Re)binds the <see cref="OnStoreChanged" /> projection to
    ///     <see cref="ActiveStore" />. On a session switch the old store is
    ///     unsubscribed, the new one subscribed + panel-seeded, and the
    ///     current snapshot projected immediately so the shared VMs show the
    ///     newly-active session without waiting for the next event. No-op
    ///     when the active store did not change.
    /// </summary>
    private void EnsureSubscribedToActiveStore()
    {
        UiStore active = ActiveStore;
        if (ReferenceEquals(_subscribedStore, active))
            return;
        if (_subscribedStore is not null)
            _subscribedStore.Changed -= OnStoreChanged;
        _subscribedStore = active;
        active.Changed += OnStoreChanged;
        // CF-E-002: seed registered panel ids + Hidden states + DefaultSizes
        // into UiState so the reducer becomes the single source of truth.
        // Already-known states/sizes survive re-seeding (plugin reload path).
        _ = Panels.EnsureSeeded(active);
        // The new store's revision ledger starts over — adopt its revision so
        // the stale-drop guard does not swallow its first notifications.
        _lastProjectedRevision = active.State.Revision;
        // #466: the seed goes through the same park→pump seam as every other
        // notification, so a session switch is the one drain point that cannot
        // be forgotten.
        Interlocked.Exchange(ref _pendingProjection, active.State);
        Volatile.Write(ref _projectionPending, 1);
        _ = PumpProjection();
    }

    private void OnStoreChanged(object? sender, UiStateChangedEventArgs e)
    {
        if (e.IsStale(_lastProjectedRevision))
            return;
        _lastProjectedRevision = e.Revision;
        // #466: the notification does NOT project. A streaming turn publishes
        // one notification per token delta, and projecting each of them re-ran
        // the whole widget projection — 11 view-model writes, a full
        // SnapshotText() of the draft, a linear pass over the session list for
        // the quick-switch slots and a sidebar projection that re-copied the
        // whole session array — for state a text delta does not change and that
        // no painted frame reads before the next one. The newest state is
        // parked instead and applied once, by the frame tick.
        Interlocked.Exchange(ref _pendingProjection, e.State);
        Volatile.Write(ref _projectionPending, 1);
    }

    /// <summary>
    ///     Frame tick: apply the parked projection, if any, and report whether it
    ///     ran. At most one projection per call no matter how many store
    ///     notifications arrived since the last one — the parked state is the
    ///     newest, and the projection is a pure function of that state, so
    ///     coalescing N notifications into one call is lossless.
    ///     <para>
    ///         Called from <see cref="RenderAsync" /> (one call per event) and
    ///         from <see cref="Dispose" />, so the last state of a turn is never
    ///         left unpainted. Hosts with a real frame loop can call it instead.
    ///     </para>
    /// </summary>
    /// <returns>True when a projection was applied.</returns>
    public bool PumpProjection()
    {
        if (Interlocked.Exchange(ref _projectionPending, 0) == 0)
            return false;
        var pending = Interlocked.Exchange(ref _pendingProjection, null);
        if (pending is null)
            return false;
        ProjectStateIntoWidgets(pending);
        ProjectionCount++;
        return true;
    }

    /// <summary>
    ///     Projections actually applied since construction (test seam for the
    ///     per-frame coalescing gate — one per pump, not one per notification).
    /// </summary>
    internal long ProjectionCount { get; private set; }

    /// <summary>True while a store notification is parked awaiting the frame tick.</summary>
    internal bool HasPendingProjection => Volatile.Read(ref _projectionPending) != 0;

    public override Task RenderAsync(AgentEvent @event, CancellationToken ct = default)
    {
        EnsureSubscribedToActiveStore();
        _ = ActiveStore.Dispatch(new ChatAppMsg.Agent(@event));
        // The render pass IS the frame: drain the coalesced projection here,
        // before any placement paints, so the widgets a view reads are the
        // ones this event's state produced (#466).
        _ = PumpProjection();
        return base.RenderAsync(@event, ct);
    }

    /// <summary>Composer buffer mirrored from <see cref="UiState.Ui.Input"/> (test seam).</summary>
    internal PromptBuffer PromptBuffer => _composer.Buffer;

    /// <summary>Fallback TEA store (test seam for panel-seeding assertions). The live path reads <see cref="ActiveStore"/>.</summary>
    internal UiStore Store => _store;

    /// <summary>Last projected session list (test seam; SideBarView/QuickSwitchSlots wiring is CF-B-008).</summary>
    internal ImmutableArray<SessionInfo> SessionsSnapshot { get; private set; } = ImmutableArray<SessionInfo>.Empty;

    /// <summary>Last projected active session id (test seam, see <see cref="SessionsSnapshot"/>).</summary>
    internal SessionId? ActiveSessionIdSnapshot { get; private set; }

    /// <summary>Last projected session-list loading flag (test seam, see <see cref="SessionsSnapshot"/>).</summary>
    internal bool SessionsLoading { get; private set; }

    internal void ProjectStateIntoWidgets(UiState state)
    {
        ProjectStatus(state);
        ProjectStreaming(state);
        SyncInputFromState(state);
        ProjectSessions(state);
        ProjectScreen(state);
    }

    /// <summary>
    ///     Status-bar slice. The seven setters are guarded by the toolkit's
    ///     own equality check, so re-running them on an unchanged value costs
    ///     no INPC — but each is still a virtual property write, so the slice
    ///     is skipped outright when none of its inputs moved.
    /// </summary>
    private void ProjectStatus(UiState state)
    {
        if (_statusVm is not StatusBarViewModel svm)
            return;

        if (_lastStatusSlice.Initialized
            && ReferenceEquals(_lastStatusSlice.Status, state.Chat.Status)
            && ReferenceEquals(_lastStatusSlice.Model, state.Chat.Model)
            && ReferenceEquals(_lastStatusSlice.Provider, state.Chat.Provider)
            && ReferenceEquals(_lastStatusSlice.AgentName, state.Chat.AgentName)
            && _lastStatusSlice.Cost == state.Chat.Cost)
        {
            return;
        }

        if (!string.IsNullOrEmpty(state.Chat.Status))
            svm.Status = state.Chat.Status;
        if (!string.IsNullOrEmpty(state.Chat.Model))
            svm.Model = state.Chat.Model;
        if (!string.IsNullOrEmpty(state.Chat.Provider))
            svm.Provider = state.Chat.Provider;
        if (!string.IsNullOrEmpty(state.Chat.AgentName))
            svm.Agent = state.Chat.AgentName;
        svm.TokensIn = (int)Math.Min(state.Chat.Cost.TokensIn, int.MaxValue);
        svm.TokensOut = (int)Math.Min(state.Chat.Cost.TokensOut, int.MaxValue);
        svm.Cost = state.Chat.Cost.CostUsd;

        _lastStatusSlice = StatusSlice.Capture(state);
    }

    /// <summary>
    ///     Streaming-tail slice. A text delta lands in
    ///     <c>Chat.PendingStreamText</c> and only reaches
    ///     <c>Chat.Active.TextBuffer</c> on the flush gate, so during a burst
    ///     the projected tail is usually the same string instance — and
    ///     comparing the instance (not the contents) skips the whole slice.
    /// </summary>
    private void ProjectStreaming(UiState state)
    {
        if (_chatVm is not ChatHistoryViewModel chvm)
            return;

        if (_lastStreamingSlice.Initialized
            && _lastStreamingSlice.IsStreaming == state.Chat.IsStreaming
            && ReferenceEquals(_lastStreamingSlice.TextBuffer, state.Chat.Active.TextBuffer)
            && ReferenceEquals(_lastStreamingSlice.ThinkBuffer, state.Chat.Active.ThinkBuffer))
        {
            return;
        }

        chvm.IsStreaming = state.Chat.IsStreaming;
        // Synced prefix only (flush-gated, like the projector tail):
        // projecting pending here would copy the whole prefix per frame.
        chvm.StreamingText = state.Chat.Active.TextBuffer;
        chvm.ThinkingText = state.Chat.Active.ThinkBuffer;
        chvm.IsThinking = state.Chat.Active.ThinkBuffer.Length != 0;

        _lastStreamingSlice = StreamingSlice.Capture(state);
    }

    /// <summary>
    ///     Session slice. <see cref="QuickSwitchSlots" /> rewrites all
    ///     <c>Count</c> slots from <c>(Sessions, ActiveSessionId)</c> — a linear
    ///     pass over the session list. The list is immutable (reference
    ///     compare); the id compares by value, because a session sync hands out
    ///     a fresh <see cref="SessionId" /> for the same session and re-running
    ///     the pass would be pure waste.
    /// </summary>
    private void ProjectSessions(UiState state)
    {
        SessionsSnapshot = state.Chat.Sessions;
        ActiveSessionIdSnapshot = state.Chat.ActiveSessionId;
        SessionsLoading = state.Chat.IsLoading;

        if (_lastSessionSlice.Initialized
            && _lastSessionSlice.Sessions.Equals(state.Chat.Sessions)
            && SideBarProjectionCache.SameId(_lastSessionSlice.ActiveSessionId, state.Chat.ActiveSessionId))
        {
            return;
        }

        _quickSwitchSlots.SyncFromStore(state);
        _lastSessionSlice = SessionSlice.Capture(state);
    }

    /// <summary>
    ///     Host-screen slice: the status panel's projected state plus the
    ///     sidebar snapshot. The sidebar goes through
    ///     <see cref="SideBarProjectionCache" />, which answers from the
    ///     previous instance while its fingerprint holds — the active-session
    ///     scan and the session-array copy it would otherwise repeat per frame.
    /// </summary>
    private void ProjectScreen(UiState state)
    {
        if (Screen is not { } screen)
            return;

        screen.Status.ProjectedState = state;
        if (screen.Sidebar is SideBarPanel sidebar)
        {
            sidebar.State = SideBarView.Project(state, _sideBarCache);
        }

        // Issue #389 — pure projection. The panel keeps no selection of its
        // own, so handing it the snapshot is the whole wiring, which is why a
        // tab opened by any host shows up with no renderer changes.
        if (screen.Tabs is { } tabs)
        {
            tabs.Strip = state.Chat.TabStrip;
        }
    }

    /// <summary>Fingerprint of the status-bar slice's inputs (issue #466).</summary>
    private struct StatusSlice
    {
        internal bool Initialized;
        internal string? Status;
        internal string? Model;
        internal string? Provider;
        internal string? AgentName;
        internal CostSnapshot Cost;

        internal static StatusSlice Capture(UiState state) => new()
        {
            Initialized = true,
            Status = state.Chat.Status,
            Model = state.Chat.Model,
            Provider = state.Chat.Provider,
            AgentName = state.Chat.AgentName,
            Cost = state.Chat.Cost
        };
    }

    /// <summary>Fingerprint of the streaming-tail slice's inputs (issue #466).</summary>
    private struct StreamingSlice
    {
        internal bool Initialized;
        internal bool IsStreaming;
        internal string? TextBuffer;
        internal string? ThinkBuffer;

        internal static StreamingSlice Capture(UiState state) => new()
        {
            Initialized = true,
            IsStreaming = state.Chat.IsStreaming,
            TextBuffer = state.Chat.Active.TextBuffer,
            ThinkBuffer = state.Chat.Active.ThinkBuffer
        };
    }

    /// <summary>Fingerprint of the session slice's inputs (issue #466).</summary>
    private struct SessionSlice
    {
        internal bool Initialized;
        internal ImmutableArray<SessionInfo> Sessions;
        internal SessionId? ActiveSessionId;

        internal static SessionSlice Capture(UiState state) => new()
        {
            Initialized = true,
            Sessions = state.Chat.Sessions,
            ActiveSessionId = state.Chat.ActiveSessionId
        };
    }

    /// <summary>
    ///     Store → VM + composer-buffer sync: draft text and idle/busy placeholder
    ///     flow from <see cref="UiState"/> (the source of truth). The
    ///     <c>_syncingInput</c> flag suppresses the <see cref="OnInputVmChanged"/>
    ///     echo while VM properties are being applied.
    ///     NOTE(CF-B-005): <c>InputModel.History/HistoryIndex</c> are intentionally
    ///     not mirrored here — history recall stays in
    ///     <c>PromptHistory/ComposerController</c> until CF-B-005.
    ///     #489: the draft is compared with <see cref="PromptBuffer.IsEquivalentTo"/>
    ///     (in place, no string copy) because this runs on every store change,
    ///     i.e. per keystroke AND per streamed token.
    /// </summary>
    private void SyncInputFromState(UiState state)
    {
        _syncingInput = true;
        try
        {
            string text = state.Ui.Input.Text ?? string.Empty;
            if (_inputVm.Text != text)
            {
                _inputVm.Text = text;
                _inputVm.CursorPosition = text.Length;
            }

            _inputVm.Placeholder = state.Chat.IsAgentRunning ? BusyPlaceholder : IdlePlaceholder;

            if (!_composer.Buffer.IsEquivalentTo(text))
            {
                _composer.Buffer.Clear();
                if (text.Length != 0)
                    _ = _composer.Buffer.InsertText(text);
            }

            _ = _composer.Buffer.MoveTo(Math.Clamp(_inputVm.CursorPosition, 0, _composer.Buffer.Length));
        }
        finally
        {
            _syncingInput = false;
        }
    }

    /// <summary>
    ///     VM → composer-buffer sync: user edits applied to the
    ///     <see cref="InputViewModel"/> (text or caret) are mirrored into the
    ///     composer buffer so the interactive prompt paints the same draft.
    ///     #466: span comparison, same reason as <see cref="SyncInputFromState" />.
    /// </summary>
    private void OnInputVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncingInput)
            return;

        if (e.PropertyName is not (nameof(InputViewModel.Text) or nameof(InputViewModel.CursorPosition)))
            return;

        string text = _inputVm.Text ?? string.Empty;
        if (!_composer.Buffer.IsEquivalentTo(text))
        {
            _composer.Buffer.Clear();
            if (text.Length != 0)
                _ = _composer.Buffer.InsertText(text);
        }

        _ = _composer.Buffer.MoveTo(Math.Clamp(_inputVm.CursorPosition, 0, _composer.Buffer.Length));
    }

    public override void Dispose()
    {
        // #466: the last state of a turn can still be parked if no render pass
        // followed the final notification. Drain it before the VMs go away, or
        // the widgets freeze one event behind.
        _ = PumpProjection();
        _inputVm.PropertyChanged -= OnInputVmChanged;
        if (_subscribedStore is not null)
        {
            _subscribedStore.Changed -= OnStoreChanged;
            _subscribedStore = null;
        }
        Context.ShowCursor();
        base.Dispose();
    }

    public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
    {
        Context.ShowCursor();
        Context.WriteColored(prompt, TuiColor.Green);
        string? line = Console.ReadLine();
        Context.HideCursor();
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
}

/// <summary>
///     CellForge render context — routes every write through the
///     <see cref="AnsiWriter" /> SGR automaton over <see cref="StdoutBackend" />.
///     The writer diffs styles frame-to-frame, so styling two consecutive spans
///     with the same color emits zero escape codes for the second span.
/// </summary>
public sealed class CellForgeRenderContext : ITuiRenderContext
{
    private readonly AnsiWriter _writer;

    public CellForgeRenderContext() : this(new StdoutBackend())
    {
    }

    /// <summary>
    /// Render context over an explicitly supplied backend. Takes
    /// <see cref="ISyncTerminalBackend"/>, not <see cref="ITerminalBackend"/>:
    /// <see cref="ITuiRenderContext"/> is an entirely synchronous contract and
    /// <see cref="Flush"/> has nowhere to await, so an async-only backend can
    /// never serve it (issue #468).
    /// </summary>
    public CellForgeRenderContext(ISyncTerminalBackend backend)
    {
        _writer = new AnsiWriter(backend);
    }

    /// <summary>Exposed for diagnostics and golden-frame tests.</summary>
    public AnsiWriter Writer => _writer;

    public int Width
    {
        get
        {
            try
            {
                return Console.WindowWidth;
            }
            catch
            {
                return 0;
            }
        }
    }

    public int Height
    {
        get
        {
            try
            {
                return Console.WindowHeight;
            }
            catch
            {
                return 0;
            }
        }
    }

    public bool SupportsColor => true;

    public void Write(string text)
    {
        _writer.WriteText(text);
        Flush();
    }

    public void WriteLine(string? text = null)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _writer.WriteText(text);
        }

        _writer.WriteLineBreak();
        Flush();
    }

    public void WriteColored(string text, TuiColor foreground, TuiColor? background = null)
    {
        var style = new CellStyle(
            PackedColor.Rgb(foreground.R, foreground.G, foreground.B),
            background.HasValue ? PackedColor.Rgb(background.Value.R, background.Value.G, background.Value.B) : default);
        _writer.WriteStyledText(text, style);
        Flush();
    }

    public void WriteStyled(string text, TuiStyle style)
    {
        _writer.WriteStyledText(text, MapStyle(default, style));
        Flush();
    }

    public void SetCursorPosition(int row, int col) => _writer.MoveTo(col, row);

    public void ClearLine()
    {
        _writer.EraseEntireLine();
        Flush();
    }

    public void Clear()
    {
        _writer.EmitEraseInDisplay(2);
        _writer.InvalidateCursorPosition();
        Flush();
    }

    public void HideCursor()
    {
        _writer.HideCursor();
        Flush();
    }

    public void ShowCursor()
    {
        _writer.ShowCursor();
        Flush();
    }

    public void EnterAlternateScreen()
    {
        // Full mouse grab (issue #36) — same single-source-of-truth flag as
        // the interactive runner; byte-identical to the previous literal.
        _writer.Raw("\x1B[?1049h" + TerminalQueries.MouseFullEnable);
        Flush();
    }

    public void ExitAlternateScreen()
    {
        _writer.Raw(TerminalQueries.MouseDisable + "\x1B[?25h\x1B[?1049l");
        Flush();
    }

    /// <summary>
    ///     AnsiWriter buffers; the sync <see cref="AnsiWriter.FlushSync"/> path
    ///     keeps the render-context contract synchronous without sync-over-async.
    /// </summary>
    public void Flush() => _writer.FlushSync();

    private static CellStyle MapStyle(PackedColor color, TuiStyle style)
    {
        StyleAttr attrs = StyleAttr.None;
        if (style.HasFlag(TuiStyle.Bold)) attrs |= StyleAttr.Bold;
        if (style.HasFlag(TuiStyle.Italic)) attrs |= StyleAttr.Italic;
        if (style.HasFlag(TuiStyle.Underline)) attrs |= StyleAttr.Underline;
        if (style.HasFlag(TuiStyle.Dim)) attrs |= StyleAttr.Dim;
        if (style.HasFlag(TuiStyle.Strike)) attrs |= StyleAttr.Strike;
        if (style.HasFlag(TuiStyle.Reverse)) attrs |= StyleAttr.Reverse;
        return new CellStyle(color, default, attrs);
    }
}
