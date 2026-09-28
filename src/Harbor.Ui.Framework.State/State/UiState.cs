// #33/T4 (#364): the flat forwarding surface is gone. Readers go through the
// two typed parts — `Ui` for generic terminal state, `Chat` for the Harbor
// domain — so there is exactly one way to reach each value.
using System.Collections.Immutable;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
namespace Harbor.Ui.Framework.State;

/// <summary>
///     Currently-streaming assistant message. Mutable deltas are folded into
///     <see cref="ChatDomainState.Active" /> on <see cref="MessageEndEvent" />.
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
///     <see cref="AppReducer" /> (generic half) and <see cref="ChatAppReducer" />
///     (Harbor extension) — never mutated inside a renderer.
/// </summary>
/// <remarks>
///     <para>
///         Composed of exactly two typed parts: generic terminal state
///         (<see cref="Ui" />) and the Harbor chat domain (<see cref="Chat" />), so
///         the framework core stays shippable without AI concerns. There is no flat
///         forwarding surface: read <c>Ui.Input</c> / <c>Chat.Cost</c> directly.
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

    // Backing field for Revision. Written in exactly two places: the public
    // `init` accessor (so `with { Revision = … }` keeps working for callers
    // that normalize a snapshot) and the store-only mutator below (#491).
    private long _revision;

    /// <summary>
    ///     Monotonic store revision, bumped by <see cref="UiStore" /> on every
    ///     successful transition (issue #94). CAS success and <c>Changed</c>
    ///     delivery are not atomic across threads, so subscribers must consume
    ///     <c>e.State</c> (never re-read <c>store.State</c>) and drop any
    ///     notification with <c>Revision</c> not greater than the last one
    ///     applied — see <see cref="UiStateChangedEventArgs.IsStale" />.
    ///     Zero on hand-built states (tests/replays); the projector always
    ///     projects those and never treats them as stale.
    /// </summary>
    public long Revision
    {
        get => _revision;
        init => _revision = value;
    }

    /// <summary>
    ///     Stamp the revision onto a snapshot the store has <b>not yet
    ///     published</b> (#491). The store is the sole owner of revision
    ///     assignment, and it needs a single copy per dispatch: the reducer
    ///     hands back a freshly built snapshot, so re-cloning it
    ///     (<c>next with { Revision = … }</c>) allocated a second full
    ///     <see cref="UiState" /> on the hottest path in the TUI — one dispatch
    ///     per token, keystroke and tool event.
    /// </summary>
    /// <remarks>
    ///     <b>Contract:</b> only legal on an instance the caller just created
    ///     and has not published. <see cref="UiStore.Dispatch" /> stamps before
    ///     its compare-exchange, so no reader can observe the pre-stamp value;
    ///     stamping a published snapshot would rewrite history that
    ///     subscribers already hold. The reducers keep it safe by being pure
    ///     folds over their single <c>state</c> argument: every arm either
    ///     allocates a fresh snapshot or returns the input, and a returned
    ///     input is caught by the store's reference-equality short-circuit
    ///     before the stamp. <c>StoreDispatchAllocTests</c> pins the
    ///     observable half of that.
    /// </remarks>
    internal void SetRevisionUnpublished(long revision) => _revision = revision;

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
    public UiState ClearTranscript()
    {
        var chat = Chat;
        return this with
        {
            Chat = ChatDomainState.Empty with
            {
                Status = chat.IsAgentRunning ? "running" : "idle",
                Cost = chat.Cost,
                Model = chat.Model,
                Provider = chat.Provider,
                AgentName = chat.AgentName,
                IsAgentRunning = chat.IsAgentRunning,
                WasRunning = chat.WasRunning,
                Sessions = chat.Sessions,
                ActiveSessionId = chat.ActiveSessionId,
                IsLoading = chat.IsLoading,
                // The open tabs are workspace chrome, not transcript: a
                // clear-screen must not close them (#388).
                TabStrip = chat.TabStrip
            },
            Ui = Ui with
            {
                Input = InputModel.Empty,
                ScrollOffset = 0,
                TotalLines = 0,
                ViewportLines = 0
            }
        };
    }

    /// <summary>Return a snapshot with the history scroll offset clamped to valid range.</summary>
    public UiState SetScroll(int offset)
    {
        var next = Ui.SetScroll(offset);
        return ReferenceEquals(next, Ui) ? this : this with { Ui = next };
    }
}
