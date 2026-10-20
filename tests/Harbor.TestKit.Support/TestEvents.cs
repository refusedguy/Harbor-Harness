// TestEvents.cs — the ONE place a test builds an event.
//
// WHY THIS FILE EXISTS (issue #1821)
// ----------------------------------
// Before it, every event in the test tree was assembled by hand:
//
//     await bus.PublishAsync(new MessageUpdateEvent(
//         new TextDeltaEvent("id", "Answer **line**\n"),
//         AssistantMessage.Empty("s1", "m")));
//
// That is 604 constructions across 83 files, and every one of them repeated
// the same handful of literals: the session id "s1", the model "m", the tool
// call id "tc1", the LLM call id "call-1", the tool name "counter". The
// repetition was not free of consequences:
//
//   * The ARGUMENT ORDER of `MessageUpdateEvent(LlmEvent, AssistantMessage)`
//     is a trap. Both parameters are reference types, so the compiler cannot
//     catch a swap, and a swapped pair still COMPILES — it just makes the
//     renderer draw a text delta as a tool card. The builders below take the
//     delta and the partial in a fixed order per event kind, so a swap is no
//     longer expressible.
//   * `AssistantMessage.Empty("s1", "m")` is a 27-character way to say
//     "an empty assistant message". It appeared hundreds of times.
//   * `JsonDocument.Parse("{\"path\":\"a.cs\"}").RootElement.Clone()` is
//     manual JSON inside a test fixture, and the `.Clone()` is there because
//     the document is disposable. `ToolExecutionStart` takes the JSON as a
//     STRING and clones it inside, where the `using` can be seen.
//
// WHAT THIS IS NOT
// ----------------
// It is NOT a second event model. Every builder returns the very record the
// product publishes, with the very values the caller passed. The only thing
// the builders add is a DEFAULT for the arguments that the same value in
// ninety percent of the call sites — and every default is documented below
// with the count that chose it.
//
// The defaults were MEASURED on the tree this landed on, not guessed:
// `tools/codemod-test-events.py` counted every `new <Event>(...)` in
// `tests/` and grouped them by shape. The counts quoted in each summary are
// that measurement, so a reader can see WHY a default is what it is.
//
// WHERE IT LIVES, AND WHY
// -----------------------
// `Harbor.TestKit.Support`, not the `Harbor.TestKit` core.
//
// The events live in `Harbor.Abstractions.Contracts`, and this part's closure
// is exactly {itself, Harbor.Abstractions, Harbor.Abstractions.Contracts} —
// no product assembly beyond the two contract ones, which every test project
// that publishes an event already has. Putting the builders here therefore
// adds NO product assembly to any consumer, which is the property #1549's
// closure rule and #1889's split exist to protect.
//
// The core kit references this part, so a consumer of the core reaches these
// builders transitively, in the same `Harbor.TestKit` namespace, with no
// csproj edit. The test projects that reference ONLY this part (Storage,
// Ipc, Renderer, Hosting, Benchmarks, LoadTests, Plugins.Runtime) reach them
// directly.
//
// The core's own closure stays at four projects — itself, this part, and the
// two contract assemblies — which is what
// `tests/Harbor.Architecture.Tests/TestKitCompositionRules.cs` pins.
//
// THE CODE THAT MIGRATED THE CALL SITES
// -------------------------------------
// `tools/codemod-test-events.py` rewrote the call sites; it is re-runnable
// (`--check` reports, `--apply` writes) and it is the reason this file's
// defaults are the values they are: the codemod only ever emits an argument
// that differs from the default, so every migrated call means exactly what
// the hand-written one meant.

using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;

namespace Harbor.TestKit;

/// <summary>
///     Builders for the events the agent loop publishes.
/// </summary>
/// <remarks>
///     <para>
///         One builder per event type, named after the type it builds. The
///         parameters mirror the record's own constructor in the same order,
///         so a reader can check a call site against the record without
///         looking anything up — with ONE exception: the argument whose value
///         the same test tree repeats everywhere is optional, and the
///         defaults are the measured mass values.
///     </para>
///     <para>
///         The <c>Llm</c>-prefixed builders produce the provider-level
///         <see cref="LlmEvent" /> values, and the <c>*Update</c> builders
///         produce the <see cref="MessageUpdateEvent" /> that wraps one of
///         them together with the partial assistant message. The two families
///         are separate because they are separate: a scripted LLM client
///         yields <see cref="LlmEvent" />s, and the bus carries
///         <see cref="AgentEvent" />s.
///     </para>
/// </remarks>
public static class TestEvents
{
    // ---------------------------------------------------------------------
    // The defaults
    // ---------------------------------------------------------------------

