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
        ISessionManager? sessions = null)
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
        ISessionManager? sessions = null)
        : base(logger)
    {
        // Issue #77: see the primary ctor — injected shared store or a
        // private one when null.
        _store = store ?? new UiStore();
        _sessions = sessions;
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
        ProjectStateIntoWidgets(active.State);
    }

    private void OnStoreChanged(object? sender, UiStateChangedEventArgs e)
    {
        if (e.IsStale(_lastProjectedRevision))
            return;
        _lastProjectedRevision = e.Revision;
        ProjectStateIntoWidgets(e.State);
    }

    public override Task RenderAsync(AgentEvent @event, CancellationToken ct = default)
    {
        EnsureSubscribedToActiveStore();
        _ = ActiveStore.Dispatch(new ChatAppMsg.Agent(@event));
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
        if (_statusVm is StatusBarViewModel svm)
        {
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
        }

        if (_chatVm is ChatHistoryViewModel chvm)
        {
            chvm.IsStreaming = state.Chat.IsStreaming;
            // Synced prefix only (flush-gated, like the projector tail):
            // projecting pending here would copy the whole prefix per frame.
            chvm.StreamingText = state.Chat.Active.TextBuffer;
            chvm.ThinkingText = state.Chat.Active.ThinkBuffer;
            chvm.IsThinking = state.Chat.Active.ThinkBuffer.Length != 0;
        }

        SyncInputFromState(state);

        SessionsSnapshot = state.Chat.Sessions;
        ActiveSessionIdSnapshot = state.Chat.ActiveSessionId;
        SessionsLoading = state.Chat.IsLoading;
        _quickSwitchSlots.SyncFromStore(state);

        if (Screen is { } screen)
        {
            screen.Status.ProjectedState = state;
            if (screen.Sidebar is SideBarPanel sidebar)
            {
                sidebar.State = SideBarView.ProjectFromStore(state);
            }
        }
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
