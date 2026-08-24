using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Harbor.Telemetry;

/// <summary>
///     Single ActivitySource + Meter pair for the whole process (O10 fix:
///     three private "Harbor" sources consolidated into one canonical point).
///     Listeners opt in via <see cref="ActivityListener" /> /
///     <see cref="MeterListener" />; with no listeners the cost is a few
///     nanoseconds per StartActivity call.
/// </summary>
public static class HarborTelemetrySources
{
    public const string SourceName = "Harbor.Telemetry";

    public static readonly ActivitySource Tracing = new(SourceName, "0.4.0");

    public static readonly Meter Instruments = new(SourceName, "0.4.0");
}