    /// <summary>
    ///     The session id the builders use when the caller does not name one.
    /// </summary>
    /// <remarks>
    ///     Measured: 38 of the 69 <c>AgentStartEvent</c> sites and 14 of the
    ///     18 <c>CompactionStartedEvent</c> sites on this tree already said
    ///     "s1".
    /// </remarks>
    public const string DefaultSessionId = "s1";

    /// <summary>
    ///     The tool-call id the tool-execution builders use.
    /// </summary>
    /// <remarks>
    ///     Measured: 9 of the 28 <c>ToolExecutionStartEvent</c> sites and 24
    ///     of the 48 <c>ToolExecutionEndEvent</c> sites already said "tc1".
    /// </remarks>
    public const string DefaultToolCallId = "tc1";

    /// <summary>
    ///     The call id the provider-level tool-call builders use.
    /// </summary>
    /// <remarks>
    ///     Measured: 34 of the 46 <c>ToolCallStartEvent</c> sites and 34 of
    ///     the 41 <c>ToolCallDeltaEvent</c> sites already said "call-1".
    /// </remarks>
    public const string DefaultLlmToolCallId = "call-1";

    /// <summary>
    ///     The tool name the provider-level tool-call builders use.
    /// </summary>
    /// <remarks>
    ///     Measured: 28 of the 46 <c>ToolCallStartEvent</c> sites already
    ///     said "counter".
    /// </remarks>
    public const string DefaultToolName = "counter";

    // ---------------------------------------------------------------------
    // AgentEvent — the run's lifecycle
    // ---------------------------------------------------------------------

    /// <summary>
    ///     The event that opens a run.
    /// </summary>
    /// <param name="sessionId">The owning session. Defaults to <see cref="DefaultSessionId" />.</param>
    /// <param name="messages">
    ///     The history the run starts from. Defaults to an EMPTY list — 44 of
    ///     the 69 <c>AgentStartEvent</c> sites on this tree pass an empty one.
    /// </param>
    /// <param name="model">The model the run uses, or <see langword="null" />.</param>
    /// <param name="kind">Whether the session is a user session or a sub-agent's. Defaults to <see cref="SessionKind.User" />.</param>
    /// <returns>The event to publish.</returns>
    public static AgentStartEvent AgentStart(
        string sessionId = DefaultSessionId,
        IReadOnlyList<AgentMessage>? messages = null,
        ModelInfo? model = null,
        SessionKind kind = SessionKind.User) =>
        new(sessionId, messages ?? [], model, kind);

    /// <summary>
    ///     The event that closes a run.
    /// </summary>
    /// <param name="messages">The messages the run produced. Defaults to an EMPTY list.</param>
    /// <param name="cancelled">Whether the run was cancelled rather than finished. Defaults to <see langword="false" />.</param>
    /// <param name="limit">The limit that ended the run, when a limit did.</param>
    /// <returns>The event to publish.</returns>
    public static AgentEndEvent AgentEnd(
        IReadOnlyList<AgentMessage>? messages = null,
        bool cancelled = false,
        RunLimitKind? limit = null) =>
        new(messages ?? [], cancelled, limit);

    /// <summary>
    ///     The event that reports an unrecoverable agent error.
    /// </summary>
    /// <param name="message">The user-facing error message. Defaults to "boom".</param>
    /// <param name="exception">Optional diagnostics.</param>
    /// <returns>The event to publish.</returns>
    public static AgentErrorEvent AgentError(string message = "boom", string? exception = null) =>
        new(message, exception);

    /// <summary>
    ///     The event that opens a turn.
    /// </summary>
    /// <param name="turnIndex">The 1-based turn index. Defaults to 1 — 67 of the 123 <c>TurnStartEvent</c> sites already said 1.</param>
    /// <param name="sessionId">The owning session, or <see langword="null" /> on a legacy emitter.</param>
    /// <returns>The event to publish.</returns>
    public static TurnStartEvent TurnStart(int turnIndex = 1, string? sessionId = null) =>
        new(turnIndex, sessionId);

