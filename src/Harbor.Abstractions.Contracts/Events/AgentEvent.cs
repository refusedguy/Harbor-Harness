using System.Text.Json.Serialization;
using Harbor.Abstractions.Models;
namespace Harbor.Abstractions.Events;
/// <summary>
///     Discriminated union of all events emitted by the agent loop.
///     Subscribers receive these events via <see cref="IEventBus" />.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(AgentStartEvent), "agent_start")]
[JsonDerivedType(typeof(TurnStartEvent), "turn_start")]
[JsonDerivedType(typeof(MessageStartEvent), "message_start")]
[JsonDerivedType(typeof(MessageUpdateEvent), "message_update")]
[JsonDerivedType(typeof(MessageEndEvent), "message_end")]
[JsonDerivedType(typeof(ToolExecutionStartEvent), "tool_execution_start")]
[JsonDerivedType(typeof(ToolExecutionUpdateEvent), "tool_execution_update")]
[JsonDerivedType(typeof(ToolExecutionEndEvent), "tool_execution_end")]
[JsonDerivedType(typeof(TurnEndEvent), "turn_end")]
[JsonDerivedType(typeof(AgentEndEvent), "agent_end")]
[JsonDerivedType(typeof(AgentErrorEvent), "agent_error")]
[JsonDerivedType(typeof(CompactionStartedEvent), "compaction_started")]
[JsonDerivedType(typeof(CompactionCompletedEvent), "compaction_completed")]
[JsonDerivedType(typeof(CompactionFailedEvent), "compaction_failed")]
[JsonDerivedType(typeof(SessionStatsEvent), "session_stats")]
[JsonDerivedType(typeof(SessionChangedEvent), "session_changed")]
public abstract record AgentEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record AgentStartEvent(
    string SessionId,
    IReadOnlyList<AgentMessage> Messages,
    ModelInfo? Model = null) : AgentEvent;

/// <summary>
///     Emitted at the start of each turn inside a run. <see cref="TurnIndex" /> is 1-based.
/// </summary>
public sealed record TurnStartEvent(int TurnIndex) : AgentEvent;

/// <summary>
///     Emitted when the assistant starts streaming a new message.
/// </summary>
public sealed record MessageStartEvent(AssistantMessage Message) : AgentEvent;

/// <summary>
///     Emitted on each LLM streaming delta (text token, tool call delta, etc.).
/// </summary>
public sealed record MessageUpdateEvent(LlmEvent LlmEvent, AssistantMessage Partial) : AgentEvent;

/// <summary>
///     Emitted when the assistant message is finalized.
/// </summary>
public sealed record MessageEndEvent(AssistantMessage Message) : AgentEvent;

/// <summary>
///     Emitted just before a tool call is executed.
/// </summary>
public sealed record ToolExecutionStartEvent(
    string ToolCallId,
    string ToolName,
    JsonElement Args) : AgentEvent;

/// <summary>
///     Emitted on a tool progress update. <see cref="PartialResult" /> is opaque and tool-specific.
/// </summary>
public sealed record ToolExecutionUpdateEvent(
    string ToolCallId,
    object PartialResult) : AgentEvent;

/// <summary>
///     Emitted when a tool call completes (success or failure).
/// </summary>
public sealed record ToolExecutionEndEvent(
    string ToolCallId,
    ToolResult Result,
    bool IsError) : AgentEvent;

/// <summary>
///     Emitted at the end of a turn, with the assistant message and any tool results produced.
/// </summary>
public sealed record TurnEndEvent(
    AssistantMessage AssistantMessage,
    IReadOnlyList<ToolResultMessage> ToolResults) : AgentEvent;

/// <summary>
///     Emitted when the entire agent run completes.
/// </summary>
/// <param name="NewMessages">Messages produced by the run.</param>
/// <param name="Cancelled">True when the run ended because it was cancelled, not because the agent finished.</param>
public sealed record AgentEndEvent(IReadOnlyList<AgentMessage> NewMessages, bool Cancelled = false) : AgentEvent;

/// <summary>
///     Emitted on an unrecoverable agent error.
/// </summary>
/// <param name="Message">User-facing error message.</param>
/// <param name="Exception">Optional exception details string for diagnostics.</param>
public sealed record AgentErrorEvent(string Message, string? Exception = null) : AgentEvent;

/// <summary>
///     Emitted when compaction starts for the given session.
/// </summary>
public sealed record CompactionStartedEvent(string SessionId) : AgentEvent;

