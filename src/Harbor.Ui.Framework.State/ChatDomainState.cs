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

    /// <summary>Whether the session list is currently loading.</summary>
    public bool IsLoading { get; init; }

    public static readonly ChatDomainState Empty = new();

    /// <summary>
    ///     Append a line to the transcript, returning a new immutable snapshot.
    ///     Avoids allocating an intermediate list.
    /// </summary>
    public ChatDomainState AddLine(ChatRole role, string text, string? toolCallId = null) =>
        this with { Lines = Lines.Add(new ChatLine(role, text, toolCallId)) };

    /// <summary>Replace a line at the given index (used only for in-place edits if needed).</summary>
    public ChatDomainState SetLine(int index, ChatRole role, string text)
    {
        if (index < 0 || index >= Lines.Length)
            return this;
        var builder = Lines.ToBuilder();
        builder[index] = new ChatLine(role, text);
        return this with { Lines = builder.MoveToImmutable() };
    }
}
