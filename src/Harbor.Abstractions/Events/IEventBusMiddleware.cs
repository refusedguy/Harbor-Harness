namespace Harbor.Abstractions.Events;

/// <summary>
///     Middleware in the event bus pipeline. Allows filtering, transforming, or
///     enriching <see cref="AgentEvent" />s before they reach subscribers.
/// </summary>
/// <remarks>
///     <para>
///         Middleware is invoked in registration order. Returning <c>false</c> drops
///         the event entirely (it will not be appended to scrollback or fanned out
///         to subscribers). Returning <c>true</c> passes the event to the next
///         middleware or, if this is the last middleware, to the scrollback +
///         fan-out phase.
///     </para>
///     <para>
///         The <c>ref AgentEvent</c> parameter allows a middleware to replace the
///         event in-place. For synchronous middleware, return
///         <see cref="ValueTask.FromResult{T}(T)" /> to avoid heap allocation.
///     </para>
///     <para>
///         Exceptions thrown by middleware are caught by the event bus, logged as
///         a warning, and cause the event to be dropped — the bus itself is never
///         broken by a faulty middleware.
///     </para>
///     <para>
///         <b>Mandatory vs optional (#47/S3):</b> every middleware is a sink, and
///         the bus needs to know what a silent skip would cost before it is
///         allowed to take the zero-subscriber fast path. Declare the verdict
///         through <see cref="SinkKind" />; the bus reads it once in its
///         constructor. The default is <see cref="EventBusSinkKind.Mandatory" />,
///         so a middleware that has not thought about it can never be bypassed by
///         accident. Full verdict table: <c>docs/EVENT_BUS_SINKS.md</c>.
///     </para>
/// </remarks>
public interface IEventBusMiddleware
{
    /// <summary>
    ///     Human-readable name used in log messages to identify which middleware
    ///     filtered or transformed an event.
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     What silently losing an event to this middleware would cost
    ///     (#47/S3). Defaults to <see cref="EventBusSinkKind.Mandatory" /> —
    ///     the conservative direction: unknown sinks keep the bus on its full
    ///     path. Declare <see cref="EventBusSinkKind.Optional" /> only for a
    ///     diagnostic, sampler, secondary projection or third-party extension,
    ///     i.e. a sink nothing downstream becomes wrong without.
    /// </summary>
    EventBusSinkKind SinkKind => EventBusSinkKind.Mandatory;

    /// <summary>
    ///     Process an event. Return <c>true</c> to continue the pipeline,
    ///     <c>false</c> to drop the event.
    /// </summary>
    /// <param name="event">The event to process. May be replaced in-place via the <c>ref</c> parameter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> to pass the event to the next middleware or fan-out; <c>false</c> to drop.</returns>
    ValueTask<bool> ProcessAsync(ref AgentEvent @event, CancellationToken ct = default);
}
