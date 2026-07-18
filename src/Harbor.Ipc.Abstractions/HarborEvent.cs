using Harbor.Abstractions.Models;
using Harbor.Abstractions.Tools;

namespace Harbor.Ipc;

/// <summary>
///     Discriminated union of streaming events delivered to
///     <see cref="IHarborClient.SubscribeToEventsAsync" />.
/// </summary>
/// <remarks>
///     <para>
///         This is a <b>simplified, wire-stable</b> projection of the richer
///         <c>AgentEvent</c> hierarchy in <c>Harbor.Abstractions.Events</c>.
///         The in-process client maps <c>AgentEvent</c> → <see cref="HarborEvent" />
///         at the boundary; the IPC server does the same before serializing
///         to MessagePack.
///     </para>
///     <para>
///         Keeping the wire union small and stable means IPC clients written
///         in other languages (Python, JS, Rust, Go) don't need to mirror the
///         full 13-case <c>AgentEvent</c> hierarchy — they only need these
///         11 cases.
///     </para>
/// </remarks>
public abstract record HarborEvent
{
    /// <summary>
    ///     Discriminator tag — used by MessagePack [Union] and by non-.NET
    ///     clients to switch on event type.
    /// </summary>
    public abstract HarborEventKind Kind { get; }

    /// <summary>The agent run started for the given session.</summary>
    public sealed record AgentStarted(string SessionId) : HarborEvent
    {
        /// <inheritdoc />
        public override HarborEventKind Kind => HarborEventKind.AgentStarted;
    }

    /// <summary>A partial assistant message arrived (streaming delta).</summary>
    public sealed record MessageUpdate(AssistantMessage Partial, string Delta) : HarborEvent
    {
        /// <inheritdoc />
        public override HarborEventKind Kind => HarborEventKind.MessageUpdate;
    }

    /// <summary>The assistant message was finalized.</summary>
    public sealed record MessageEnd(AssistantMessage Final) : HarborEvent
    {
        /// <inheritdoc />
        public override HarborEventKind Kind => HarborEventKind.MessageEnd;
    }

    /// <summary>A tool call started executing.</summary>
    public sealed record ToolStart(string ToolCallId, string ToolName) : HarborEvent
    {
        /// <inheritdoc />
        public override HarborEventKind Kind => HarborEventKind.ToolStart;
    }

    /// <summary>A tool call completed (success or failure).</summary>
    public sealed record ToolEnd(string ToolCallId, ToolResult Result) : HarborEvent
    {
        /// <inheritdoc />
        public override HarborEventKind Kind => HarborEventKind.ToolEnd;
    }

    /// <summary>A new turn started (1-based turn index).</summary>
    public sealed record TurnStart(int Turn) : HarborEvent
    {
        /// <inheritdoc />
        public override HarborEventKind Kind => HarborEventKind.TurnStart;
    }

    /// <summary>A turn ended.</summary>
    public sealed record TurnEnd(int Turn) : HarborEvent
    {
        /// <inheritdoc />
        public override HarborEventKind Kind => HarborEventKind.TurnEnd;
    }

    /// <summary>The agent run completed for the given session.</summary>
    public sealed record AgentEnded(string SessionId) : HarborEvent
    {
        /// <inheritdoc />
        public override HarborEventKind Kind => HarborEventKind.AgentEnded;
    }

    /// <summary>An unrecoverable agent error occurred.</summary>
    public sealed record AgentError(string Message) : HarborEvent
    {
        /// <inheritdoc />
        public override HarborEventKind Kind => HarborEventKind.AgentError;
    }

    /// <summary>Compaction started for the given session.</summary>
    public sealed record CompactionStarted(string SessionId) : HarborEvent
    {
        /// <inheritdoc />
        public override HarborEventKind Kind => HarborEventKind.CompactionStarted;
    }

    /// <summary>Compaction completed; <paramref name="Pruned"/> messages folded, <paramref name="Saved"/> tokens reclaimed.</summary>
    public sealed record CompactionCompleted(string SessionId, int Pruned, int Saved) : HarborEvent
    {
        /// <inheritdoc />
        public override HarborEventKind Kind => HarborEventKind.CompactionCompleted;
    }
}

/// <summary>
///     Discriminator enum for <see cref="HarborEvent" />. The integer value
///     of each member matches its <c>[Union]</c> tag in the MessagePack
///     serialization — keep them in lockstep.
/// </summary>
public enum HarborEventKind
{
    /// <summary>See <see cref="HarborEvent.AgentStarted" />.</summary>
    AgentStarted = 0,

    /// <summary>See <see cref="HarborEvent.MessageUpdate" />.</summary>
    MessageUpdate = 1,

    /// <summary>See <see cref="HarborEvent.MessageEnd" />.</summary>
    MessageEnd = 2,

    /// <summary>See <see cref="HarborEvent.ToolStart" />.</summary>
    ToolStart = 3,

    /// <summary>See <see cref="HarborEvent.ToolEnd" />.</summary>
    ToolEnd = 4,

    /// <summary>See <see cref="HarborEvent.TurnStart" />.</summary>
    TurnStart = 5,

    /// <summary>See <see cref="HarborEvent.TurnEnd" />.</summary>
    TurnEnd = 6,

    /// <summary>See <see cref="HarborEvent.AgentEnded" />.</summary>
    AgentEnded = 7,

    /// <summary>See <see cref="HarborEvent.AgentError" />.</summary>
    AgentError = 8,

    /// <summary>See <see cref="HarborEvent.CompactionStarted" />.</summary>
    CompactionStarted = 9,

    /// <summary>See <see cref="HarborEvent.CompactionCompleted" />.</summary>
    CompactionCompleted = 10,
}
