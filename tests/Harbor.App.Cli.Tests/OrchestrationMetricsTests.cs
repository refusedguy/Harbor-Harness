using Harbor.App.Cli.Commands;
using Harbor.Diagnostics;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #2674 G1. The orchestration recorder is pure apart from its
///     <see cref="IMetrics" /> sink, so the instrument names, values and tag
///     shape can be pinned without a process, a MeterListener or an OTLP host.
/// </summary>
public class OrchestrationMetricsTests
{
    private static readonly string[] CounterNames =
    [
        "harbor.commands",
        "harbor.effects.claim",
        "harbor.runs",
        "harbor.git.commands",
    ];

    [Test]
    public async Task EveryOperation_RecordsOneCounterAndOneDurationHistogram()
    {
        var sink = new CapturingMetrics();
        var metrics = new OrchestrationMetrics(sink);
        var duration = TimeSpan.FromMilliseconds(125);

        metrics.Command("gh", duration, true);
        metrics.EffectClaim("outbox", duration, true);
        metrics.Run("ask", duration, true);
        metrics.GitCommand("git", duration, true);

        await Assert.That(sink.Counters.Count).IsEqualTo(4);
        await Assert.That(sink.Histograms.Count).IsEqualTo(4);
        await Assert.That(sink.Counters.Select(c => c.Name)).IsEquivalentTo(CounterNames);

        foreach (string counter in CounterNames)
        {
            await Assert.That(sink.Histograms.Count(h => h.Name == counter + ".duration.ms")).IsEqualTo(1);
            await Assert.That(sink.Histograms.Single(h => h.Name == counter + ".duration.ms").Value).IsEqualTo(125d);
        }

        await Assert.That(sink.Counters.All(c => c.Value == 1d)).IsTrue();
    }

    [Test]
    public async Task StatusAndOperation_AreBoundedTags_NotIdsOrCommandLines()
    {
        var sink = new CapturingMetrics();
        var metrics = new OrchestrationMetrics(sink);

        metrics.GitCommand("git", TimeSpan.FromMilliseconds(10), false);
        metrics.Command(string.Empty, TimeSpan.Zero, true);

        KeyValuePair<string, object?>[] failed = sink.Counters[0].Tags;
        await Assert.That(Tag(failed, "orchestration.operation")).IsEqualTo("git");
        await Assert.That(Tag(failed, "orchestration.status")).IsEqualTo("failure");
        await Assert.That(Tag(sink.Histograms[0].Tags, "orchestration.status")).IsEqualTo("failure");

        KeyValuePair<string, object?>[] emptyOperation = sink.Counters[1].Tags;
        await Assert.That(Tag(emptyOperation, "orchestration.operation")).IsEqualTo("unknown");
        await Assert.That(Tag(emptyOperation, "orchestration.status")).IsEqualTo("success");
    }

    [Test]
    public async Task NegativeDuration_ClampsToZero()
    {
        var sink = new CapturingMetrics();
        var metrics = new OrchestrationMetrics(sink);

        metrics.Run("ask", TimeSpan.FromMilliseconds(-5), true);

        await Assert.That(sink.Histograms[0].Value).IsEqualTo(0d);
        await Assert.That(sink.Counters[0].Name).IsEqualTo("harbor.runs");
    }

    private static object? Tag(IReadOnlyList<KeyValuePair<string, object?>> tags, string key)
    {
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (string.Equals(tag.Key, key, StringComparison.Ordinal)) return tag.Value;
        }

        return null;
    }

    private sealed class CapturingMetrics : IMetrics
    {
        internal List<(string Name, double Value, KeyValuePair<string, object?>[] Tags)> Counters { get; } = [];

        internal List<(string Name, double Value, KeyValuePair<string, object?>[] Tags)> Histograms { get; } = [];

        public void Counter(string name, double value = 1, params KeyValuePair<string, object?>[] tags) =>
            Counters.Add((name, value, tags));

        public void Histogram(string name, double value, params KeyValuePair<string, object?>[] tags) =>
            Histograms.Add((name, value, tags));
    }
}
