using Harbor.Abstractions.Events;
namespace Harbor.Ipc;
/// <summary>
///     Why one <see cref="AgentEvent" /> did or did not reach the
///     <see cref="HarborEvent" /> wire union.
/// </summary>
public enum EventProjectionOutcome
{
    /// <summary>Projected — <see cref="EventProjection.Event" /> carries the wire event.</summary>
    Emitted,

    /// <summary>
    ///     Deliberately has no <see cref="HarborEvent" /> case. This is a DECISION
    ///     recorded per event type, with a written reason, in
    ///     <see cref="AgentEventProjector.NoWireCaseReasons" /> — never a default
    ///     reached by falling off the end of a switch.
    /// </summary>
    NoWireCase,

    /// <summary>
    ///     The event type is in neither table: a new <see cref="AgentEvent" />
    ///     subtype was added without a mapping. That is a bug, and hosts must count
    ///     and log it rather than drop the event silently (#495).
    /// </summary>
    Unmapped
}

/// <summary>Projects one <see cref="AgentEvent" /> subtype onto a <see cref="HarborEvent" />.</summary>
/// <param name="evt">The event to project.</param>
/// <param name="state">Per-host turn / active-session state.</param>
/// <returns>The wire event, or null for a type listed as having no wire case.</returns>
public delegate HarborEvent? AgentEventHandler(AgentEvent evt, ProjectionState state);

/// <summary>
///     The result of projecting a single <see cref="AgentEvent" />: the outcome
///     plus the wire event when one was produced.
/// </summary>
/// <param name="Outcome">Whether the event was emitted, skipped by decision, or unmapped.</param>
/// <param name="Event">The projected wire event, or null unless <paramref name="Outcome" /> is <see cref="EventProjectionOutcome.Emitted" />.</param>
public readonly record struct EventProjection(EventProjectionOutcome Outcome, HarborEvent? Event)
{
    /// <summary>Skipped by decision — the event type has no wire case.</summary>
    public static EventProjection NoWireCase { get; } = new(EventProjectionOutcome.NoWireCase, null);

    /// <summary>Unmapped — the event type is in neither table (a bug, see #495).</summary>
    public static EventProjection Unmapped { get; } = new(EventProjectionOutcome.Unmapped, null);

    /// <summary>Wrap whatever a handler produced (handlers are expected to return a wire event).</summary>
    /// <param name="evt">The wire event the handler produced, or null if it produced none.</param>
    public static EventProjection Emit(HarborEvent? evt) => new(EventProjectionOutcome.Emitted, evt);

    /// <summary>True when a non-null <see cref="HarborEvent" /> was produced.</summary>
    public bool IsEmitted => Outcome == EventProjectionOutcome.Emitted && Event is not null;
}