    /// <summary>
    ///     The event that closes a turn.
    /// </summary>
    /// <param name="message">The finalized assistant message. Defaults to an empty one.</param>
    /// <param name="toolResults">The tool results the turn produced. Defaults to an EMPTY list.</param>
    /// <param name="sessionId">The owning session, or <see langword="null" />.</param>
    /// <returns>The event to publish.</returns>
    public static TurnEndEvent TurnEnd(
        AssistantMessage? message = null,
        IReadOnlyList<ToolResultMessage>? toolResults = null,
        string? sessionId = null) =>
        new(message ?? EmptyAssistant(), toolResults ?? [], sessionId);

    // ---------------------------------------------------------------------
    // AgentEvent — the message stream
    // ---------------------------------------------------------------------

    /// <summary>
    ///     The event that opens an assistant message.
    /// </summary>
    /// <param name="message">
    ///     The message being streamed. Defaults to an empty assistant message
    ///     for the session <see cref="DefaultSessionId" /> and the model "m" —
    ///     8 of the 61 <c>MessageStartEvent</c> sites already passed exactly
    ///     that.
    /// </param>
    /// <returns>The event to publish.</returns>
    public static MessageStartEvent MessageStart(AssistantMessage? message = null) =>
        new(message ?? EmptyAssistant());

    /// <summary>
    ///     The event that closes an assistant message.
    /// </summary>
    /// <param name="message">
    ///     The finalized message. Defaults to an empty assistant message — 2
    ///     of the 32 <c>MessageEndEvent</c> sites already passed exactly that.
    /// </param>
    /// <returns>The event to publish.</returns>
    public static MessageEndEvent MessageEnd(AssistantMessage? message = null) =>
        new(message ?? EmptyAssistant());

    /// <summary>
    ///     The event that carries one provider-level delta to the subscribers.
    /// </summary>
    /// <param name="llmEvent">The provider-level event.</param>
    /// <param name="partial">The assistant message as it stands. Defaults to an empty one.</param>
    /// <returns>The event to publish.</returns>
    /// <remarks>
    ///     Prefer the <c>*Update</c> builders over this one: they take the
    ///     delta and the partial in an order the compiler cannot be asked to
    ///     check, and they remove the possibility of swapping the two.
    /// </remarks>
    public static MessageUpdateEvent MessageUpdate(LlmEvent llmEvent, AssistantMessage? partial = null) =>
        new(llmEvent, partial ?? EmptyAssistant());

    // ---------------------------------------------------------------------
    // AgentEvent — the tool executions
    // ---------------------------------------------------------------------

    /// <summary>
    ///     The event that opens a tool execution.
    /// </summary>
    /// <param name="toolCallId">The correlation id. Defaults to <see cref="DefaultToolCallId" />.</param>
    /// <param name="toolName">The tool's stable name. Defaults to "read".</param>
    /// <param name="argsJson">
    ///     The raw arguments, as JSON. Defaults to an empty object. The JSON
    ///     is parsed and CLONED inside, so the event owns its arguments beyond
    ///     the document's lifetime — the reason the hand-written call sites
    ///     spelled <c>JsonDocument.Parse(...).RootElement.Clone()</c>.
    /// </param>
    /// <param name="glyph">The tool's declared glyph, or <see langword="null" />.</param>
    /// <returns>The event to publish.</returns>
    public static ToolExecutionStartEvent ToolExecutionStart(
        string toolCallId = DefaultToolCallId,
        string toolName = "read",
        string? argsJson = null,
        string? glyph = null)
    {
        using JsonDocument document = JsonDocument.Parse(argsJson ?? "{}");
        return new ToolExecutionStartEvent(toolCallId, toolName, document.RootElement.Clone(), glyph);
    }

    /// <summary>
    ///     The event that reports progress inside a tool execution.
    /// </summary>
    /// <param name="toolCallId">The correlation id. Defaults to <see cref="DefaultToolCallId" />.</param>
    /// <param name="partialResult">The tool's own progress payload.</param>
    /// <param name="retryAttempt">The attempt that failed, when this update reports a retry.</param>
    /// <param name="retryMaxAttempts">The total number of attempts the policy allows.</param>
    /// <param name="retryBackoffSeconds">How long the dispatcher waited before the attempt.</param>
    /// <returns>The event to publish.</returns>
    public static ToolExecutionUpdateEvent ToolExecutionUpdate(
        string toolCallId = DefaultToolCallId,
        object? partialResult = null,
        int? retryAttempt = null,
        int? retryMaxAttempts = null,
        double? retryBackoffSeconds = null) =>
        new(toolCallId, partialResult ?? string.Empty, retryAttempt, retryMaxAttempts, retryBackoffSeconds);

