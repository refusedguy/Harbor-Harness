using Microsoft.Extensions.Logging;

namespace Harbor.Core.Events;

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
/// </remarks>
public sealed class SamplingMiddleware : IEventBusMiddleware
{
    public string Name => "sampling";

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
