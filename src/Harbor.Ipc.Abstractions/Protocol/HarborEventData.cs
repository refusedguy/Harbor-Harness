using MessagePack;

namespace Harbor.Ipc.Protocol;

/// <summary>
///     MessagePack [Union] for <see cref="Harbor.HarborEvent" />. Declared
///     separately so non-.NET clients can mirror just this small tag table
///     instead of the full <c>AgentEvent</c> hierarchy.
/// </summary>
/// <remarks>
///     <para>
///         The integer tags here MUST match the
///         <see cref="Harbor.HarborEventKind" /> enum values exactly.
///     </para>
/// </remarks>
[MessagePackObject]
[Union(0, typeof(HarborEventAgentStarted))]
[Union(1, typeof(HarborEventMessageUpdate))]
[Union(2, typeof(HarborEventMessageEnd))]
[Union(3, typeof(HarborEventToolStart))]
[Union(4, typeof(HarborEventToolEnd))]
[Union(5, typeof(HarborEventTurnStart))]
[Union(6, typeof(HarborEventTurnEnd))]
[Union(7, typeof(HarborEventAgentEnded))]
[Union(8, typeof(HarborEventAgentError))]
[Union(9, typeof(HarborEventCompactionStarted))]
[Union(10, typeof(HarborEventCompactionCompleted))]
public abstract record HarborEventData;

/// <summary>Wire DTO for <see cref="Harbor.HarborEvent.AgentStarted" />.</summary>
[MessagePackObject]
public sealed record HarborEventAgentStarted(
    [property: Key(0)] string SessionId) : HarborEventData;

/// <summary>Wire DTO for <see cref="Harbor.HarborEvent.MessageUpdate" />.</summary>
[MessagePackObject]
public sealed record HarborEventMessageUpdate(
    [property: Key(0)] byte[]? PartialBytes,
    [property: Key(1)] string Delta) : HarborEventData;

/// <summary>Wire DTO for <see cref="Harbor.HarborEvent.MessageEnd" />.</summary>
[MessagePackObject]
public sealed record HarborEventMessageEnd(
    [property: Key(0)] byte[]? FinalBytes) : HarborEventData;

/// <summary>Wire DTO for <see cref="Harbor.HarborEvent.ToolStart" />.</summary>
[MessagePackObject]
public sealed record HarborEventToolStart(
    [property: Key(0)] string ToolCallId,
    [property: Key(1)] string ToolName) : HarborEventData;

/// <summary>Wire DTO for <see cref="Harbor.HarborEvent.ToolEnd" />.</summary>
[MessagePackObject]
public sealed record HarborEventToolEnd(
    [property: Key(0)] string ToolCallId,
    [property: Key(1)] byte[]? ResultBytes) : HarborEventData;

/// <summary>Wire DTO for <see cref="Harbor.HarborEvent.TurnStart" />.</summary>
[MessagePackObject]
public sealed record HarborEventTurnStart(
    [property: Key(0)] int Turn) : HarborEventData;

/// <summary>Wire DTO for <see cref="Harbor.HarborEvent.TurnEnd" />.</summary>
[MessagePackObject]
public sealed record HarborEventTurnEnd(
    [property: Key(0)] int Turn) : HarborEventData;

/// <summary>Wire DTO for <see cref="Harbor.HarborEvent.AgentEnded" />.</summary>
[MessagePackObject]
public sealed record HarborEventAgentEnded(
    [property: Key(0)] string SessionId) : HarborEventData;

/// <summary>Wire DTO for <see cref="Harbor.HarborEvent.AgentError" />.</summary>
[MessagePackObject]
public sealed record HarborEventAgentError(
    [property: Key(0)] string Message) : HarborEventData;

/// <summary>Wire DTO for <see cref="Harbor.HarborEvent.CompactionStarted" />.</summary>
[MessagePackObject]
public sealed record HarborEventCompactionStarted(
    [property: Key(0)] string SessionId) : HarborEventData;

/// <summary>Wire DTO for <see cref="Harbor.HarborEvent.CompactionCompleted" />.</summary>
[MessagePackObject]
public sealed record HarborEventCompactionCompleted(
    [property: Key(0)] string SessionId,
    [property: Key(1)] int Pruned,
    [property: Key(2)] int Saved) : HarborEventData;
