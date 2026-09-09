// UiState is deprecated, use AppState. Kept for backward compatibility during migration.
using System.Collections.Immutable;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Ui.Framework.Panels;
namespace Harbor.Ui.Framework.State;

// TODO(principles)[SRP]: finish the split — remove the legacy flat forwarding
// getters below once all readers use Ui/Chat directly (tracked for the cleanup
// PR after this branch merges; reads stay source-compatible until then).

/// <summary>
///     Currently-streaming assistant message. Mutable deltas are folded into
///     <see cref="UiState.Lines" /> on <see cref="MessageEndEvent" />.
/// </summary>
public sealed record ActiveMessage(
    string TextBuffer,
    string ThinkBuffer)
{
    public static readonly ActiveMessage Empty = new(string.Empty, string.Empty);
}

/// <summary>
///     Running cost/token accounting for the session status line.
/// </summary>
/// <param name="TokensIn">Cumulative input tokens.</param>
/// <param name="TokensOut">Cumulative output tokens.</param>
/// <param name="CostUsd">Cumulative estimated cost in USD.</param>
public readonly record struct CostSnapshot(
    long TokensIn,
    long TokensOut,
    decimal CostUsd);

/// <summary>
///     Renderer-agnostic, immutable UI snapshot. The single source of truth that
///     every interactive renderer projects from. Produced only by
///     <see cref="UiReducer" /> — never mutated inside a renderer.
/// </summary>
/// <remarks>
///     <para>
///         Composed of generic terminal state (<see cref="Ui" />) and the Harbor
///         chat domain (<see cref="Chat" />) so the framework core stays shippable
///         without AI concerns. Flat getters below forward to the parts for
///         source compatibility — new code reads <c>Ui</c>/<c>Chat</c> directly.
///     </para>
///     <para>
///         Designed for NativeAOT and zero-reflection: all members are value types
///         or <see cref="ImmutableArray{T}" /> (no <see cref="List{T}" />, no
///         reflection-based binding). Renderers read it on each frame and build
///         their framework-specific widgets from it.
///     </para>
/// </remarks>
public sealed record UiState
{
    /// <summary>Generic terminal-UI state (input, scroll, focus, panels, quit).</summary>
    public TerminalUiState Ui { get; init; } = TerminalUiState.Empty;

    /// <summary>Harbor chat-domain state (transcript, streaming, agent, costs, sessions).</summary>
    public ChatDomainState Chat { get; init; } = ChatDomainState.Empty;

    // ── Legacy flat forwarding getters (read-only; writes go through Ui/Chat) ──

    /// <summary>The full transcript (user/assistant/tool/… lines), oldest first.</summary>
    public ImmutableArray<ChatLine> Lines { get => Chat.Lines; init => Chat = Chat with { Lines = value }; }

    /// <summary>Live streaming message (text + thinking) for the current turn.</summary>
    public ActiveMessage Active { get => Chat.Active; init => Chat = Chat with { Active = value }; }

    /// <summary>
    ///     Text deltas not yet concatenated into
    ///     <see cref="Active.TextBuffer" /> (flush policy: <see cref="StreamingSync" />).
    /// </summary>
    public ChunkedBuffer PendingStreamText { get => Chat.PendingStreamText; init => Chat = Chat with { PendingStreamText = value }; }

    /// <summary>
    ///     Thinking deltas not yet concatenated into
    ///     <see cref="Active.ThinkBuffer" /> (flush policy: <see cref="StreamingSync" />).
    /// </summary>
    public ChunkedBuffer PendingStreamThink { get => Chat.PendingStreamThink; init => Chat = Chat with { PendingStreamThink = value }; }

    /// <summary>Whether a message is actively streaming right now.</summary>
    public bool IsStreaming { get => Chat.IsStreaming; init => Chat = Chat with { IsStreaming = value }; }

    /// <summary>Human-readable status: idle / running / compacting / error.</summary>
    public string Status { get => Chat.Status; init => Chat = Chat with { Status = value }; }

    /// <summary>Running token/cost accounting for the status line.</summary>
    public CostSnapshot Cost { get => Chat.Cost; init => Chat = Chat with { Cost = value }; }

    /// <summary>Active model id (for the header/status).</summary>
    public string Model { get => Chat.Model; init => Chat = Chat with { Model = value }; }

    /// <summary>Active provider id (for the header/status).</summary>
    public string Provider { get => Chat.Provider; init => Chat = Chat with { Provider = value }; }

    /// <summary>Active agent name (for the header/status).</summary>
    public string AgentName { get => Chat.AgentName; init => Chat = Chat with { AgentName = value }; }

    /// <summary>Whether the agent is currently running a prompt.</summary>
    public bool IsAgentRunning { get => Chat.IsAgentRunning; init => Chat = Chat with { IsAgentRunning = value }; }

    /// <summary>
    ///     Snapshot of <see cref="IsAgentRunning" /> from the previous agent event
    ///     (AgentStart/AgentEnd). Lets renderers detect the rising edge
    ///     (<c>IsAgentRunning &amp;&amp; !WasRunning</c>) without keeping local mutable
    ///     state — TEA compliance (§FP-005).
    /// </summary>
    public bool WasRunning { get => Chat.WasRunning; init => Chat = Chat with { WasRunning = value }; }

    /// <summary>Whether the user has requested to quit the interactive loop.</summary>
    public bool ShouldQuit { get => Ui.ShouldQuit; init => Ui = Ui with { ShouldQuit = value }; }

