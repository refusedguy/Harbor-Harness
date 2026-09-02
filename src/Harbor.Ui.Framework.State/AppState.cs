using System.Collections.Immutable;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Ui.Framework.Panels;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     Unified immutable UI state for the hybrid MVU+MVVM architecture.
/// </summary>
/// <remarks>
///     <para>
///         Merges the TUI-oriented <see cref="UiState" />, the shell chrome from
///         <see cref="AppState" />, and the application chrome (navigation, modals,
///         toasts) into a single snapshot. Every interactive renderer projects from this
///         — there is no per-renderer state divergence.
///     </para>
///     <para>
///         Designed for NativeAOT and zero-reflection: all members are value types
///         or <see cref="ImmutableArray{T}" /> / <see cref="ImmutableDictionary{TKey,TValue}" />.
///         No <see cref="List{T}" />, no reflection-based binding.
///     </para>
/// </remarks>
public sealed record AppState
{
    // ── Transcript ────────────────────────────────────────────────────────

    /// <summary>The full transcript (user/assistant/tool/… lines), oldest first.</summary>
    public ImmutableArray<ChatLine> Lines { get; init; } = ImmutableArray<ChatLine>.Empty;

    /// <summary>Live streaming message (text + thinking) for the current turn.</summary>
    public ActiveMessage Active { get; init; } = ActiveMessage.Empty;

    /// <summary>Whether a message is actively streaming right now.</summary>
    public bool IsStreaming { get; init; }

    /// <summary>Whether the model is currently emitting thinking tokens.</summary>
    public bool IsThinking { get; init; }

    /// <summary>Human-readable status: idle / running / compacting / error.</summary>
    public string Status { get; init; } = "idle";

    /// <summary>Running token/cost accounting for the status line.</summary>
    public CostSnapshot Cost { get; init; }

    /// <summary>Active model id (for the header/status).</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>Active provider id (for the header/status).</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>Active agent name (for the header/status).</summary>
    public string AgentName { get; init; } = string.Empty;

    /// <summary>Whether the agent is currently running a prompt.</summary>
    public bool IsAgentRunning { get; init; }

    /// <summary>
    ///     Snapshot of <see cref="IsAgentRunning" /> from the previous agent event.
    /// </summary>
    public bool WasRunning { get; init; }

    /// <summary>Whether the user has requested to quit the interactive loop.</summary>
    public bool ShouldQuit { get; init; }

    // ── Input ─────────────────────────────────────────────────────────────

    /// <summary>Editable prompt state (text + history navigation).</summary>
    public InputModel Input { get; init; } = InputModel.Empty;

    /// <summary>Which region currently owns the keyboard (drives highlight + routing).</summary>
    public FocusMode Focus { get; init; } = FocusMode.Input;

    // ── Scroll / Viewport ─────────────────────────────────────────────────

    /// <summary>History scroll-back offset (0 = pinned to newest line).</summary>
    public int ScrollOffset { get; init; }

    /// <summary>Number of history rows currently visible (reported by the renderer).</summary>
    public int ViewportLines { get; init; }

    /// <summary>Total number of wrapped history rows (reported by the renderer).</summary>
    public int TotalLines { get; init; }

    /// <summary>
    ///     How far the history is scrolled, as a percentage (0 = bottom/live, 100 = top).
    /// </summary>
    public int ScrollPercent
    {
        get
        {
            int max = Math.Max(0, TotalLines - ViewportLines);
            if (max == 0) return 0;
            return (int)Math.Round(100.0 * ScrollOffset / max);
        }
    }

    // ── Panels ────────────────────────────────────────────────────────────

    /// <summary>Per-panel runtime state.</summary>
    public ImmutableDictionary<string, TuiPanelState> PanelStates { get; init; }
        = ImmutableDictionary<string, TuiPanelState>.Empty;

    /// <summary>Per-panel size override (rows or cols). 0 = use the provider's DefaultSize.</summary>
    public ImmutableDictionary<string, int> PanelSizes { get; init; }
        = ImmutableDictionary<string, int>.Empty;

    /// <summary>Id of the panel currently owning keyboard focus, or null when chat/input owns focus.</summary>
    public string? FocusedPanelId { get; init; }

    /// <summary>Registered panel ids in registration order.</summary>
    public ImmutableArray<string> RegisteredPanelIds { get; init; }
        = ImmutableArray<string>.Empty;

    // ── Shell / Drawer ────────────────────────────────────────────────────

    /// <summary>Active right-drawer tab (None | Files | Terminal | History).</summary>
    public string ActiveDrawerTab { get; init; } = "None";

    // ── Streaming buffers ─────────────────────────────────────────────────

    /// <summary>Current streaming text buffer for the active assistant message.</summary>
    public string StreamingBuffer { get; init; } = string.Empty;

    /// <summary>Current thinking buffer for the active assistant message.</summary>
    public string ThinkingBuffer { get; init; } = string.Empty;

    /// <summary>
    ///     Streaming-text deltas not yet concatenated into
    ///     <see cref="StreamingBuffer" /> (flush policy: <see cref="StreamingSync" />).
    /// </summary>
    public ChunkedBuffer PendingStreamText { get; init; } = ChunkedBuffer.Empty;

