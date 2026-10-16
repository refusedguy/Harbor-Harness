using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Sessions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Application.Tests;

/// <summary>
///     Parity between the <see cref="ICompactionView" /> forwarder and the
///     <see cref="CompactionPolicy" /> statics it wraps (#1612): the seam adds
///     no behavior, so every member must return exactly what the static does
///     for the same inputs.
/// </summary>
public class CompactionViewTests
{
    private static readonly ICompactionView View = CompactionView.Instance;

    private static ModelInfo Model(int contextWindow) => new(
        "test-model", "test", "Test Model", contextWindow, 4_096,
        false, false, true, Pricing.Unknown, "openai");

    private static UserMessage Msg(string content) => new(
        Guid.NewGuid().ToString("N"),
        "s1",
        DateTimeOffset.UtcNow,
        content,
        "code",
        "test-model");

    private static AssistantMessage SummaryMsg() => new(
        Guid.NewGuid().ToString("N"),
        "s1",
        DateTimeOffset.UtcNow,
        [new TextPart("summary")],
        StopReason.Stop,
        new Usage(0, 0),
        "test-model",
        IsSummary: true);

    /// <summary>Both sides must build the same compacted view element-for-element.</summary>
    private static async Task AssertSameView(
        IReadOnlyList<AgentMessage> viaView, IReadOnlyList<AgentMessage> viaStatic)
    {
        await Assert.That(viaView.Count).IsEqualTo(viaStatic.Count);
        for (int i = 0; i < viaView.Count; i++)
        {
            await Assert.That(ReferenceEquals(viaView[i], viaStatic[i])).IsTrue();
        }
    }

    [Test]
    public async Task MaterializeCompactedView_ParityWithPolicy()
    {
        var history = new List<AgentMessage> { Msg("a"), SummaryMsg(), Msg("b"), Msg("c") };

        await AssertSameView(
            View.MaterializeCompactedView(history),
            CompactionPolicy.MaterializeCompactedView(history));
    }

    [Test]
    public async Task TruncateToFitStrict_ParityWithPolicy()
    {
        var history = new List<AgentMessage>
        {
            Msg(new string('a', 20_000)),
            Msg(new string('b', 20_000)),
            Msg(new string('c', 20_000)),
            Msg(new string('d', 20_000)),
            Msg(new string('e', 20_000)),
            Msg(new string('f', 20_000)),
        };
        var tracker = new TokenTracker();

        await AssertSameView(
            View.TruncateToFitStrict(history, Model(4_096), tracker),
            CompactionPolicy.TruncateToFitStrict(history, Model(4_096), tracker));
    }

    [Test]
    public async Task TruncateToFit_ParityWithPolicy()
    {
        var history = new List<AgentMessage>
        {
            Msg(new string('a', 20_000)),
            Msg(new string('b', 20_000)),
            Msg(new string('c', 20_000)),
        };
        var tracker = new TokenTracker();

        await AssertSameView(
            View.TruncateToFit(history, Model(4_096), tracker),
            CompactionPolicy.TruncateToFit(history, Model(4_096), tracker));
    }
}