    /// <summary>
    ///     The event that closes a tool execution.
    /// </summary>
    /// <param name="toolCallId">The correlation id. Defaults to <see cref="DefaultToolCallId" />.</param>
    /// <param name="result">The tool's result. Defaults to an empty successful one.</param>
    /// <param name="isError">Whether the execution failed. Defaults to <see langword="false" />.</param>
    /// <returns>The event to publish.</returns>
    public static ToolExecutionEndEvent ToolExecutionEnd(
        string toolCallId = DefaultToolCallId,
        ToolResult? result = null,
        bool isError = false) =>
        new(toolCallId, result ?? ToolResult.Success(string.Empty), isError);

    // ---------------------------------------------------------------------
    // AgentEvent — the session's bookkeeping
    // ---------------------------------------------------------------------

    /// <summary>
    ///     The periodic cost-and-token snapshot.
    /// </summary>
    /// <param name="sessionId">The owning session. Defaults to <see cref="DefaultSessionId" />.</param>
    /// <param name="metadata">The snapshot. Defaults to <see cref="SessionMetadata.Empty" />.</param>
    /// <returns>The event to publish.</returns>
    public static SessionStatsEvent SessionStats(string sessionId = DefaultSessionId, SessionMetadata? metadata = null) =>
        new(sessionId, metadata ?? SessionMetadata.Empty);

    /// <summary>
    ///     The event that reports a change of the active session.
    /// </summary>
    /// <param name="sessionId">The new active session. Defaults to <see cref="DefaultSessionId" />.</param>
    /// <returns>The event to publish.</returns>
    public static SessionChangedEvent SessionChanged(string sessionId = DefaultSessionId) =>
        new(sessionId);

    // ---------------------------------------------------------------------
    // AgentEvent — the compaction lifecycle
    // ---------------------------------------------------------------------

    /// <summary>
    ///     The event that opens a compaction.
    /// </summary>
    /// <param name="sessionId">The session being compacted. Defaults to <see cref="DefaultSessionId" />.</param>
    /// <returns>The event to publish.</returns>
    public static CompactionStartedEvent CompactionStarted(string sessionId = DefaultSessionId) =>
        new(sessionId);

    /// <summary>
    ///     The event that reports a finished compaction.
    /// </summary>
    /// <param name="sessionId">The compacted session. Defaults to <see cref="DefaultSessionId" />.</param>
    /// <param name="summary">The generated summary. Defaults to "summary".</param>
    /// <param name="prunedMessageCount">How many messages were folded into the summary.</param>
    /// <param name="tokensSaved">The estimated tokens the compaction saved.</param>
    /// <param name="duration">How long the compaction took. Defaults to one second.</param>
    /// <returns>The event to publish.</returns>
    public static CompactionCompletedEvent CompactionCompleted(
        string sessionId = DefaultSessionId,
        string summary = "summary",
        int prunedMessageCount = 0,
        int tokensSaved = 0,
        TimeSpan? duration = null) =>
        new(sessionId, summary, prunedMessageCount, tokensSaved, duration ?? TimeSpan.FromSeconds(1));

    /// <summary>
    ///     The event that reports a failed compaction.
    /// </summary>
    /// <param name="sessionId">The session whose compaction failed. Defaults to <see cref="DefaultSessionId" />.</param>
    /// <param name="error">What went wrong. Defaults to "boom".</param>
    /// <returns>The event to publish.</returns>
    public static CompactionFailedEvent CompactionFailed(string sessionId = DefaultSessionId, string error = "boom") =>
        new(sessionId, error);

    // ---------------------------------------------------------------------
    // AgentEvent — the plugin sandbox
    // ---------------------------------------------------------------------

    /// <summary>
    ///     The event that reports a blocked plugin operation.
    /// </summary>
    /// <param name="pluginName">The plugin whose operation was blocked.</param>
    /// <param name="reason">The machine-readable reason: "timeout", "memory" or "capability".</param>
    /// <param name="detail">What was attempted and which limit fired.</param>
    /// <returns>The event to publish.</returns>
    public static PluginBlockedEvent PluginBlocked(
        string pluginName = "plugin",
        string reason = "timeout",
        string detail = "detail") =>
        new(pluginName, reason, detail);

    // ---------------------------------------------------------------------
    // LlmEvent — what a provider streams
    // ---------------------------------------------------------------------

    /// <summary>
    ///     The provider-level event that opens a text run.
    /// </summary>
    /// <param name="id">The provider's id for the run.</param>
    /// <returns>The provider-level event to yield.</returns>
    public static TextStartEvent TextStart(string id = "t") => new(id);

