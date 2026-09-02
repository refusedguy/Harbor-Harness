using System.Diagnostics;
using Harbor.Diagnostics;

namespace Harbor.Telemetry;

/// <summary>
///     <see cref="ITracer" /> backed by <see cref="ActivitySource" /> (AOT-safe,
///     no OTEL SDK). Auto-tags the ambient correlation context onto every span.
/// </summary>
public sealed class ActivityTracer : ITracer
{
    public static readonly ActivityTracer Instance = new();

    public ActivityTracer()
    {
    }

    public ITelemetrySpan? StartSpan(string name, params KeyValuePair<string, object?>[] tags)
    {
        Activity? activity = HarborTelemetrySources.Tracing.StartActivity(name);
        if (activity is null)
        {
            return null;
        }

        CorrelationContext correlation = Correlation.Current;
        if (correlation.SessionId is { } sessionId)
        {
            activity.SetTag(TelemetryTagNames.SessionId, sessionId);
        }

        if (correlation.AgentName is { } agentName)
        {
            activity.SetTag(TelemetryTagNames.Agent, agentName);
        }

        if (correlation.Turn > 0)
        {
            activity.SetTag(TelemetryTagNames.Turn, correlation.Turn);
        }

        if (tags is not null)
        {
            foreach ((string key, object? value) in tags)
            {
                activity.SetTag(key, value);
            }
        }

        return new ActivitySpan(activity);
    }

    private sealed class ActivitySpan(Activity activity) : ITelemetrySpan
    {
        public bool IsRecorded => activity.IsAllDataRequested;

        public void SetTag(string key, object? value)
        {
            if (activity.IsAllDataRequested)
            {
                activity.SetTag(key, value);
            }
        }

        public void SetError(string? description = null)
        {
            if (!activity.IsAllDataRequested)
            {
                return;
            }

            activity.SetStatus(ActivityStatusCode.Error, description);
        }

        public void Dispose() => activity.Dispose();
    }
}

/// <summary>Canonical span/metric attribute and instrument names.</summary>
public static class TelemetryTagNames
{
    public const string SessionId = "session.id";
    public const string Agent = "agent.name";
    public const string Turn = "agent.turn";
    public const string ToolName = "tool.name";
    public const string ToolStatus = "tool.status";
    public const string Model = "llm.model";
    public const string Provider = "llm.provider";
    public const string StreamStatus = "llm.status";

    // Counters / histograms (sprint3-C T.6).
    public const string ToolCalls = "tool.calls";
    public const string ToolDurationMs = "tool.duration.ms";
    public const string LlmTtfbMs = "llm.ttfb.ms";
    public const string LlmTokens = "llm.tokens";
    public const string LlmTokenType = "token.type";
    public const string TurnCount = "turn.count";
    public const string TurnDurationMs = "turn.duration.ms";
    public const string ContextSize = "session.context.size";
    public const string ContextPhase = "context.phase";
}
