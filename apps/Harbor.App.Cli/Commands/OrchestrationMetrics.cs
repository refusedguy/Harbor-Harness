using Harbor.Diagnostics;
using Harbor.Telemetry;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     #2674 G1: the four orchestration instruments missing from Harbor's
///     telemetry surface. Each is a counter under <see cref="MeterMetrics" />
///     and the canonical <c>Harbor.Telemetry</c> Meter, paired with a duration
///     histogram of the same name plus the <c>.duration.ms</c> suffix. Every
///     recording carries a bounded operation name and a success/failure status,
///     never an id or a free-form command line.
/// </summary>
internal static class OrchestrationInstrumentNames
{
    internal const string Commands = "harbor.commands";
    internal const string EffectsClaim = "harbor.effects.claim";
    internal const string Runs = "harbor.runs";
    internal const string GitCommands = "harbor.git.commands";

    internal const string DurationSuffix = ".duration.ms";
    internal const string Operation = "orchestration.operation";
    internal const string Status = "orchestration.status";
    internal const string Success = "success";
    internal const string Failure = "failure";

    internal static string DurationInstrument(string counter) => string.Concat(counter, DurationSuffix);
}

/// <summary>
///     Records one orchestration operation as a counter plus a duration
///     histogram. The sink is <see cref="IMetrics" />, so the same recording is
///     visible to a <c>MeterListener</c>, the text exporter and the OTLP
///     exporter with no extra wiring. Pure apart from the sink: tests pass a
///     recording double and assert the exact names, values and tags.
/// </summary>
internal sealed class OrchestrationMetrics
{
    private readonly IMetrics _metrics;

    internal OrchestrationMetrics(IMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        _metrics = metrics;
    }

    /// <summary>A process launched through the drained CLI process seam.</summary>
    internal void Command(string operation, TimeSpan duration, bool success) =>
        Record(OrchestrationInstrumentNames.Commands, operation, duration, success);

    /// <summary>One effect claimed from an outbox (the seam is not wired yet).</summary>
    internal void EffectClaim(string operation, TimeSpan duration, bool success) =>
        Record(OrchestrationInstrumentNames.EffectsClaim, operation, duration, success);

    /// <summary>One agent run started by a CLI verb or the REPL.</summary>
    internal void Run(string operation, TimeSpan duration, bool success) =>
        Record(OrchestrationInstrumentNames.Runs, operation, duration, success);

    /// <summary>A git command, so forge traffic can be separated from other commands.</summary>
    internal void GitCommand(string operation, TimeSpan duration, bool success) =>
        Record(OrchestrationInstrumentNames.GitCommands, operation, duration, success);

    private void Record(string counter, string operation, TimeSpan duration, bool success)
    {
        KeyValuePair<string, object?>[] tags =
        [
            new(OrchestrationInstrumentNames.Operation, string.IsNullOrEmpty(operation) ? "unknown" : operation),
            new(OrchestrationInstrumentNames.Status, success ? OrchestrationInstrumentNames.Success : OrchestrationInstrumentNames.Failure),
        ];

        _metrics.Counter(counter, 1, tags);
        _metrics.Histogram(OrchestrationInstrumentNames.DurationInstrument(counter), Math.Max(0d, duration.TotalMilliseconds), tags);
    }
}