    /// <summary>
    ///     Thinking deltas not yet concatenated into
    ///     <see cref="ThinkingBuffer" /> (flush policy: <see cref="StreamingSync" />).
    /// </summary>
    public ChunkedBuffer PendingStreamThink { get; init; } = ChunkedBuffer.Empty;

    // ── Chrome ────────────────────────────────────────────────────────────

    /// <summary>Application chrome state (navigation, modals, toasts).</summary>
    public ChromeState? Chrome { get; init; }

    // ── Nested types ──────────────────────────────────────────────────────

    /// <summary>
    ///     Chat-specific state for projection and renderer concerns.
    /// </summary>
    public sealed record ChatState
    {
        /// <summary>The full transcript, oldest first.</summary>
        public ImmutableArray<ChatLine> Lines { get; init; } = ImmutableArray<ChatLine>.Empty;

        /// <summary>Whether a message is actively streaming right now.</summary>
        public bool IsStreaming { get; init; }

        /// <summary>Whether the model is currently emitting thinking tokens.</summary>
        public bool IsThinking { get; init; }

        /// <summary>Whether the agent is currently running a prompt.</summary>
        public bool IsAgentRunning { get; init; }

        /// <summary>Current streaming text buffer.</summary>
        public string StreamingBuffer { get; init; } = string.Empty;

        /// <summary>Human-readable status message for the chat header.</summary>
        public string StatusMessage { get; init; } = string.Empty;

        /// <summary>Active tool calls for the current turn.</summary>
        public ImmutableArray<ToolCall> ToolCalls { get; init; } = ImmutableArray<ToolCall>.Empty;

        /// <summary>Pull-to-load progress (0.0 – 1.0), or 0.0 when idle.</summary>
        public double PullProgress { get; init; }

        /// <summary>Current pull offset (message count or byte offset).</summary>
        public long PullOffset { get; init; }

        /// <summary>Whether older messages can be loaded.</summary>
        public bool CanLoadOlder { get; init; }

        /// <summary>Whether to show the pull-to-load indicator.</summary>
        public bool ShowPullIndicator { get; init; }

        /// <summary>Content zoom scale (1.0 = default).</summary>
        public double ContentScale { get; init; } = 1.0;
    }

    /// <summary>
    ///     Application chrome state: session identity, navigation, modals, toasts.
    /// </summary>
    public sealed record ChromeState
    {
        /// <summary>Id of the currently active session, or null if none.</summary>
        public SessionId? ActiveSessionId { get; init; }

        /// <summary>Navigation history stack. Top of stack is the current route.</summary>
        public ImmutableStack<Route> NavigationStack { get; init; } = ImmutableStack<Route>.Empty;

        /// <summary>Currently active modal, or null if no modal is shown.</summary>
        public Modal? ActiveModal { get; init; }

        /// <summary>Active toast notifications, oldest first.</summary>
        public ImmutableArray<Toast> Toasts { get; init; } = ImmutableArray<Toast>.Empty;
    }

    /// <summary>
    ///     Discriminated union of navigation routes.
    /// </summary>
    public abstract record Route
    {
        /// <summary>Chat view for the given session.</summary>
        /// <param name="SessionId">The session to display.</param>
        public sealed record Chat(SessionId SessionId) : Route;

        /// <summary>Settings view.</summary>
        public sealed record Settings : Route;

        /// <summary>Agent log view.</summary>
        public sealed record AgentLog : Route;

        /// <summary>Provider picker view.</summary>
        public sealed record ProviderPicker : Route;

        /// <summary>Onboarding view.</summary>
        public sealed record Onboarding : Route;
    }

    /// <summary>
    ///     Discriminated union of modal dialogs.
    /// </summary>
    public abstract record Modal
    {
        /// <summary>Confirmation dialog.</summary>
        /// <param name="Title">Dialog title.</param>
        /// <param name="Message">Dialog message.</param>
        /// <param name="OnConfirm">Action id to dispatch on confirmation.</param>
        public sealed record Confirm(string Title, string Message, string OnConfirm) : Modal;

        /// <summary>Alert dialog.</summary>
        /// <param name="Title">Dialog title.</param>
        /// <param name="Message">Dialog message.</param>
        public sealed record Alert(string Title, string Message) : Modal;
    }

    /// <summary>
    ///     Toast notification.
    /// </summary>
    /// <param name="Message">Toast message text.</param>
    /// <param name="Severity">Toast severity level.</param>
    /// <param name="CreatedAt">UTC timestamp when the toast was created.</param>
    /// <param name="Id">Stable unique identifier for the toast.</param>
    public sealed record Toast(string Message, ToastSeverity Severity, DateTimeOffset CreatedAt, string Id);

    /// <summary>Toast severity levels.</summary>
    public enum ToastSeverity
    {
        Info,
        Success,
        Warning,
        Error
    }

    /// <summary>
    ///     One tool call in the current turn, projected for the UI.
    /// </summary>
    /// <param name="Id">Stable tool call identifier.</param>
    /// <param name="ToolName">Name of the invoked tool.</param>
    /// <param name="Args">Raw JSON arguments.</param>
    public sealed record ToolCall(string Id, string ToolName, string Args);
}
