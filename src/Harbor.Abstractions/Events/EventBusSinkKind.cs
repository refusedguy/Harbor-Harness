namespace Harbor.Abstractions.Events;

/// <summary>
///     Verdict on a registered event-bus sink: what breaks if the event is
///     silently dropped for it (#47/S3). Declared once, by the sink itself, and
///     read once at composition time by the bus — never sniffed per publish.
/// </summary>
/// <remarks>
///     <para>
///         The verdict rule is the one <see cref="IEventBusMiddleware" />
///         documents: a sink is <see cref="Mandatory" /> when losing an event
///         silently breaks <b>state</b> (a projection the user sees),
///         <b>audit</b> (a record that must exist), <b>accounting</b> (token/cost
///         totals) or <b>telemetry</b> (a measurement that must be taken). It is
///         <see cref="Optional" /> when the sink is a diagnostic, a sampler, a
///         secondary projection or a third-party extension — nothing downstream
///         of it becomes wrong when it stops seeing events.
///     </para>
///     <para>
///         Optional does <b>not</b> mean "may be skipped in silence". It means
///         the bus may take the zero-subscriber fast path for a bus that has
///         nothing but optional sinks attached — and then it still drains them
///         and counts the drain, so a skip is always visible in a metric. See
///         <c>docs/EVENT_BUS_SINKS.md</c> for the enumerated verdict of every
///         registration site in the repository.
///     </para>
/// </remarks>
public enum EventBusSinkKind : byte
{
    /// <summary>
    ///     Losing the event costs observability, not correctness. The sink is
    ///     still executed on the fast path; only a bus that has no other
    ///     consumer at all may skip work it would otherwise duplicate.
    /// </summary>
    Optional = 0,

    /// <summary>
    ///     Losing the event silently breaks state, audit, accounting or
    ///     telemetry. The bus keeps its full slow path for as long as such a
    ///     sink is registered — the default for any middleware that does not
    ///     declare a verdict of its own.
    /// </summary>
    Mandatory = 1
}
