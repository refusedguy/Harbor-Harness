using System.Collections.Immutable;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     Harbor chat-domain state: transcript, streaming buffers, agent lifecycle,
///     costs and sessions. Lives next to the generic <see cref="TerminalUiState" />
///     inside <see cref="UiState" /> so the framework core stays shippable
///     without AI concerns.
/// </summary>
public sealed record ChatDomainState
{
    /// <summary>The full transcript (user/assistant/tool/… lines), oldest first.</summary>
    public ImmutableArray<ChatLine> Lines { get; init; } = ImmutableArray<ChatLine>.Empty;

    /// <summary>
    ///     Structured tool invocations, in call order — what renderers read to draw
    ///     a tool card (#680).
    /// </summary>
    /// <remarks>
    ///     <see cref="Lines" /> holds the RENDERED transcript, where a tool start is
    ///     the single string <c>"→ edit {…}"</c>. It is a display format, and the two
    ///     were being used interchangeably: the chat view-model parsed that string
    ///     back to recover the name and arguments, and in doing so dropped the diff
    ///     payload. This is the structure, carried beside the display text rather
    ///     than recovered from it.
    /// </remarks>
    public ImmutableArray<ToolCallSnapshot> ToolCalls { get; init; } = ImmutableArray<ToolCallSnapshot>.Empty;

    /// <summary>Live streaming message (text + thinking) for the current turn.</summary>
    public ActiveMessage Active { get; init; } = ActiveMessage.Empty;

    /// <summary>
    ///     Text deltas not yet concatenated into
    ///     <see cref="ActiveMessage.TextBuffer" /> (flush policy: <see cref="StreamingSync" />).
    /// </summary>
    public ChunkedBuffer PendingStreamText { get; init; } = ChunkedBuffer.Empty;

    /// <summary>
    ///     Thinking deltas not yet concatenated into
    ///     <see cref="ActiveMessage.ThinkBuffer" /> (flush policy: <see cref="StreamingSync" />).
    /// </summary>
    public ChunkedBuffer PendingStreamThink { get; init; } = ChunkedBuffer.Empty;

    /// <summary>Whether a message is actively streaming right now.</summary>
    public bool IsStreaming { get; init; }

    /// <summary>Human-readable status: idle / running / compacting / error.</summary>
    public string Status { get; init; } = "idle";

    /// <summary>
    ///     The session's status, as a DOMAIN FACT (#687). Distinct from
    ///     <see cref="Status" />, which is the status-bar's own string and
    ///     carries states that are not session states at all (<c>"compacting"</c>).
    ///     <para>
    ///         It is written by exactly one seam — <see cref="ChatAppReducer" />,
    ///         on the transition that establishes it: the core publishes
    ///         <c>AgentErrorEvent</c> for a failure, <c>AgentEndEvent</c> with
    ///         <c>Cancelled</c> for an abort, and a clean <c>AgentEndEvent</c> for
    ///         a finish. Nothing downstream re-derives it.
    ///     </para>
    ///     <para>
    ///         Before #687 the presentation layer answered this question a second
    ///         time by asking which role the transcript's LAST line had. That is a
    ///         second opinion about a run the core already knows the end of, and
    ///         the two do not have to agree: a run that ended in
    ///         <c>AgentErrorEvent</c> can still be sitting on an assistant line,
    ///         so the heuristic repainted failed runs green and overwrote the
    ///         <see cref="Abstractions.Models.SessionStatus.Error" /> a lifecycle
    ///         transition had just set.
    ///     </para>
    /// </summary>
    public SessionStatus SessionStatus { get; init; } = SessionStatus.Idle;

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
    ///     Snapshot of <see cref="IsAgentRunning" /> from the previous agent event
    ///     (AgentStart/AgentEnd). Lets renderers detect the rising edge
    ///     (<c>IsAgentRunning &amp;&amp; !WasRunning</c>) without keeping local mutable
    ///     state — TEA compliance (§FP-005).
    /// </summary>
    public bool WasRunning { get; init; }

    /// <summary>Visible sessions projected for the session-list view.</summary>
    public ImmutableArray<SessionInfo> Sessions { get; init; } = ImmutableArray<SessionInfo>.Empty;

    /// <summary>Id of the currently active session, or null if none.</summary>
    public SessionId? ActiveSessionId { get; init; }

    /// <summary>
    ///     Screenshot-markup overlay session (#400 slice 1/2). Closed until the
    ///     host dispatches <see cref="ChatAppMsg.OpenMarkup" /> over an image
    ///     row; transitions live in <see cref="ChatAppReducer" />. The CellForge
    ///     overlay paints this snapshot — it keeps no session of its own.
    /// </summary>
    public MarkupOverlayState Markup { get; init; } = MarkupOverlayState.Closed;