    /// <summary>
    ///     The provider-level event that carries a chunk of assistant text.
    /// </summary>
    /// <param name="id">The provider's id for the run. Defaults to "t".</param>
    /// <param name="delta">The chunk. Defaults to the empty string.</param>
    /// <returns>The provider-level event to yield.</returns>
    public static TextDeltaEvent TextDelta(string id = "t", string delta = "") => new(id, delta);

    /// <summary>
    ///     The provider-level event that closes a text run.
    /// </summary>
    /// <param name="id">The provider's id for the run.</param>
    /// <param name="finalText">The complete text the run emitted.</param>
    /// <returns>The provider-level event to yield.</returns>
    public static TextEndEvent TextEnd(string id = "t", string finalText = "") => new(id, finalText);

    /// <summary>
    ///     The provider-level event that opens a thinking run.
    /// </summary>
    /// <param name="id">The provider's id for the run.</param>
    /// <returns>The provider-level event to yield.</returns>
    public static ThinkingStartEvent ThinkingStart(string id = "h1") => new(id);

    /// <summary>
    ///     The provider-level event that carries a chunk of reasoning text.
    /// </summary>
    /// <param name="id">The provider's id for the run. Defaults to "h1".</param>
    /// <param name="delta">The chunk. Defaults to the empty string.</param>
    /// <returns>The provider-level event to yield.</returns>
    public static ThinkingDeltaEvent ThinkingDelta(string id = "h1", string delta = "") => new(id, delta);

    /// <summary>
    ///     The provider-level event that closes a thinking run.
    /// </summary>
    /// <param name="id">The provider's id for the run.</param>
    /// <param name="finalText">The complete text the run emitted.</param>
    /// <returns>The provider-level event to yield.</returns>
    public static ThinkingEndEvent ThinkingEnd(string id = "h1", string finalText = "") => new(id, finalText);

    /// <summary>
    ///     The provider-level event that opens a tool call.
    /// </summary>
    /// <param name="id">The provider's id for the call. Defaults to <see cref="DefaultLlmToolCallId" />.</param>
    /// <param name="toolName">The tool's name. Defaults to <see cref="DefaultToolName" />.</param>
    /// <returns>The provider-level event to yield.</returns>
    public static ToolCallStartEvent ToolCallStart(
        string id = DefaultLlmToolCallId,
        string toolName = DefaultToolName) =>
        new(id, toolName);

    /// <summary>
    ///     The provider-level event that carries a fragment of the tool call's arguments.
    /// </summary>
    /// <param name="id">The provider's id for the call. Defaults to <see cref="DefaultLlmToolCallId" />.</param>
    /// <param name="argsDelta">The fragment. Defaults to an empty object.</param>
    /// <returns>The provider-level event to yield.</returns>
    public static ToolCallDeltaEvent ToolCallDelta(
        string id = DefaultLlmToolCallId,
        string argsDelta = "{}") =>
        new(id, argsDelta);

    /// <summary>
    ///     The provider-level event that opens an inference step.
    /// </summary>
    /// <param name="index">The step's index. Defaults to 0.</param>
    /// <returns>The provider-level event to yield.</returns>
    public static StepStartEvent StepStart(int index = 0) => new(index);

    /// <summary>
    ///     The provider-level event that closes an inference step.
    /// </summary>
    /// <param name="index">The step's index. Defaults to 0 — 87 of the 116 <c>StepFinishEvent</c> sites already said 0.</param>
    /// <param name="finishReason">Why the step ended. Defaults to "stop".</param>
    /// <param name="usage">The step's token usage, or <see langword="null" />.</param>
    /// <param name="providerModelId">The provider's own id for the model, when it differs from Harbor's.</param>
    /// <returns>The provider-level event to yield.</returns>
    public static StepFinishEvent StepFinish(
        int index = 0,
        string finishReason = "stop",
        Usage? usage = null,
        string? providerModelId = null) =>
        new(index, finishReason, usage, providerModelId);

    /// <summary>
    ///     The provider-level event that reports the end of the generation.
    /// </summary>
    /// <returns>The provider-level event to yield.</returns>
    public static FinishEvent LlmFinish() => new();

    /// <summary>
    ///     The provider-level event that reports a streaming failure.
    /// </summary>
    /// <param name="message">What went wrong. Defaults to "boom".</param>
    /// <param name="exception">Optional diagnostics.</param>
    /// <param name="kind">How the transport failed.</param>
    /// <param name="statusCode">The HTTP status, when there was one.</param>
    /// <returns>The provider-level event to yield.</returns>
    public static ErrorEvent LlmError(
        string message = "boom",
        string? exception = null,
        ProviderErrorKind kind = ProviderErrorKind.Unknown,
        int? statusCode = null) =>
        new(message, exception, kind, statusCode);

