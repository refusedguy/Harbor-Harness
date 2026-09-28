using Microsoft.Extensions.Logging;

namespace Harbor.Registries.Events;

/// <summary>
///     Samples <see cref="MessageUpdateEvent" />s at a configurable rate.
///     Useful for reducing event traffic to TUI renderers when the LLM is
///     streaming many token deltas.
/// </summary>
/// <remarks>
///     <para>
///         Uses pattern matching (<c>is MessageUpdateEvent</c>) — a sealed
///         type check with zero reflection overhead. Returns
///         <see cref="ValueTask.FromResult{T}(T)" /> for synchronous paths
///         to avoid heap allocation.
///     </para>
///     <para>
///         <b>Verdict (#47/S3): <see cref="EventBusSinkKind.Optional" />.</b>
///         This sink's entire purpose is to throw events away for the sake of
///         cheaper downstream rendering — it is a diagnostic throttle, not a
///         state, audit, accounting or telemetry record. Nothing downstream
///         becomes wrong when it stops seeing an event, so a bus whose only
///         sinks are samplers may take the zero-subscriber fast path; the bus
///         still drains it there and counts the drain
///         (<c>InMemoryEventBus.OptionalSinkDrainCount</c>), so the cost of the
///         sampler never becomes a silent skip.
///     </para>
/// </remarks>
public sealed class SamplingMiddleware : IEventBusMiddleware
{
    public string Name => "sampling";

    /// <inheritdoc />
    public EventBusSinkKind SinkKind => EventBusSinkKind.Optional;

    private readonly ILogger _logger;
    private readonly double _rate;
    private readonly Random _rnd;

    public SamplingMiddleware(ILogger<SamplingMiddleware> logger, double rate = 0.1)
    {
        _logger = logger;
        _rate = rate;
        _rnd = new Random();
    }

    public ValueTask<bool> ProcessAsync(ref AgentEvent @event, CancellationToken ct = default)
    {
        if (@event is MessageUpdateEvent)
        {
            if (_rnd.NextDouble() > _rate)
            {
                _logger.LogTrace("Sampled out MessageUpdateEvent");
                return ValueTask.FromResult(false);
            }
        }
        return ValueTask.FromResult(true);
    }
}
