using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Harbor.Telemetry;

/// <summary>
///     OTLP export for Harbor's <see cref="HarborTelemetrySources" /> (sprint3-C T.5).
///     <para>
///         <b>Deployment rule:</b> this assembly may ONLY be referenced by
///         daemon / server publish profiles. The CLI and TUI binaries must not
///         carry the OTEL SDK — attach exporters in-process there is a
///         deployment decision, made here and nowhere else.
///     </para>
/// </summary>
public static class HarborOtlpExporter
{
    /// <summary>
    ///     Attach OTLP trace + metric exporters for the "Harbor.Telemetry"
    ///     source. Returns a composite disposable — dispose on shutdown to flush.
    ///     Endpoint defaults to $OTEL_EXPORTER_OTLP_ENDPOINT (OTel standard).
    /// </summary>
    public static IDisposable Attach(string? endpoint = null)
    {
        var tracerProviderBuilder = Sdk.CreateTracerProviderBuilder()
            .AddSource(HarborTelemetrySources.SourceName);

        var meterProviderBuilder = Sdk.CreateMeterProviderBuilder()
            .AddMeter(HarborTelemetrySources.SourceName);

        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            tracerProviderBuilder.AddOtlpExporter(otlpOptions => otlpOptions.Endpoint = new Uri(endpoint));
            meterProviderBuilder.AddOtlpExporter(otlpOptions => otlpOptions.Endpoint = new Uri(endpoint));
        }
        else
        {
            tracerProviderBuilder.AddOtlpExporter();
            meterProviderBuilder.AddOtlpExporter();
        }

        return new CompositeDisposable(
            tracerProviderBuilder.Build(),
            meterProviderBuilder.Build());
    }

    private sealed class CompositeDisposable(params IDisposable[] disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (IDisposable disposable in disposables)
            {
                disposable.Dispose();
            }
        }
    }
}
