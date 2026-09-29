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
///     Running cost/token accounting for the session status line. The cost
///     figures are copied from the core's <c>SessionStatsEvent</c> — the UI
///     framework displays them and never forms one (#653); the context figure is
///     the per-request one the core publishes on every step (#651).
/// </summary>
/// <param name="TokensIn">
///     <b>Paid</b> — the cumulative input tokens, i.e. the sum of every
///     request's FULL input. This is what an uncached provider bills (it re-reads
///     the whole prompt every turn), so on turn N it is roughly N × the context,
///     and it is not what a person means by "how full is my context". The status
///     cell shows <paramref name="ContextTokens" /> instead; the per-turn bar
///     chart and the token-breakdown panel report the bill and keep this one
///     (#651).
/// </param>
/// <param name="TokensOut">Cumulative output tokens — never re-read, so a real total.</param>
/// <param name="CostUsd">
///     Cumulative cost in USD as the core priced it, or a lower bound when
///     <paramref name="IsCostUnpriced" /> is <see langword="true" />.
/// </param>
/// <param name="IsCostUnpriced">
///     The core could NOT price the session — the model publishes no rate table,
///     so <paramref name="CostUsd" /> is a floor. The cost cell then renders as
///     an em dash, because a zero there reads as "free" and is false for a paid
///     provider whose catalogue entry carries no rates.
///     <para>
///         <b>Polarity is deliberate and load-bearing:</b> the flag is
///         "unpriced", never "priced", because a struct's zero value is
///         <c>default(CostSnapshot)</c> — the value every state that never
///         mentions a cost carries (<c>ChatDomainState.Empty</c>, a fresh
///         <c>UiState</c>, a test's hand-built state). A positive flag would make
///         "no data at all" render as "price unknown" on every screen in the
///         product, which is how the first version of #653 turned a status line
///         into "—" in the golden frames. Zero must mean "renders exactly as it
///         did before the flag existed".
///     </para>
/// </param>
/// <param name="ContextTokens">
///     <b>Occupied</b> — the prompt tokens of the request the provider last
///     accepted, i.e. the context this session currently occupies. It is the
///     figure the ctx bar has read since #630, and the one a person means by
///     "how full is my context"; the paid sum is a bill, not a window. Stays 0
///     until this process has seen a request: a message history records tokens,
///     never the size of a request, so a restored session cannot reconstruct it
///     — and <see cref="ContextUsage.DisplayedInputTokens" /> degrades to
///     <paramref name="TokensIn" /> rather than claiming an empty context.
/// </param>
public readonly record struct CostSnapshot(
    long TokensIn,
    long TokensOut,
    decimal CostUsd,
    bool IsCostUnpriced = false,
    long ContextTokens = 0);

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

    // Backing field for the store-owned revision. It has exactly one writer —
    // SetRevision below — so the init accessor and UiStore cannot disagree
    // about when a revision may be written (#491).
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
        init => SetRevision(value);
    }

    /// <summary>
    ///     The only writer of <see cref="Revision" /> (#491). <see cref="UiStore" />
    ///     uses it to stamp the reducer's fresh snapshot in place, which is what
    ///     keeps dispatch at a single state copy: re-cloning the snapshot
    ///     (<c>next with { Revision = … }</c>) allocated a second full
    ///     <see cref="UiState" /> on the hottest path in the TUI — one dispatch
    ///     per token, keystroke and tool event.
    /// </summary>
    /// <remarks>
    ///     <b>Contract:</b> only legal before the snapshot is published.
    ///     <see cref="UiStore.Dispatch" /> stamps before its compare-exchange, so
    ///     no reader can observe the pre-stamp value; writing a published
    ///     snapshot would rewrite history that subscribers already hold. The
    ///     reducers keep that safe by being pure folds over their single
    ///     <c>state</c> argument: every arm either allocates a fresh snapshot or
    ///     returns the input, and a returned input is caught by the store's
    ///     reference-equality short-circuit before the stamp.
    ///     <c>StoreDispatchAllocTests</c> pins the observable half of it.
    /// </remarks>
    internal void SetRevision(long revision) => _revision = revision;

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
                // A clear-screen drops what was said, not what happened to the
                // run: the status the reducer decided survives it (#687).
                SessionStatus = chat.SessionStatus,
                Sessions = chat.Sessions,
                ActiveSessionId = chat.ActiveSessionId,
                IsLoading = chat.IsLoading,
                // Diagnostics are workspace chrome, not transcript: a
                // clear-screen drops what was said, not what is broken (#674).
                Diagnostics = chat.Diagnostics,
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
