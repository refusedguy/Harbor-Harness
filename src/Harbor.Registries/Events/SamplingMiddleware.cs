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
///     <para>
///         Middleware instances may be shared across concurrent publishers, so
///         the generator is guarded by a lock: draws are serialized and the
///         instance stays safe under parallel <c>PublishAsync</c> calls. The lock
///         allocates no managed memory, keeping <c>ProcessAsync</c> allocation-free.
///     </para>
/// </remarks>
public sealed class SamplingMiddleware : IEventBusMiddleware
{
    public string Name => "sampling";

    /// <inheritdoc />
    public EventBusSinkKind SinkKind => EventBusSinkKind.Optional;

    private readonly ILogger _logger;
    private readonly double _rate;
    private readonly object _rngGate = new();
    private readonly Random _rng = new();

    public SamplingMiddleware(ILogger<SamplingMiddleware> logger, double rate = 0.1)
    {
        _logger = logger;
        _rate = rate;
    }

    public ValueTask<bool> ProcessAsync(ref AgentEvent @event, CancellationToken ct = default)
    {
        if (@event is MessageUpdateEvent)
        {
            double sample;
            lock (_rngGate)
            {
                sample = _rng.NextDouble();
            }
            if (sample > _rate)
            {
                SamplingMiddlewareLog.MessageUpdateSampledOut(_logger);
                return ValueTask.FromResult(false);
            }
        }
        return ValueTask.FromResult(true);
    }
}

/// <summary>
///     SG3: BCL <c>[LoggerMessage]</c> delegates for <see cref="SamplingMiddleware" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class SamplingMiddlewareLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Trace, Message = "Sampled out MessageUpdateEvent")]
    public static partial void MessageUpdateSampledOut(ILogger logger);
}
