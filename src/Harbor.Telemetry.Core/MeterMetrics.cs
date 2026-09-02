using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Harbor.Diagnostics;

namespace Harbor.Telemetry;

/// <summary>
///     <see cref="IMetrics" /> backed by <see cref="Meter" /> (AOT-safe).
///     Counter/histogram instruments are cached per metric name; tag sets are
///     passed per measurement, matching the modern System.Diagnostics.Metrics API.
/// </summary>
public sealed class MeterMetrics : IMetrics
{
    public static readonly MeterMetrics Instance = new();

    private readonly ConcurrentDictionary<string, Counter<double>> _counters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Histogram<double>> _histograms = new(StringComparer.Ordinal);

    public void Counter(string name, double value = 1, params KeyValuePair<string, object?>[] tags)
    {
        GetCounter(name).Add(value, tags ?? []);
    }

    public void Histogram(string name, double value, params KeyValuePair<string, object?>[] tags)
    {
        GetHistogram(name).Record(value, tags ?? []);
    }

    private Counter<double> GetCounter(string name) =>
        _counters.GetOrAdd(name, static n => HarborTelemetrySources.Instruments.CreateCounter<double>(n));

    private Histogram<double> GetHistogram(string name) =>
        _histograms.GetOrAdd(name, static n => HarborTelemetrySources.Instruments.CreateHistogram<double>(n));
}
