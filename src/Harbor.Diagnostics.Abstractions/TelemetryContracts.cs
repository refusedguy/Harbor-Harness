namespace Harbor.Diagnostics;

/// <summary>
///     A single telemetry span. Disposable: dispose ends the span. Spans are
///     sampled — <see cref="IsRecorded" /> is false when no listener cares, and
///     callers should skip expensive attribute computation in that case.
/// </summary>
public interface ITelemetrySpan : IDisposable
{
    /// <summary>False when the span is not being recorded (sampling off).</summary>
    bool IsRecorded { get; }

    /// <summary>Attach an attribute (e.g. tool.name, model, session.id).</summary>
    void SetTag(string key, object? value);

    /// <summary>Mark the span as failed with an optional description.</summary>
    void SetError(string? description = null);
}

/// <summary>
///     Distributed-tracing contract (sprint3-C). Core layers depend on this
///     interface only; the ActivitySource-backed implementation lives in
///     Harbor.Telemetry.Core and OTEL export in Harbor.Telemetry.Otlp.
/// </summary>
public interface ITracer
{
    /// <summary>
    ///     Start a span named <paramref name="name" /> (dot-separated, e.g.
    ///     "tool.execute", "llm.stream", "agent.turn"). Returns null when
    ///     tracing is disabled entirely.
    /// </summary>
    ITelemetrySpan? StartSpan(string name, params KeyValuePair<string, object?>[] tags);
}

/// <summary>
///     Metrics contract (sprint3-C): counters for rates/events and histograms
///     for distributions (durations, sizes). Tag cardinality is the caller's
///     responsibility — keep tag values bounded (names, statuses; never ids).
/// </summary>
public interface IMetrics
{
    /// <summary>Add <paramref name="value" /> to a monotonically increasing counter.</summary>
    void Counter(string name, double value = 1, params KeyValuePair<string, object?>[] tags);

    /// <summary>Record a value into a distribution histogram (duration ms, token counts, context size).</summary>
    void Histogram(string name, double value, params KeyValuePair<string, object?>[] tags);
}
