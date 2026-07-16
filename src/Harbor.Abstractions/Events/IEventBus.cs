namespace Harbor.Abstractions.Events;
/// <summary>
///     Pub/Sub event bus. Implements Observer pattern (GOF).
///     Allows multiple subscribers to receive agent events.
/// </summary>
/// <remarks>
///     <para>
///         The event bus is the single broadcast channel for everything that happens inside the
///         agent loop: turn boundaries, LLM streaming deltas, tool execution progress, compaction
///         events, errors, and periodic stats snapshots. The TUI, loggers, IPC clients, and plugins
///         all subscribe to <see cref="AgentEvent" />s without ever reaching into <c>Harbor.Core</c> directly.
///     </para>
///     <para>
///         Implementations MUST be thread-safe and SHOULD support backpressure (e.g. bounded
///         channels or drop-oldest semantics). The default <c>InMemoryEventBus</c> lives in
///         <c>Harbor.Core</c> and uses an <c>ImmutableArray</c> for lock-free snapshot reads plus
///         a bounded scrollback channel for late-attaching subscribers.
///     </para>
/// </remarks>
public interface IEventBus
{
    /// <summary>
    ///     Publish an event to all subscribers.
    /// </summary>
    /// <param name="event">The event to publish.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task PublishAsync(AgentEvent @event, CancellationToken ct = default);

    /// <summary>
    ///     Subscribe to events. Returns a disposable to unsubscribe.
    /// </summary>
    /// <param name="handler">Async handler invoked for every event.</param>
    /// <returns>An <see cref="IDisposable" /> that unsubscribes the handler on dispose.</returns>
    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> handler);

    /// <summary>
    ///     Subscribe with a typed filter.
    /// </summary>
    /// <typeparam name="TEvent">The event type to filter on.</typeparam>
    /// <param name="handler">Async handler invoked only for events assignable to <typeparamref name="TEvent" />.</param>
    /// <returns>An <see cref="IDisposable" /> that unsubscribes the handler on dispose.</returns>
    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler) where TEvent : AgentEvent;

    /// <summary>
    ///     Get recent events from the in-memory scrollback buffer.
    /// </summary>
    /// <param name="maxEvents">Maximum number of events to return (most recent first).</param>
    /// <returns>The scrollback slice.</returns>
    public IReadOnlyList<AgentEvent> GetScrollback(int maxEvents);
}

/// <summary>
///     Subscriber that receives events (used for IPC clients, e.g. TUI process).
/// </summary>
/// <remarks>
///     Implementations forward <see cref="AgentEvent" />s to an out-of-process consumer
///     (typically the TUI process in the planned two-process architecture).
/// </remarks>
public interface IEventSubscriber
{
    /// <summary>
    ///     Forward an event to the subscriber.
    /// </summary>
    /// <param name="event">The event to forward.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task SendAsync(AgentEvent @event, CancellationToken ct = default);
}
