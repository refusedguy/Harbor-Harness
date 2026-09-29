using Microsoft.Extensions.Logging;

namespace Harbor.Registries.Events;

/// <summary>
///     Type-allowlist middleware. Only events whose runtime type matches one
///     of the allowed types pass through.
/// </summary>
/// <remarks>
///     <para>
///         An allowlist is <b>required</b>: constructing this middleware with no
///         types throws <see cref="ArgumentException" />. It used to mean "allow
///         everything" (<c>_allowAll</c>), and the CLI registered exactly that for
///         its whole life — a filter that admitted every event while its
///         doc-comment advertised configuration-driven filtering that no config
///         key ever fed (#478). That was the most expensive kind of no-op
///         available: see <see cref="EventBusSinkKind.Mandatory" /> below.
///     </para>
///     <para>
///         Uses a plain <c>for</c> loop with <c>Type</c> equality (not
///         <c>IsAssignableFrom</c>) for O(n) matching with zero reflection.
///         <see cref="AgentEvent.GetType()" /> is a built-in CLR method, not
///         reflection.
///     </para>
///     <para>
///         <b>Verdict (#47/S3): <see cref="EventBusSinkKind.Mandatory" />.</b>
///         A filter is a contract on what the projections downstream are allowed
///         to see, not a listener: the UI projections, the IPC broadcaster and
///         the session/accounting path all assume an unfiltered bus only reaches
///         them event types a host approved. Silently bypassing the filter would
///         push unapproved event types into state that is rendered to the user —
///         a correctness break, not a lost log line.
///     </para>
///     <para>
///         That verdict is also why the empty allowlist had to go rather than be
///         documented. <c>InMemoryEventBus</c> reads <see cref="SinkKind" /> once
///         in its constructor, so a mandatory sink sets <c>_hasMandatorySink</c>
///         and forces <c>_fastPathEligible = false</c> for the whole bus. A
///         typeless filter therefore charged the full mandatory-sink cost on
///         every publish — slow path, queue-age envelope, ~112 B/op per
///         docs/BENCHMARKS.md §5.4 — and filtered nothing in return. The verdict
///         is decided by the sink's role, so the sink has to actually do that
///         role before it may claim it.
///     </para>
/// </remarks>
public sealed class TypeFilterMiddleware : IEventBusMiddleware
{
    public string Name => "type-filter";

    /// <inheritdoc />
    public EventBusSinkKind SinkKind => EventBusSinkKind.Mandatory;

    private readonly ILogger _logger;
    private readonly Type[] _allowedTypes;

    /// <summary>
    ///     Creates a filter that admits exactly the runtime types listed in
    ///     <paramref name="allowedTypes" /> and drops every other event.
    /// </summary>
    /// <param name="logger">Sink for the trace line emitted on a dropped event.</param>
    /// <param name="allowedTypes">
    ///     The event types to admit. Must contain at least one type.
    /// </param>
    /// <exception cref="ArgumentException">
    ///     <paramref name="allowedTypes" /> is null or empty. A filter with no
    ///     allowlist drops nothing and admits nothing new; it is a no-op that
    ///     still forces the bus off its fast path (#478).
    /// </exception>
    public TypeFilterMiddleware(ILogger<TypeFilterMiddleware> logger, params Type[] allowedTypes)
    {
        if (allowedTypes is null || allowedTypes.Length == 0)
        {
            throw new ArgumentException(
                "TypeFilterMiddleware requires at least one allowed event type. An empty allowlist filters "
                + "nothing while still declaring EventBusSinkKind.Mandatory, which keeps the event bus off "
                + "its fast path for no benefit. Pass the event types to admit, or register no filter at "
                + "all. See issue #478.",
                nameof(allowedTypes));
        }

        _logger = logger;
        _allowedTypes = allowedTypes;
    }

    public ValueTask<bool> ProcessAsync(ref AgentEvent @event, CancellationToken ct = default)
    {
        int count = _allowedTypes.Length;
        for (int i = 0; i < count; i++)
        {
            if (_allowedTypes[i] == @event.GetType())
                return ValueTask.FromResult(true);
        }

        _logger.LogTrace("Filtered out event type {Type}", @event.GetType().Name);
        return ValueTask.FromResult(false);
    }
}