    /// <summary>Editable prompt state (text + history navigation).</summary>
    public InputModel Input { get => Ui.Input; init => Ui = Ui with { Input = value }; }

    /// <summary>Which region currently owns the keyboard (drives highlight + routing).</summary>
    public FocusMode Focus { get => Ui.Focus; init => Ui = Ui with { Focus = value }; }

    /// <summary>
    ///     History scroll-back offset (0 = pinned to newest line, grows toward the
    ///     top). Clamped to <c>TotalLines - ViewportLines</c> by the reducer.
    /// </summary>
    public int ScrollOffset { get => Ui.ScrollOffset; init => Ui = Ui with { ScrollOffset = value }; }

    /// <summary>Number of history rows currently visible (reported by the renderer).</summary>
    public int ViewportLines { get => Ui.ViewportLines; init => Ui = Ui with { ViewportLines = value }; }

    /// <summary>Total number of wrapped history rows (reported by the renderer).</summary>
    public int TotalLines { get => Ui.TotalLines; init => Ui = Ui with { TotalLines = value }; }

    /// <summary>How far the history is scrolled, as a percentage (0 = bottom/live, 100 = top).</summary>
    public int ScrollPercent => Ui.ScrollPercent;

    /// <summary>
    ///     Per-panel runtime state. Mirrors the <c>PanelRegistry</c>; updated by
    ///     <c>UiReducer</c> on <c>TogglePanel</c> / <c>FocusPanel</c> /
    ///     <c>ResizePanel</c>. Renderers read this to decide which panels to render.
    /// </summary>
    public ImmutableDictionary<string, TuiPanelState> PanelStates { get => Ui.PanelStates; init => Ui = Ui with { PanelStates = value }; }

    /// <summary>
    ///     Per-panel size override (rows or cols, depending on the panel's placement).
    ///     <c>0</c> = use the provider's <c>DefaultSize</c>.
    /// </summary>
    public ImmutableDictionary<string, int> PanelSizes { get => Ui.PanelSizes; init => Ui = Ui with { PanelSizes = value }; }

    /// <summary>
    ///     Id of the panel currently owning keyboard focus, or <see langword="null" />
    ///     when the chat / input box owns focus. Driven by <c>FocusPanel</c> /
    ///     <c>CyclePanelFocus</c> messages.
    /// </summary>
    public string? FocusedPanelId { get => Ui.FocusedPanelId; init => Ui = Ui with { FocusedPanelId = value }; }

    /// <summary>
    ///     Registered panel ids in registration order. Maintained by the host
    ///     (<c>PanelRegistry</c>) via <c>UiStore.Transition</c>. Read by the reducer
    ///     for <c>CyclePanelFocus</c> so it stays pure (no IRegistry dependency).
    /// </summary>
    public ImmutableArray<string> RegisteredPanelIds { get => Ui.RegisteredPanelIds; init => Ui = Ui with { RegisteredPanelIds = value }; }

    /// <summary>Visible sessions projected for the session-list view.</summary>
    public ImmutableArray<SessionInfo> Sessions { get => Chat.Sessions; init => Chat = Chat with { Sessions = value }; }

    /// <summary>Id of the currently active session, or null if none.</summary>
    public SessionId? ActiveSessionId { get => Chat.ActiveSessionId; init => Chat = Chat with { ActiveSessionId = value }; }

    /// <summary>Whether the session list is currently loading.</summary>
    public bool IsLoading { get => Chat.IsLoading; init => Chat = Chat with { IsLoading = value }; }

    /// <summary>
    ///     Append a line to the transcript, returning a new immutable snapshot.
    ///     Avoids allocating an intermediate list.
    /// </summary>
    public UiState AddLine(ChatRole role, string text, string? toolCallId = null) =>
        this with { Chat = Chat.AddLine(role, text, toolCallId) };

    /// <summary>Replace a line at the given index (used only for in-place edits if needed).</summary>
    public UiState SetLine(int index, ChatRole role, string text)
    {
        var next = Chat.SetLine(index, role, text);
        return ReferenceEquals(next, Chat) ? this : this with { Chat = next };
    }

    /// <summary>Return a snapshot with the editable input model replaced.</summary>
    public UiState SetInput(InputModel input) => this with { Ui = Ui.SetInput(input) };

    /// <summary>Return a snapshot with the keyboard focus replaced.</summary>
    public UiState SetFocus(FocusMode focus) => this with { Ui = Ui.SetFocus(focus) };

    /// <summary>
    ///     Return a snapshot with the transcript, live message, input, and scroll
    ///     state cleared. Session chrome (model/provider/agent/cost/status) is
    ///     preserved so a clear-screen does not wipe the active session identity.
    /// </summary>
    public UiState ClearTranscript() => this with
    {
        Chat = ChatDomainState.Empty with
        {
            Status = IsAgentRunning ? "running" : "idle",
            Cost = Cost,
            Model = Model,
            Provider = Provider,
            AgentName = AgentName,
            IsAgentRunning = IsAgentRunning,
            WasRunning = WasRunning,
            Sessions = Sessions,
            ActiveSessionId = ActiveSessionId,
            IsLoading = IsLoading
        },
        Ui = Ui with
        {
            Input = InputModel.Empty,
            ScrollOffset = 0,
            TotalLines = 0,
            ViewportLines = 0
        }
    };

    /// <summary>Return a snapshot with the history scroll offset clamped to valid range.</summary>
    public UiState SetScroll(int offset)
    {
        var next = Ui.SetScroll(offset);
        return ReferenceEquals(next, Ui) ? this : this with { Ui = next };
    }
}