    // ---------------------------------------------------------------------
    // MessageUpdateEvent — a provider-level delta, wrapped
    // ---------------------------------------------------------------------

    /// <summary>
    ///     A text delta, wrapped in the update the subscribers see.
    /// </summary>
    /// <param name="id">The provider's id for the run. Defaults to "t".</param>
    /// <param name="delta">The chunk. Defaults to the empty string.</param>
    /// <param name="partial">The assistant message as it stands. Defaults to an empty one.</param>
    /// <returns>The event to publish.</returns>
    public static MessageUpdateEvent TextDeltaUpdate(
        string id = "t",
        string delta = "",
        AssistantMessage? partial = null) =>
        new(new TextDeltaEvent(id, delta), partial ?? EmptyAssistant());

    /// <summary>
    ///     A reasoning delta, wrapped in the update the subscribers see.
    /// </summary>
    /// <param name="id">The provider's id for the run. Defaults to "h1".</param>
    /// <param name="delta">The chunk. Defaults to the empty string.</param>
    /// <param name="partial">The assistant message as it stands. Defaults to an empty one.</param>
    /// <returns>The event to publish.</returns>
    public static MessageUpdateEvent ThinkingDeltaUpdate(
        string id = "h1",
        string delta = "",
        AssistantMessage? partial = null) =>
        new(new ThinkingDeltaEvent(id, delta), partial ?? EmptyAssistant());

    /// <summary>
    ///     A tool-call opening, wrapped in the update the subscribers see.
    /// </summary>
    /// <param name="id">The provider's id for the call. Defaults to <see cref="DefaultLlmToolCallId" />.</param>
    /// <param name="toolName">The tool's name. Defaults to <see cref="DefaultToolName" />.</param>
    /// <param name="partial">The assistant message as it stands. Defaults to an empty one.</param>
    /// <returns>The event to publish.</returns>
    public static MessageUpdateEvent ToolCallStartUpdate(
        string id = DefaultLlmToolCallId,
        string toolName = DefaultToolName,
        AssistantMessage? partial = null) =>
        new(new ToolCallStartEvent(id, toolName), partial ?? EmptyAssistant());

    /// <summary>
    ///     A tool-call argument fragment, wrapped in the update the subscribers see.
    /// </summary>
    /// <param name="id">The provider's id for the call. Defaults to <see cref="DefaultLlmToolCallId" />.</param>
    /// <param name="argsDelta">The fragment. Defaults to an empty object.</param>
    /// <param name="partial">The assistant message as it stands. Defaults to an empty one.</param>
    /// <returns>The event to publish.</returns>
    public static MessageUpdateEvent ToolCallDeltaUpdate(
        string id = DefaultLlmToolCallId,
        string argsDelta = "{}",
        AssistantMessage? partial = null) =>
        new(new ToolCallDeltaEvent(id, argsDelta), partial ?? EmptyAssistant());

    /// <summary>
    ///     A finished inference step, wrapped in the update the subscribers see.
    /// </summary>
    /// <param name="index">The step's index. Defaults to 0.</param>
    /// <param name="finishReason">Why the step ended. Defaults to "stop".</param>
    /// <param name="usage">The step's token usage, or <see langword="null" />.</param>
    /// <param name="partial">The assistant message as it stands. Defaults to an empty one.</param>
    /// <returns>The event to publish.</returns>
    public static MessageUpdateEvent StepFinishUpdate(
        int index = 0,
        string finishReason = "stop",
        Usage? usage = null,
        AssistantMessage? partial = null) =>
        new(new StepFinishEvent(index, finishReason, usage), partial ?? EmptyAssistant());

    // ---------------------------------------------------------------------
    // The shared empty assistant message
    // ---------------------------------------------------------------------

    /// <summary>
    ///     An empty assistant message for the default session and the model "m".
    /// </summary>
    /// <returns>A fresh empty assistant message.</returns>
    /// <remarks>
    ///     The id is fresh on every call, exactly as
    ///     <see cref="AssistantMessage.Empty" /> makes it — the fixture is a
    ///     value, not a shared instance.
    /// </remarks>
    private static AssistantMessage EmptyAssistant() => AssistantMessage.Empty(DefaultSessionId, "m");
}
