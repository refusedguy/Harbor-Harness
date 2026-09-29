using System.Collections.Frozen;
using Harbor.Abstractions.Events;
namespace Harbor.Ipc;
/// <summary>
///     THE <see cref="AgentEvent" /> → <see cref="HarborEvent" /> projection —
///     one table, shared by every host that streams events to clients (#495).
/// </summary>
/// <remarks>
///     <para>
///         This switch used to be copy-pasted into
///         <c>EventBroadcaster.ProjectEvent</c> and
///         <c>InProcessHarborClient.ProjectEvent</c>, arm for arm, with a comment
///         promising they were "intentionally identical". Both ended in
///         <c>_ =&gt; null</c>, so adding a case in one assembly and forgetting the
///         other compiled cleanly and produced a host that silently never delivered
///         that event: no error, no log, no test failure. The duplication had
///         already drifted (the in-process copy also set the active session, the
///         broadcaster's was set by the routing switch instead).
///     </para>
///     <para>
///         There is now exactly one table, and an event type that appears in
///         neither <see cref="Handlers" /> nor <see cref="NoWireCaseReasons" /> comes
///         back as <see cref="EventProjectionOutcome.Unmapped" /> so the host can log
///         it and bump an observable counter instead of dropping it silently.
///     </para>
///     <para>
///         <b>Exhaustiveness is enforced by a test, not by a comment:</b>
///         <c>Harbor.Ipc.Tests/AgentEventProjectionCoverageTests</c> reflects over
///         every <see cref="AgentEvent" /> and <see cref="LlmEvent" /> subtype and
///         fails when one is unclassified — so "just add a <c>_ =&gt;</c> arm" is not
///         an available way to add an event.
///     </para>
///     <para>
///         <b>AOT:</b> the tables are built once in a static initializer from
///         explicit <c>typeof</c> keys. No reflection, no emit, no dynamic dispatch
///         beyond a delegate invoke.
///     </para>
/// </remarks>
public static class AgentEventProjector
{
    /// <summary>
    ///     Event types that DO reach the wire, and the handler that builds the
    ///     <see cref="HarborEvent" /> for each.
    /// </summary>
    public static FrozenDictionary<Type, AgentEventHandler> Handlers { get; } =
        new Dictionary<Type, AgentEventHandler>
        {
            [typeof(AgentStartEvent)] = ProjectAgentStart,
            [typeof(MessageUpdateEvent)] = ProjectMessageUpdate,
            [typeof(MessageEndEvent)] = ProjectMessageEnd,
            [typeof(ToolExecutionStartEvent)] = ProjectToolStart,
            [typeof(ToolExecutionEndEvent)] = ProjectToolEnd,
            [typeof(TurnStartEvent)] = ProjectTurnStart,
            [typeof(TurnEndEvent)] = ProjectTurnEnd,
            [typeof(AgentErrorEvent)] = ProjectAgentError,
            [typeof(CompactionStartedEvent)] = ProjectCompactionStarted,
            [typeof(CompactionCompletedEvent)] = ProjectCompactionCompleted
        }.ToFrozenDictionary();

    /// <summary>
    ///     Event types that deliberately do NOT reach the wire, each with the reason
    ///     why. Membership here is a decision, not an omission — the coverage test
    ///     requires every <see cref="AgentEvent" /> subtype to be in exactly one of
    ///     <see cref="Handlers" /> and this table.
    /// </summary>
    public static FrozenDictionary<Type, string> NoWireCaseReasons { get; } =
        new Dictionary<Type, string>
        {
            [typeof(MessageStartEvent)] =
                "start is implied by the first MessageUpdate; the wire union has no separate case",
            [typeof(AgentEndEvent)] =
                "HarborEvent.AgentEnded is emitted by the agent runner hook, not by the projection",
            [typeof(ToolExecutionUpdateEvent)] =
                "tool progress is render-only; ToolStart/ToolEnd bracket it on the wire",
            [typeof(CompactionFailedEvent)] =
                "a failed compaction falls back to truncation and the run continues — the union carries started/completed",
            [typeof(SessionStatsEvent)] =
                "cost/token snapshots are polled over RPC, not streamed",
            [typeof(SessionChangedEvent)] =
                "a session switch is returned by the RPC call that caused it",
            [typeof(PluginBlockedEvent)] =
                "plugin-sandbox enforcement is an audit/telemetry signal, not part of the client event stream"
        }.ToFrozenDictionary();

    /// <summary>
    ///     <see cref="LlmEvent" /> subtypes that carry a textual delta the wire
    ///     union can show, and how to read it out.
    /// </summary>
    public static FrozenDictionary<Type, Func<LlmEvent, string>> DeltaExtractors { get; } =
        new Dictionary<Type, Func<LlmEvent, string>>
        {
            [typeof(TextDeltaEvent)] = static e => ((TextDeltaEvent)e).Delta,
            [typeof(ThinkingDeltaEvent)] = static e => ((ThinkingDeltaEvent)e).Delta,
            [typeof(ToolCallDeltaEvent)] = static e => ((ToolCallDeltaEvent)e).ArgsDelta
        }.ToFrozenDictionary();

