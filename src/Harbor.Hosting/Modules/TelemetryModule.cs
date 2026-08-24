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
    internal static IServiceCollection AddHarborTelemetry(this IServiceCollection services)
    {
        services.AddSingleton<ITracer>(ActivityTracer.Instance);
        services.AddSingleton<IMetrics>(MeterMetrics.Instance);
        return services;
    }
}
