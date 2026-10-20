using System.Collections.Immutable;
using Ensure = Harbor.Abstractions.Extensions.Ensure;
using System.ComponentModel;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge;

/// <summary>
/// Per-screen projector (epic #1195, issue #1202): folds one <see cref="UiState"/>
/// snapshot into the widgets of the screen named by <see cref="ScreenId"/>.
/// In-tree only — this is NOT a plugin axis: the renderer owns the
/// registry, implementations live beside the screens they serve, and nothing
/// outside this assembly registers one — so no plugin can reach it and
/// <c>PluginAxisRegistry</c> does not name it.
/// <para/>
/// Inversion lesson (ratatui): ratatui has NO projection — render reads the app
/// state directly — so chat slices must not leak into the common path. The
/// renderer calls <see cref="ScreenProjectorRegistry.Project"/> and never
/// names a slice; each screen brings the projector that knows its own.
/// </summary>
/// <typeparam name="TState">Snapshot type (today always <see cref="UiState"/>).</typeparam>
public interface IProjector<in TState>
{
    /// <summary>Screen (or panel group) this projector serves; the registry key.</summary>
    string ScreenId { get; }

    /// <summary>Folds <paramref name="state"/> into this screen's widgets.</summary>
    void Project(TState state);
}

/// <summary>
/// Renderer-owned projector routing (epic #1195, issue #1202): maps a screen id
/// to its <see cref="IProjector{UiState}"/> so the frame tick projects without
/// knowing any screen. Unknown ids are a no-op <c>false</c>, never a throw —
/// a screen that brings no projector simply paints what it already holds.
/// </summary>
public sealed class ScreenProjectorRegistry
{
    private readonly Dictionary<string, IProjector<UiState>> _byScreenId = new(StringComparer.Ordinal);

    /// <summary>Registered projector count.</summary>
    public int Count => _byScreenId.Count;

    /// <summary>Registered screen ids.</summary>
    public IReadOnlyCollection<string> ScreenIds => _byScreenId.Keys;

    /// <summary>Registers (or replaces) the projector for its <see cref="IProjector{TState}.ScreenId"/>.</summary>
    public void Register(IProjector<UiState> projector)
    {
        Ensure.NotNull(projector);
        Ensure.NotBlank(projector.ScreenId);
        _byScreenId[projector.ScreenId] = projector;
    }

    /// <summary>Projector for <paramref name="screenId"/>, or false when none is registered.</summary>
    public bool TryGet(string screenId, out IProjector<UiState>? projector) =>
        _byScreenId.TryGetValue(screenId, out projector);

    /// <summary>
    /// Projects <paramref name="state"/> through the registered projector.
    /// Returns false (no-op) when <paramref name="screenId"/> has none.
    /// </summary>
    public bool Project(string screenId, UiState state)
    {
        Ensure.NotNull(state);
        if (!_byScreenId.TryGetValue(screenId, out var projector))
            return false;
        projector.Project(state);
        return true;
    }
}

/// <summary>
/// Chat-screen projection (epic #1195, issue #1202): the one chat implementation
/// of <see cref="IProjector{UiState}"/>, extracted verbatim from
/// <c>CellForgeTuiRenderer.ProjectStateIntoWidgets</c> so the renderer keeps the
/// frame tick (park→pump) and this class owns every chat slice it folds:
/// status bar, streaming tail, composer/input sync, session list, host screen.
/// New screens bring their own projector; the renderer never names these slices.
/// </summary>
public sealed class ChatScreenProjector : IProjector<UiState>, IDisposable
{
    /// <summary>Registry key the chat renderer projects under.</summary>
    public const string ChatScreenId = "chat";

    /// <inheritdoc/>
    public string ScreenId => ChatScreenId;

    private readonly StatusBarViewModel _statusVm;
    private readonly ChatHistoryViewModel _chatVm;
    private readonly InputViewModel _inputVm;
    private readonly ComposerController _composer;
    private readonly QuickSwitchSlots _slots;
    private readonly Func<ChatScreen?> _screen;
    private readonly string _idlePlaceholder;
    private readonly string _busyPlaceholder;
    private readonly SideBarProjectionCache _sideBarCache = new();
    private StatusSlice _lastStatusSlice;
    private StreamingSlice _lastStreamingSlice;
    private SessionSlice _lastSessionSlice;
    private bool _syncingInput;
    private bool _disposed;

    /// <summary>Last projected session list (test seam; SideBarView/QuickSwitchSlots wiring is CF-B-008).</summary>
    public ImmutableArray<SessionInfo> SessionsSnapshot { get; private set; } = ImmutableArray<SessionInfo>.Empty;