/// <summary>
///     Emitted when compaction completes for the given session.
/// </summary>
/// <param name="SessionId">The compacted session.</param>
/// <param name="Summary">The generated summary text.</param>
/// <param name="PrunedMessageCount">How many messages were folded into the summary.</param>
/// <param name="TokensSaved">Estimated tokens saved by the compaction.</param>
/// <param name="Duration">Wall-clock time spent compacting.</param>
public sealed record CompactionCompletedEvent(
    string SessionId,
    string Summary,
    int PrunedMessageCount,
    int TokensSaved,
    TimeSpan Duration) : AgentEvent;

/// <summary>
///     Emitted when compaction fails and the loop falls back to truncation.
/// </summary>
/// <param name="SessionId">The session whose compaction failed.</param>
/// <param name="Error">The compaction failure description.</param>
public sealed record CompactionFailedEvent(string SessionId, string Error) : AgentEvent;

/// <summary>
///     Periodic stats snapshot for a session (cost, tokens, message count).
/// </summary>
public sealed record SessionStatsEvent(
    string SessionId,
    SessionMetadata Metadata) : AgentEvent;

/// <summary>
///     Emitted when the active session changes (user switches context).
/// </summary>
public sealed record SessionChangedEvent(string SessionId) : AgentEvent;

/// <summary>
///     Streaming event from LLM provider. Mapped to <see cref="MessageUpdateEvent" /> by agent loop.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextStartEvent), "text_start")]
[JsonDerivedType(typeof(TextDeltaEvent), "text_delta")]
[JsonDerivedType(typeof(TextEndEvent), "text_end")]
[JsonDerivedType(typeof(ThinkingStartEvent), "thinking_start")]
[JsonDerivedType(typeof(ThinkingDeltaEvent), "thinking_delta")]
[JsonDerivedType(typeof(ThinkingEndEvent), "thinking_end")]
[JsonDerivedType(typeof(ToolCallStartEvent), "tool_call_start")]
[JsonDerivedType(typeof(ToolCallDeltaEvent), "tool_call_delta")]
[JsonDerivedType(typeof(ToolCallEndEvent), "tool_call_end")]
[JsonDerivedType(typeof(StepStartEvent), "step_start")]
[JsonDerivedType(typeof(StepFinishEvent), "step_finish")]
[JsonDerivedType(typeof(FinishEvent), "finish")]
[JsonDerivedType(typeof(ErrorEvent), "error")]
public abstract record LlmEvent;

public sealed record TextStartEvent(string Id) : LlmEvent;

/// <summary>
///     A text delta — a small chunk of assistant text.
/// </summary>
public sealed record TextDeltaEvent(string Id, string Delta) : LlmEvent;

/// <summary>
///     Marks the end of a text run; <see cref="FinalText" /> is the complete text emitted.
/// </summary>
public sealed record TextEndEvent(string Id, string FinalText) : LlmEvent;

/// <summary>
///     Marks the start of a thinking/reasoning block.
/// </summary>
public sealed record ThinkingStartEvent(string Id) : LlmEvent;

/// <summary>
///     A thinking delta — a small chunk of reasoning text.
/// </summary>
public sealed record ThinkingDeltaEvent(string Id, string Delta) : LlmEvent;

/// <summary>
///     Marks the end of a thinking run.
/// </summary>
public sealed record ThinkingEndEvent(string Id, string FinalText) : LlmEvent;

/// <summary>
///     Marks the start of a tool call.
/// </summary>
public sealed record ToolCallStartEvent(string Id, string ToolName) : LlmEvent;

/// <summary>
///     A tool-call arguments delta — a small JSON fragment.
/// </summary>
public sealed record ToolCallDeltaEvent(string Id, string ArgsDelta) : LlmEvent;

/// <summary>
///     Marks the end of a tool call; <see cref="Args" /> is the parsed JSON arguments object.
/// </summary>
public sealed record ToolCallEndEvent(string Id, string ToolName, JsonElement Args) : LlmEvent;

/// <summary>
///     Marks the start of an inference step (e.g. a reasoning step on Anthropic extended thinking).
/// </summary>
public sealed record StepStartEvent(int Index) : LlmEvent;

/// <summary>
///     Marks the finish of an inference step. Carries the step's <see cref="Usage" /> if available.
/// </summary>
public sealed record StepFinishEvent(
    int Index,
    string FinishReason,
    Usage? Usage,
    string? ProviderModelId = null) : LlmEvent;

/// <summary>
///     Emitted when the entire generation is finished.
/// </summary>
public sealed record FinishEvent : LlmEvent;

/// <summary>
///     Emitted on a streaming error. <see cref="Kind" /> classifies the
///     transport failure (ROP-A ПР.5) so the retry policy can distinguish
///     transient conditions from fatal ones without parsing the message.
/// </summary>
public sealed record ErrorEvent(
    string Message,
    string? Exception = null,
    ProviderErrorKind Kind = ProviderErrorKind.Unknown,
    int? StatusCode = null) : LlmEvent;