    /// <summary>
    ///     Open tabs in tab order plus the focused tab (#388). Distinct from
    ///     <see cref="Sessions" />: that is every session the store knows about,
    ///     this is the subset that is open right now. Transitions live in
    ///     <see cref="ChatAppReducer" /> (<c>OpenTab</c> / <c>ActivateTab</c> /
    ///     <c>CloseTab</c> / <c>ReorderTab</c> / …) — nothing mutates it here.
    /// </summary>
    public TabStripState TabStrip { get; init; } = TabStripState.Empty;

    /// <summary>Whether the session list is currently loading.</summary>
    public bool IsLoading { get; init; }

    /// <summary>
    ///     Classified diagnostics, pushed in by the host from the headless core
    ///     (<c>Harbor.Application.Diagnostics.DiagnosticsAggregator</c>) through
    ///     <see cref="ChatAppMsg.SyncDiagnostics" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         These arrive ALREADY CLASSIFIED. The UI framework never inspects a
    ///         tool's output to decide that a line is an error, and never asks a
    ///         language server anything: it reads this array and draws it. That is
    ///         the whole contract, and #674 is why it is written down — the
    ///         projection layer used to parse the <c>bash</c> tool's output with
    ///         its own regexes and present the result as diagnostics, in the same
    ///         panel as (and instead of) the language server's.
    ///     </para>
    ///     <para>
    ///         <see cref="DiagnosticIssue.Source" /> keeps the two producers apart,
    ///         so a renderer sections them instead of summing them into one
    ///         heading. The array is empty until a language server publishes
    ///         something or a tool prints a log worth reading — which is an honest
    ///         «nothing yet», not a missing connection.
    ///     </para>
    /// </remarks>
    public ImmutableArray<DiagnosticIssue> Diagnostics { get; init; } = ImmutableArray<DiagnosticIssue>.Empty;

    public static readonly ChatDomainState Empty = new();

    /// <summary>
    ///     Append a line to the transcript, returning a new immutable snapshot.
    ///     Avoids allocating an intermediate list.
    /// </summary>
    public ChatDomainState AddLine(ChatRole role, string text, string? toolCallId = null) =>
        this with { Lines = Lines.Add(new ChatLine(role, text, toolCallId)) };

    /// <summary>
    ///     Record a tool invocation, replacing any earlier snapshot with the same
    ///     id (#680). A pre-execution placeholder and the real start event share
    ///     one <c>ToolCallId</c>, so upsert — not append — is what keeps the list
    ///     one entry per call.
    /// </summary>
    public ChatDomainState PutToolCall(ToolCallSnapshot call)
    {
        var builder = ImmutableArray.CreateBuilder<ToolCallSnapshot>(ToolCalls.Length);
        bool replaced = false;
        foreach (ToolCallSnapshot existing in ToolCalls)
        {
            if (existing.Id == call.Id)
            {
                builder.Add(call);
                replaced = true;
            }
            else
            {
                builder.Add(existing);
            }
        }

        if (!replaced)
        {
            builder.Add(call);
        }

        return this with { ToolCalls = builder.ToImmutable() };
    }

    /// <summary>
    ///     Close an in-flight call with its result. An id we have not seen is
    ///     ignored: an end event with no start carries no name, no glyph and no
    ///     arguments, and inventing a card from it would show the user an empty
    ///     one.
    /// </summary>
    public ChatDomainState CompleteToolCall(string toolCallId, ToolCallState status, string resultPreview)
    {
        var builder = ImmutableArray.CreateBuilder<ToolCallSnapshot>(ToolCalls.Length);
        bool changed = false;
        foreach (ToolCallSnapshot existing in ToolCalls)
        {
            if (existing.Id == toolCallId)
            {
                builder.Add(existing with { Status = status, ResultPreview = resultPreview });
                changed = true;
            }
            else
            {
                builder.Add(existing);
            }
        }

        return changed ? this with { ToolCalls = builder.ToImmutable() } : this;
    }

    /// <summary>
    ///     Replace a line at the given index (used only for in-place edits if needed).
    ///     The replaced line's <c>ToolCallId</c>/<c>MessageId</c>/<c>TimestampUtc</c>
    ///     are preserved (issue #94): they are the single key joining transcript
    ///     lines with tool cards, and an in-place text edit must not break the join.
    /// </summary>
    public ChatDomainState SetLine(int index, ChatRole role, string text)
    {
        if (index < 0 || index >= Lines.Length)
            return this;
        var prev = Lines[index];
        var builder = Lines.ToBuilder();
        builder[index] = new ChatLine(role, text, prev.ToolCallId, prev.MessageId, prev.TimestampUtc);
        return this with { Lines = builder.MoveToImmutable() };
    }
}