    /// <summary>Last projected active session id (test seam, see <see cref="SessionsSnapshot"/>).</summary>
    public SessionId? ActiveSessionIdSnapshot { get; private set; }

    /// <summary>Last projected session-list loading flag (test seam, see <see cref="SessionsSnapshot"/>).</summary>
    public bool SessionsLoading { get; private set; }

    public ChatScreenProjector(
        StatusBarViewModel statusVm,
        ChatHistoryViewModel chatVm,
        InputViewModel inputVm,
        ComposerController composer,
        QuickSwitchSlots slots,
        Func<ChatScreen?> screen,
        string idlePlaceholder,
        string busyPlaceholder)
    {
        _statusVm = statusVm ?? throw new ArgumentNullException(nameof(statusVm));
        _chatVm = chatVm ?? throw new ArgumentNullException(nameof(chatVm));
        _inputVm = inputVm ?? throw new ArgumentNullException(nameof(inputVm));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _slots = slots ?? throw new ArgumentNullException(nameof(slots));
        _screen = screen ?? throw new ArgumentNullException(nameof(screen));
        _idlePlaceholder = idlePlaceholder ?? throw new ArgumentNullException(nameof(idlePlaceholder));
        _busyPlaceholder = busyPlaceholder ?? throw new ArgumentNullException(nameof(busyPlaceholder));
        _inputVm.PropertyChanged += OnInputVmChanged;
    }

    /// <inheritdoc/>
    public void Project(UiState state)
    {
        Ensure.NotNull(state);
        ProjectStatus(state);
        ProjectStreaming(state);
        SyncInputFromState(state);
        ProjectSessions(state);
        ProjectScreen(state);
    }

    /// <summary>
    /// Status-bar slice. The seven setters are guarded by the toolkit's
    /// own equality check, so re-running them on an unchanged value costs
    /// no INPC — but each is still a virtual property write, so the slice
    /// is skipped outright when none of its inputs moved.
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
        // #653: pass the core's "could this be priced?" through unchanged, so the
        // status line says "—" for an unpriced model instead of "$0.0000".
        svm.IsCostKnown = !state.Chat.Cost.IsCostUnpriced;

        _lastStatusSlice = StatusSlice.Capture(state);
    }

    /// <summary>
    /// Streaming-tail slice. A text delta lands in
    /// <c>Chat.PendingStreamText</c> and only reaches
    /// <c>Chat.Active.TextBuffer</c> on the flush gate, so during a burst
    /// the projected tail is usually the same string instance — and
    /// comparing the instance (not the contents) skips the whole slice.
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
    /// Session slice. <see cref="QuickSwitchSlots" /> rewrites all
    /// <c>Count</c> slots from <c>(Sessions, ActiveSessionId)</c> — a linear
    /// pass over the session list. The list is immutable (reference
    /// compare); the id compares by value, because a session sync hands out
    /// a fresh <see cref="SessionId" /> for the same session and re-running
    /// the pass would be pure waste.
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

        _slots.SyncFromStore(state);
        _lastSessionSlice = SessionSlice.Capture(state);
    }

    /// <summary>
    /// Host-screen slice: the status panel's projected state plus the
    /// sidebar snapshot. The sidebar goes through
    /// <see cref="SideBarProjectionCache" />, which answers from the
    /// previous instance while its fingerprint holds — the active-session
    /// scan and the session-array copy it would otherwise repeat per frame.
    /// </summary>
    private void ProjectScreen(UiState state)
    {
        if (_screen() is not { } screen)
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
    /// Store → VM + composer-buffer sync: draft text and idle/busy placeholder
    /// flow from <see cref="UiState"/> (the source of truth). The
    /// <c>_syncingInput</c> flag suppresses the <see cref="OnInputVmChanged"/>
    /// echo while VM properties are being applied.
    /// NOTE(CF-B-005): <c>InputModel.History/HistoryIndex</c> are intentionally
    /// not mirrored here — history recall stays in
    /// <c>PromptHistory/ComposerController</c> until CF-B-005.
    /// #489: the draft is compared with <see cref="PromptBuffer.IsEquivalentTo"/>
    /// (in place, no string copy) because this runs on every store change,
    /// i.e. per keystroke AND per streamed token.
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

            _inputVm.Placeholder = state.Chat.IsAgentRunning ? _busyPlaceholder : _idlePlaceholder;

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
    /// VM → composer-buffer sync: user edits applied to the
    /// <see cref="InputViewModel"/> (text or caret) are mirrored into the
    /// composer buffer so the interactive prompt paints the same draft.
    /// #466: span comparison, same reason as <see cref="SyncInputFromState" />.
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

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _inputVm.PropertyChanged -= OnInputVmChanged;
    }
}
