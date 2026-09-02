namespace Harbor.Diagnostics;

/// <summary>Tracer that records nothing — the default when telemetry is off.</summary>
public sealed class NullTracer : ITracer
{
    public static readonly NullTracer Instance = new();

    private NullTracer()
    {
    }

    public ITelemetrySpan? StartSpan(string name, params KeyValuePair<string, object?>[] tags)
        => null;
}

/// <summary>Metrics sink that drops everything — the default when telemetry is off.</summary>
public sealed class NullMetrics : IMetrics
{
    public static readonly NullMetrics Instance = new();

    private NullMetrics()
    {
    }

    public void Counter(string name, double value = 1, params KeyValuePair<string, object?>[] tags)
    {
    }

    public void Histogram(string name, double value, params KeyValuePair<string, object?>[] tags)
    {
    }
}
