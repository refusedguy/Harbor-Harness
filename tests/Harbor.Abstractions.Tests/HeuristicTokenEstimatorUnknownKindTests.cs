using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;

namespace Harbor.Abstractions.Tests;
/// <summary>
///     #461 — <see cref="HeuristicTokenEstimator" /> walked the message tree with
///     two hand-rolled switches whose <c>default:</c> arms billed any unrecognised
///     case at a flat 50 tokens. A part kind nobody had taught the estimator was
///     therefore priced as if it were an empty message, and the compaction budget
///     drifted with no trace.
/// </summary>
public class HeuristicTokenEstimatorUnknownKindTests
{
    private static readonly DateTimeOffset Ts = DateTimeOffset.UnixEpoch;

    [Test]
    public async Task EstimateMessage_KnownKinds_KeepTheirExistingNumbers()
    {
        var estimator = new HeuristicTokenEstimator();
        var assistant = new AssistantMessage(
            "m1",
            "s",
            Ts,
            new ContentPart[] { new TextPart("hello world"), new FilePart("a.png", "image/png", 1) },
            StopReason.Stop,
            new Usage(0, 0),
            "test-model");

        // 11 chars -> ceil(11/4) = 3, plus the flat 200 for the file, plus the
        // 100-token message framing. Same arithmetic the switch used to do.
        await Assert.That(estimator.EstimateMessage(assistant)).IsEqualTo(303);
    }

    [Test]
    public async Task EstimateMessage_UnknownPartKind_RefusesIt()
    {
        var assistant = new AssistantMessage(
            "m1",
            "s",
            Ts,
            new ContentPart[] { new RoguePart("payload") },
            StopReason.Stop,
            new Usage(0, 0),
            "test-model");

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => new HeuristicTokenEstimator().EstimateMessage(assistant));

        await Assert.That(ex.Message.Contains("RoguePart", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task EstimateMessage_UnknownMessageRole_RefusesIt()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => new HeuristicTokenEstimator().EstimateMessage(new RogueMessage("m1", "s", Ts)));

        await Assert.That(ex.Message.Contains("RogueMessage", StringComparison.Ordinal)).IsTrue();
    }

    private sealed record RoguePart(string Text) : ContentPart
    {
        public override string Type => "rogue";
    }

    private sealed record RogueMessage(string Id, string SessionId, DateTimeOffset CreatedAt)
        : AgentMessage(Id, SessionId, CreatedAt)
    {
        public override string Role => "rogue";
    }
}
