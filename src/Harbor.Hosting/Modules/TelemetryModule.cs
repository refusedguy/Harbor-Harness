using System.Globalization;
using Harbor.Abstractions.Env;
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
///     no ActivityListener/MeterListener attached everything is inert; the
///     opt-in visible sink below is what makes it non-inert.
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

        AddVisibleExport(services, ctx);

        return services;
    }

    /// <summary>
    ///     Attach the visible telemetry sink — but only when a host asked for
    ///     it (#1741/#1742).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Before this, Harbor's instrumentation was complete and
    ///         invisible: the tracer and the metrics sink wrote into the
    ///         canonical <c>ActivitySource</c>/<c>Meter</c>, and with no
    ///         listener attached every span and every measurement went
    ///         nowhere. The sink is built on <c>ActivityListener</c> /
    ///         <c>MeterListener</c> — framework types, no OpenTelemetry SDK —
    ///         so the AOT publish of the CLI carries no OTLP exporter.
    ///     </para>
    ///     <para>
    ///         It is created HERE, eagerly, and not on first resolution: the
    ///         listeners have to be live before the first span of the run
    ///         exists, and nothing in the graph resolves the sink.
    ///     </para>
    /// </remarks>
    private static void AddVisibleExport(IServiceCollection services, HarborCompositionContext ctx)
    {
        TelemetryExportOptions? options = ResolveExportOptions(ctx.Options, ctx.Logger);
        if (options is null)
        {
            return;
        }

        var exporter = new TelemetryTextExporter(
            options,
            ctx.LoggerFactory.CreateLogger<TelemetryTextExporter>());

        services.AddSingleton(exporter);
        TelemetryModuleLog.VisibleExportEnabled(ctx.Logger, options.Target.ToString(), options.FilePath);
    }

    /// <summary>
    ///     Read the export configuration: the environment wins over the
    ///     composition options, and an unreadable value turns the sink OFF
    ///     rather than guessing.
    /// </summary>
    /// <param name="options">The host's composition options.</param>
    /// <param name="logger">Logger for the configuration diagnostics.</param>
    /// <param name="readEnvironment">
    ///     Environment reader. Defaults to the process environment; the
    ///     parameter exists so a test can drive the resolution without
    ///     writing process-wide state.
    /// </param>
    /// <returns>The sink configuration, or <see langword="null" /> when no sink should exist.</returns>
    internal static TelemetryExportOptions? ResolveExportOptions(
        HarborComposeOptions options,
        ILogger logger,
        Func<string, string?>? readEnvironment = null)
    {
        Func<string, string?> read = readEnvironment ?? HarborEnv.Get;

        TelemetryExportTarget target = options.TelemetryExport;

        string? rawTarget = read(HarborEnv.TelemetryExport);
        if (!string.IsNullOrWhiteSpace(rawTarget))
        {
            switch (rawTarget.Trim().ToLowerInvariant())
            {
                case "none":
                case "off":
                    target = TelemetryExportTarget.None;
                    break;
                case "console":
                    target = TelemetryExportTarget.Console;
                    break;
                case "file":
                    target = TelemetryExportTarget.File;
                    break;
                default:
                    TelemetryModuleLog.UnknownExportTarget(logger, HarborEnv.TelemetryExport, rawTarget);
                    return null;
            }
        }

        if (target == TelemetryExportTarget.None)
        {
            return null;
        }

        string? path = options.TelemetryExportPath;
        string? rawPath = read(HarborEnv.TelemetryExportPath);
        if (!string.IsNullOrWhiteSpace(rawPath))
        {
            path = rawPath;
        }

        if (target == TelemetryExportTarget.File && string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(options.HarborDir, "logs", "telemetry.txt");
        }

        int maxRecordBytes = ReadPositiveInt(
            HarborEnv.TelemetryExportMaxRecordBytes,
            options.TelemetryExportMaxRecordBytes,
            TelemetryExportOptions.DefaultMaxRecordBytes,
            read,
            logger);
        int maxBufferBytes = ReadPositiveInt(
            HarborEnv.TelemetryExportMaxBufferBytes,
            options.TelemetryExportMaxBufferBytes,
            TelemetryExportOptions.DefaultMaxBufferBytes,
            read,
            logger);

        return new TelemetryExportOptions
        {
            Target = target,
            FilePath = path,
            MaxRecordBytes = maxRecordBytes,
            MaxBufferBytes = maxBufferBytes,
        };
    }

    /// <summary>Read a positive integer bound: the environment wins, a bad value falls back.</summary>
    private static int ReadPositiveInt(
        string variable,
        int? configured,
        int fallback,
        Func<string, string?> read,
        ILogger logger)
    {
        string? raw = read(variable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return configured ?? fallback;
        }

        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
        {
            return parsed;
        }

        TelemetryModuleLog.InvalidExportBound(logger, variable, raw);
        return configured ?? fallback;
    }
}

/// <summary>Log messages of the telemetry module (source-generated, allocation-free).</summary>
internal static partial class TelemetryModuleLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Telemetry export: {Target} ({Path})")]
    public static partial void VisibleExportEnabled(ILogger logger, string target, string? path);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Unknown {Variable} value '{Value}' — the telemetry export stays off. Expected one of: none, console, file.")]
    public static partial void UnknownExportTarget(ILogger logger, string variable, string value);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Ignoring {Variable}='{Value}' — expected a positive integer.")]
    public static partial void InvalidExportBound(ILogger logger, string variable, string value);
}