    /// <summary>
    ///     <see cref="LlmEvent" /> subtypes that carry no delta text (starts, ends,
    ///     step/finish markers, errors). They still produce a
    ///     <see cref="HarborEvent.MessageUpdate" /> — with an empty delta — so a
    ///     client sees the message boundary. Listed explicitly so the coverage test
    ///     can hold this union closed too.
    /// </summary>
    public static FrozenSet<Type> DeltaFreeLlmEvents { get; } =
        new HashSet<Type>
        {
            typeof(TextStartEvent),
            typeof(TextEndEvent),
            typeof(ThinkingStartEvent),
            typeof(ThinkingEndEvent),
            typeof(ToolCallStartEvent),
            typeof(ToolCallEndEvent),
            typeof(StepStartEvent),
            typeof(StepFinishEvent),
            typeof(FinishEvent),
            typeof(ErrorEvent)
        }.ToFrozenSet();

    /// <summary>
    ///     Project one <see cref="AgentEvent" /> onto the wire union, mutating
    ///     <paramref name="state" /> as the mapping requires (run start, turn
    ///     tracking). There is no per-host override: every host observes the same
    ///     stream for the same agent run.
    /// </summary>
    /// <param name="evt">The event to project.</param>
    /// <param name="state">Per-host projection state, owned by the caller.</param>
    /// <returns>
    ///     <see cref="EventProjectionOutcome.Emitted" /> with a wire event,
    ///     <see cref="EventProjectionOutcome.NoWireCase" /> for a type listed in
    ///     <see cref="NoWireCaseReasons" />, or
    ///     <see cref="EventProjectionOutcome.Unmapped" /> for an event type that is
    ///     in neither table — a bug the caller must surface.
    /// </returns>
    public static EventProjection Project(AgentEvent evt, ProjectionState state)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(state);

        if (Handlers.TryGetValue(evt.GetType(), out var handler))
        {
            return EventProjection.Emit(handler(evt, state));
        }

        return NoWireCaseReasons.ContainsKey(evt.GetType())
            ? EventProjection.NoWireCase
            : EventProjection.Unmapped;
    }

    /// <summary>
    ///     Read the textual delta out of an <see cref="LlmEvent" />. Types listed in
    ///     <see cref="DeltaFreeLlmEvents" /> contribute an empty delta; the coverage
    ///     test guarantees no <see cref="LlmEvent" /> subtype reaches the residual
    ///     branch unclassified.
    /// </summary>
    /// <param name="llmEvent">The provider-level streaming event.</param>
    public static string ExtractDelta(LlmEvent llmEvent)
    {
        ArgumentNullException.ThrowIfNull(llmEvent);

        return DeltaExtractors.TryGetValue(llmEvent.GetType(), out var extract)
            ? extract(llmEvent)
            : string.Empty;
    }

    private static HarborEvent? ProjectAgentStart(AgentEvent evt, ProjectionState state)
    {
        var e = (AgentStartEvent)evt;
        state.BeginRun(e.SessionId);
        return new HarborEvent.AgentStarted(e.SessionId);
    }

    private static HarborEvent? ProjectMessageUpdate(AgentEvent evt, ProjectionState _)
    {
        var e = (MessageUpdateEvent)evt;
        return new HarborEvent.MessageUpdate(e.Partial, ExtractDelta(e.LlmEvent));
    }

    private static HarborEvent? ProjectMessageEnd(AgentEvent evt, ProjectionState _)
    {
        var e = (MessageEndEvent)evt;
        return new HarborEvent.MessageEnd(e.Message);
    }

    private static HarborEvent? ProjectToolStart(AgentEvent evt, ProjectionState _)
    {
        var e = (ToolExecutionStartEvent)evt;
        return new HarborEvent.ToolStart(e.ToolCallId, e.ToolName);
    }

    private static HarborEvent? ProjectToolEnd(AgentEvent evt, ProjectionState _)
    {
        var e = (ToolExecutionEndEvent)evt;
        return new HarborEvent.ToolEnd(e.ToolCallId, e.Result);
    }

    private static HarborEvent? ProjectTurnStart(AgentEvent evt, ProjectionState state)
    {
        var e = (TurnStartEvent)evt;
        state.RecordTurn(e.SessionId, e.TurnIndex);
        return new HarborEvent.TurnStart(e.TurnIndex);
    }

    private static HarborEvent? ProjectTurnEnd(AgentEvent evt, ProjectionState state)
    {
        var e = (TurnEndEvent)evt;
        return new HarborEvent.TurnEnd(state.ResolveTurn(e.SessionId));
    }

    private static HarborEvent? ProjectAgentError(AgentEvent evt, ProjectionState _)
    {
        var e = (AgentErrorEvent)evt;
        return new HarborEvent.AgentError(e.Message);
    }

    private static HarborEvent? ProjectCompactionStarted(AgentEvent evt, ProjectionState _)
    {
        var e = (CompactionStartedEvent)evt;
        return new HarborEvent.CompactionStarted(e.SessionId);
    }

    private static HarborEvent? ProjectCompactionCompleted(AgentEvent evt, ProjectionState _)
    {
        var e = (CompactionCompletedEvent)evt;
        return new HarborEvent.CompactionCompleted(e.SessionId, e.PrunedMessageCount, e.TokensSaved);
    }
}
