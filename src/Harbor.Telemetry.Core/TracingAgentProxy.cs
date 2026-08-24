using System.Diagnostics;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Diagnostics;

namespace Harbor.Telemetry;

/// <summary>
///     Forwarding <see cref="IAgent" /> decorator (sprint3-C C1). Adds:
///     <list type="bullet">
///         <item>span agent.turn{agent.name} around both prompt overloads,</item>
///         <item>turn.count / turn.duration.ms metrics,</item>
///         <item>an ambient <see cref="Correlation" /> scope so every nested
///         span/metric and log line carries session.id/agent/turn.</item>
///     </list>
/// </summary>
public sealed class TracingAgentProxy(IAgent inner, IMetrics metrics, ITracer tracer) : IAgent
{
    private int _turnCounter;

    public AgentState State => inner.State;

    public CancellationTokenSource AbortSource => inner.AbortSource;

    public void ResetAbortSource() => inner.ResetAbortSource();

    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener) =>
        inner.Subscribe(listener);

    public void Initialize(Session session, AgentDefinition agent) => inner.Initialize(session, agent);

    public void Steer(AgentMessage message) => inner.Steer(message);

    public async Task<Result> PromptAsync(string text, CancellationToken ct = default)
        => await InstrumentTurn(() => inner.PromptAsync(text, ct)).ConfigureAwait(false);

    public async Task<Result> PromptAsync(UserMessage message, CancellationToken ct = default)
        => await InstrumentTurn(() => inner.PromptAsync(message, ct)).ConfigureAwait(false);

    public Task WaitForIdleAsync(CancellationToken ct = default) => inner.WaitForIdleAsync(ct);

    public void Dispose() => inner.Dispose();

    private async Task<Result> InstrumentTurn(Func<Task<Result>> run)
    {
        // State is null before Initialize; fall back to unknown tags.
        string agentName = inner.State?.Agent.Name.Value ?? "unknown";
        string? sessionId = inner.State?.SessionId;

        using Harbor.Diagnostics.ITelemetrySpan? span = tracer.StartSpan(
            "agent.turn",
            new KeyValuePair<string, object?>(TelemetryTagNames.Agent, agentName),
            new KeyValuePair<string, object?>(TelemetryTagNames.SessionId, sessionId));

        // AgentState.CurrentTurn is known-stale (deep2-core F9 note); count
        // turns locally instead so the correlation value is monotonic and real.
        int turn = Interlocked.Increment(ref _turnCounter);
        using IDisposable correlation = Correlation.Push(new CorrelationContext(sessionId, agentName, turn));
        long start = Stopwatch.GetTimestamp();
        Result result;
        try
        {
            result = await run().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            span?.SetError(ex.Message);
            RecordOutcome(agentName, "exception", start);
            throw;
        }

        RecordOutcome(agentName, result.IsSuccess ? "ok" : "failure", start);
        if (result.IsFailure)
        {
            span?.SetError(result.Error);
        }

        return result;
    }

    private void RecordOutcome(string agentName, string status, long start)
    {
        metrics.Counter(
            TelemetryTagNames.TurnCount, 1,
            new KeyValuePair<string, object?>(TelemetryTagNames.Agent, agentName),
            new KeyValuePair<string, object?>("turn.status", status));
        metrics.Histogram(
            TelemetryTagNames.TurnDurationMs, Stopwatch.GetElapsedTime(start).TotalMilliseconds,
            new KeyValuePair<string, object?>(TelemetryTagNames.Agent, agentName));
    }
}
