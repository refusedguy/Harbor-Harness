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
/// </remarks>
public interface IEventBusMiddleware
{
    /// <summary>
    ///     Human-readable name used in log messages to identify which middleware
    ///     filtered or transformed an event.
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     Process an event. Return <c>true</c> to continue the pipeline,
    ///     <c>false</c> to drop the event.
    /// </summary>
    /// <param name="event">The event to process. May be replaced in-place via the <c>ref</c> parameter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> to pass the event to the next middleware or fan-out; <c>false</c> to drop.</returns>
    ValueTask<bool> ProcessAsync(ref AgentEvent @event, CancellationToken ct = default);
}
