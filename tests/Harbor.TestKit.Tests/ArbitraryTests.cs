// #2704 D02. Behavior tests for the seeded event-transcript generator.
// Every case observes what a caller gets from EventStream: the same walk for
// the same seed, a structurally valid transcript, and the argument errors.
// Nothing here inspects which builder produced a given event.

using Harbor.Abstractions.Events;

namespace Harbor.TestKit.Tests;

public class ArbitraryTests
{
    [Test]
    public async Task SameSeed_ReplaysTheSameTranscript()
    {
        string first = Signature(Arbitrary.EventStream(new Random(4242), 8));
        string second = Signature(Arbitrary.EventStream(new Random(4242), 8));

        await Assert.That(first).IsEqualTo(second)
            .Because("a seed is only useful when a second run walks the same events, so a red test can be replayed");
        await Assert.That(first.Length).IsGreaterThan(0)
            .Because("an eight-turn transcript must contain events");
    }

    [Test]
    public async Task DifferentSeeds_DiversifyTheTranscript()
    {
        var signatures = new HashSet<string>();
        for (int seed = 0; seed < 32; seed++)
        {
            signatures.Add(Signature(Arbitrary.EventStream(new Random(seed), 8)));
        }

        await Assert.That(signatures.Count).IsGreaterThanOrEqualTo(16)
            .Because("one fixed shape would turn a property test into a repeated fixture");
    }

    [Test]
    public async Task GeneratedTranscripts_AreWellFormed()
    {
        for (int seed = 0; seed < 50; seed++)
        {
            IReadOnlyList<AgentEvent> stream = Arbitrary.EventStream(new Random(seed), 6);

            await Assert.That(EventTranscriptAssert.FindViolations(stream)).IsEmpty()
                .Because($"seed {seed} produced a transcript the structural assertion rejects");
            EventTranscriptAssert.AssertWellFormed(stream);
        }
    }

    [Test]
    public async Task Count_BoundsTheTurnsAndFraming()
    {
        IReadOnlyList<AgentEvent> empty = Arbitrary.EventStream(new Random(1), 0);
        await Assert.That(empty).IsEmpty()
            .Because("zero turns must not smuggle in a bare run frame");

        IReadOnlyList<AgentEvent> stream = Arbitrary.EventStream(new Random(1), 3);
        await Assert.That(stream.Count).IsGreaterThanOrEqualTo(8)
            .Because("three turns need a run start, a start/end pair per turn, and a run end");
        await Assert.That(stream[0] is AgentStartEvent).IsTrue()
            .Because("a transcript opens with the run start");
        await Assert.That(stream[^1] is AgentEndEvent).IsTrue()
            .Because("a complete transcript closes with the run end");
    }

    [Test]
    public async Task InvalidArguments_AreRejected()
    {
        ArgumentNullException nullRng = Assert.Throws<ArgumentNullException>(
            () => Arbitrary.EventStream(null!, 1));
        await Assert.That(nullRng.ParamName).IsEqualTo("rng")
            .Because("the caller must see which argument was missing");

        ArgumentOutOfRangeException negativeCount = Assert.Throws<ArgumentOutOfRangeException>(
            () => Arbitrary.EventStream(new Random(1), -1));
        await Assert.That(negativeCount.ParamName).IsEqualTo("count")
            .Because("a negative turn count is a caller error, not an empty stream");
    }

    /// <summary>
    ///     The observable shape of a walk: event kinds plus the ids a caller
    ///     correlates on. Assistant-message ids are freshly generated inside
    ///     the builders, so they are deliberately not part of the signature.
    /// </summary>
    private static string Signature(IReadOnlyList<AgentEvent> events) =>
        string.Join("|", events.Select(Describe));

    private static string Describe(AgentEvent item) => item switch
    {
        ToolExecutionStartEvent start => $"{nameof(ToolExecutionStartEvent)}({start.ToolCallId})",
        ToolExecutionUpdateEvent update => $"{nameof(ToolExecutionUpdateEvent)}({update.ToolCallId})",
        ToolExecutionEndEvent end => $"{nameof(ToolExecutionEndEvent)}({end.ToolCallId})",
        CompactionStartedEvent started => $"{nameof(CompactionStartedEvent)}({started.SessionId})",
        CompactionCompletedEvent completed => $"{nameof(CompactionCompletedEvent)}({completed.SessionId})",
        CompactionFailedEvent failed => $"{nameof(CompactionFailedEvent)}({failed.SessionId})",
        _ => item.GetType().Name,
    };
}
