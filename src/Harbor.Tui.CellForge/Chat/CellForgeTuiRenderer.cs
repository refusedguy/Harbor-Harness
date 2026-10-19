using System.Collections.Immutable;
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
// #436: the frame writer is the engine AnsiWriter, so styled text is built
// in the engine style vocabulary even though this file lives outside the
// engine namespace (aliases pin the bare names to Rendering).
using EngineCells = Harbor.Tui.CellForge.Rendering;
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

    /// <summary>
    ///     Mediator seam (issue #1556): owns the chat screen's three colleague
    ///     view-models, the composer and the quick-switch slots, and assembles
    ///     <see cref="ChatScreenProjector" /> from them at one assembly point —
    ///     the construction the renderer's two constructors used to duplicate.
    ///     The renderer depends on the seam, not on each VM by name.
    /// </summary>
    private readonly IChatViewModelMediator _mediator;
    private readonly ChatScreenProjector _chatProjector;

    /// <summary>
    /// Per-screen projectors (epic #1195, issue #1202): the frame tick projects
    /// through this registry and never names a state slice — the chat slices
    /// live in <see cref="ChatScreenProjector"/>, new screens bring their own.
    /// </summary>
    public ScreenProjectorRegistry Projectors { get; }

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
        _mediator = new ChatViewModelMediator(
            ViewModels, statusVm, chatVm, inputVm,
            () => Screen, IdlePlaceholder, BusyPlaceholder);
        _chatProjector = _mediator.ChatProjector;
        Projectors = new ScreenProjectorRegistry();
        Projectors.Register(_chatProjector);
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
        _mediator = new ChatViewModelMediator(
            ViewModels, statusVm, chatVm, inputVm,
            () => Screen, IdlePlaceholder, BusyPlaceholder);
        _chatProjector = _mediator.ChatProjector;
        Projectors = new ScreenProjectorRegistry();
        Projectors.Register(_chatProjector);
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

    // Mediator seam (issue #1556): VM registry resolution and the composer
    // two-way binding moved to ChatViewModelMediator.ResolveInputVm — the
    // input VM registration into the shared registry rides with it verbatim.

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

    /// <summary>True while a store notification is parked awaiting the frame tick.</summary>
    public bool HasPendingWork => HasPendingProjection;

    /// <summary>
    /// Headless-test idle seam (#1183, textual Pilot steal): drain parked
    /// projections until none remains (or the timeout elapses). Textual's
    /// pilot waits for the message pump to drain instead of sleeping fixed
    /// delays; the parked projection is the CellForge equivalent of "pump
    /// work outstanding" (#466). Polls on a short quantum and exits early the
    /// moment the pump drains — a fixed <c>Task.Delay(n)</c> always pays
    /// <c>n</c>, this pays only what the pump needed.
    /// <para />
    /// NOTE: this member lives in the main file (not a new partial) because
    /// <c>RendererAdapterGenerator</c> emits one source per class-declaration
    /// syntax node — a second <c>partial CellForgeTuiRenderer</c> file
    /// collides on the adapter hintName (CS8785).
    /// </para>
    /// </summary>
    /// <param name="timeout">Maximum wait. Null means 5 seconds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when idle, false on timeout.</returns>
    public async Task<bool> WaitForIdleAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        TimeSpan deadline = timeout ?? TimeSpan.FromSeconds(5);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            _ = PumpProjection();
            if (!HasPendingProjection)
                return true;
            if (sw.Elapsed >= deadline)
                return false;
            await Task.Delay(5, ct).ConfigureAwait(false);
        }
    }

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
    internal PromptBuffer PromptBuffer => _mediator.PromptBuffer;

    /// <summary>Fallback TEA store (test seam for panel-seeding assertions). The live path reads <see cref="ActiveStore"/>.</summary>
    internal UiStore Store => _store;

    /// <summary>Last projected session list (test seam; SideBarView/QuickSwitchSlots wiring is CF-B-008).</summary>
    internal ImmutableArray<SessionInfo> SessionsSnapshot => _chatProjector.SessionsSnapshot;

    /// <summary>Last projected active session id (test seam, see <see cref="SessionsSnapshot"/>).</summary>
    internal SessionId? ActiveSessionIdSnapshot => _chatProjector.ActiveSessionIdSnapshot;

    /// <summary>Last projected session-list loading flag (test seam, see <see cref="SessionsSnapshot"/>).</summary>
    internal bool SessionsLoading => _chatProjector.SessionsLoading;

    /// <summary>
    /// Frame-tick projection entry (epic #1195, issue #1202): routes through
    /// <see cref="Projectors"/> under <see cref="ChatScreenProjector.ChatScreenId"/>.
    /// The renderer names no slice here — the chat slices live in the registered
    /// projector, and new screens project through their own registry entries.
    /// </summary>
    internal void ProjectStateIntoWidgets(UiState state)
    {
        _ = Projectors.Project(ChatScreenProjector.ChatScreenId, state);
    }


    public override void Dispose()
    {
        // #466: the last state of a turn can still be parked if no render pass
        // followed the final notification. Drain it before the VMs go away, or
        // the widgets freeze one event behind.
        _ = PumpProjection();
        _chatProjector.Dispose();
        if (_subscribedStore is not null)
        {
            _subscribedStore.Changed -= OnStoreChanged;
            _subscribedStore = null;
        }
        Context.ShowCursor();
        base.Dispose();
    }

    public override async Task<Maybe<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
    {
        Context.ShowCursor();
        try
        {
            Context.WriteColored(prompt, TuiColor.Green);
            ct.ThrowIfCancellationRequested();
            // #1110: Console.ReadLine blocks the calling (frame-loop) thread with
            // no cancellation. Offload to the pool so ct can abort the wait
            // (PlainPermissionAsker passes ct expecting Deny-on-cancel); the
            // orphaned pool read may still block on stdin, which is unavoidable
            // for a console reader.
            string? line = await Task.Run(Console.ReadLine, CancellationToken.None).WaitAsync(ct).ConfigureAwait(false);
            // EOF is absence, not failure: Maybe.None, not Success("") (#589).
            return Maybe.From(line);
        }
        finally
        {
            Context.HideCursor();
        }
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

    // The writer is NOT exposed (issue #494). A `public AnsiWriter Writer`
    // property sat here, documented "exposed for diagnostics and golden-frame
    // tests", and had no readers: the golden tests substitute one layer down,
    // at ISyncTerminalBackend — RecordingBackend / CountingBackend /
    // AsyncOnlyBackend — which is what the parameter above is for. Everything
    // this context does to the writer is already a member of ITuiRenderContext,
    // so the property published 28 implementation members to anyone who asked
    // and bound them to recompile against Harbor.Tui.CellForge.Engine.

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
        var style = new EngineCells.CellStyle(
            EngineCells.PackedColor.Rgb(foreground.R, foreground.G, foreground.B),
            background.HasValue ? EngineCells.PackedColor.Rgb(background.Value.R, background.Value.G, background.Value.B) : default);
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
        // #1160 cursor-save audit: the cursor-show rides the slim trait
        // (ShowCursor), not an escape insert — byte-identical (same buffer
        // order), but the ?25h shape stays owned by AnsiWriter. Alt-screen +
        // mouse remain Raw: mode switches, not cursor/scroll primitives.
        _writer.Raw(TerminalQueries.MouseDisable);
        _writer.ShowCursor();
        _writer.Raw("\x1B[?1049l");
        Flush();
    }

    /// <summary>
    ///     AnsiWriter buffers; the sync <see cref="AnsiWriter.FlushSync"/> path
    ///     keeps the render-context contract synchronous without sync-over-async.
    /// </summary>
    public void Flush() => _writer.FlushSync();

    private static EngineCells.CellStyle MapStyle(EngineCells.PackedColor color, TuiStyle style)
    {
        EngineCells.StyleAttr attrs = EngineCells.StyleAttr.None;
        if (style.HasFlag(TuiStyle.Bold)) attrs |= EngineCells.StyleAttr.Bold;
        if (style.HasFlag(TuiStyle.Italic)) attrs |= EngineCells.StyleAttr.Italic;
        if (style.HasFlag(TuiStyle.Underline)) attrs |= EngineCells.StyleAttr.Underline;
        if (style.HasFlag(TuiStyle.Dim)) attrs |= EngineCells.StyleAttr.Dim;
        if (style.HasFlag(TuiStyle.Strike)) attrs |= EngineCells.StyleAttr.Strike;
        if (style.HasFlag(TuiStyle.Reverse)) attrs |= EngineCells.StyleAttr.Reverse;
        return new EngineCells.CellStyle(color, default, attrs);
    }
}
