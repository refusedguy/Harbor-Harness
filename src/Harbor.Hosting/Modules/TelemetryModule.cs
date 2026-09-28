using Harbor.Abstractions.Events;
using Harbor.Diagnostics;
using Harbor.Telemetry;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Hosting;

/// <summary>
///     sprint3-C activation chain: registers the ActivitySource/Meter-backed
///     ITracer/IMetrics singletons. Decorators (InstrumentedToolRegistry,
///     InstrumentedProviderRegistry, TracingAgentProxy) wrap the registries in
///     AddHarborRegistries/AddHarborCore — one chain, no per-app wiring. With
///     no ActivityListener/MeterListener attached everything is inert; attach
///     listeners or the OTLP exporter (daemon publish profiles only) to observe.
/// </summary>
internal static class TelemetryModule
{
    /// <summary>
    ///     Register the tracer/metrics singletons plus the event-bus queue-age
    ///     reporter (#47/S2).
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="ctx">Composition context; carries the already-constructed event bus.</param>
    /// <remarks>
    ///     The reporter is the seam that lets the telemetry layer read the
    ///     publish-envelope counters without a forbidden reference: the bus
    ///     (Harbor.Registries) and the reporter (Harbor.Telemetry.Core) meet on
    ///     <see cref="IEventBusQueueMetrics" />, and the composition root — the
    ///     one place allowed to know both — passes the instance over. A bus
    ///     that is not instrumented (a stub, a remote one) is simply not
    ///     registered: the metrics surface then carries nothing for the event
    ///     bus instead of a fake zero.
    ///     <para>
    ///         The reporter instance is built here (not on first resolution)
    ///         because its cadence is what produces the numbers, and the
    ///         container disposes it with the host, which stops the loop.
    ///     </para>
    /// </remarks>
    internal static IServiceCollection AddHarborTelemetry(
        this IServiceCollection services,
        HarborCompositionContext ctx)
    {
        services.AddSingleton<ITracer>(ActivityTracer.Instance);
        services.AddSingleton<IMetrics>(MeterMetrics.Instance);

        if (ctx.EventBus is IEventBusQueueMetrics queueMetrics)
        {
            services.AddSingleton(new EventBusQueueAgeReporter(
                queueMetrics,
                MeterMetrics.Instance,
                ctx.LoggerFactory.CreateLogger<EventBusQueueAgeReporter>(),
                ctx.Options.EventBusQueueAgeReportInterval));
        }

        return services;
    }
}
