// #2704 E03 — behavior tests for the event-transcript well-formedness check.
// Every case observes what a caller gets back: the violation list, or the
// exception AssertWellFormed raises. None reaches into the helper's state.

using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.TestKit;

namespace Harbor.TestKit.Tests;

public class EventTranscriptAssertTests
{
    [Test]
    public async Task EmptyTranscript_HasNoViolations()
    {
        IReadOnlyList<string> violations = EventTranscriptAssert.FindViolations([]);

        await Assert.That(violations).IsEmpty();
        EventTranscriptAssert.AssertWellFormed(Array.Empty<AgentEvent>());
    }

    [Test]
    public async Task FullWellFormedRun_HasNoViolations()
    {
        AgentEvent[] events =
        [
            TestEvents.AgentStart(),
            TestEvents.TurnStart(),
            TestEvents.MessageStart(),
            TestEvents.TextDeltaUpdate(),
            TestEvents.MessageEnd(),
            TestEvents.ToolExecutionStart("tc9", "read"),
            TestEvents.ToolExecutionUpdate("tc9"),
            TestEvents.ToolExecutionEnd("tc9", ToolResult.Success("ok")),
            TestEvents.TurnEnd(),
            TestEvents.AgentEnd(),
        ];

        await Assert.That(EventTranscriptAssert.FindViolations(events)).IsEmpty();
        EventTranscriptAssert.AssertWellFormed(events);
    }

    [Test]
    public async Task CompactionPair_HasNoViolations()
    {
        AgentEvent[] events =
        [
            TestEvents.CompactionStarted("s1"),
            TestEvents.CompactionCompleted("s1"),
        ];

        await Assert.That(EventTranscriptAssert.FindViolations(events)).IsEmpty();
    }

    /// <summary>
    ///     One shape per lifecycle rule. A malformed transcript must report the
    ///     specific broken pair, not merely "something was wrong".
    /// </summary>
    [Test]
    [Arguments(Malformed.BareToolEnd, "ToolExecutionEnd for 'tcX' without an open start")]
    [Arguments(Malformed.SecondToolEnd, "[2] ToolExecutionEnd for 'tc1' without an open start")]
    [Arguments(Malformed.OrphanToolUpdate, "ToolExecutionUpdate for 'tc1' with no open start")]
    [Arguments(Malformed.ReusedToolId, "reuses an earlier call id")]
    [Arguments(Malformed.MessageEnd, "MessageEnd without a preceding MessageStart")]
    [Arguments(Malformed.TurnEnd, "TurnEnd without a preceding TurnStart")]
    [Arguments(Malformed.AgentEnd, "AgentEnd without a preceding AgentStart")]
    [Arguments(Malformed.CompactionEnd, "CompactionFailed for 's1' without an open CompactionStarted")]
    public async Task MalformedTranscript_ReportsTheBrokenPair(Malformed kind, string expected)
    {
        AgentEvent[] events = Transcript(kind);

        IReadOnlyList<string> violations = EventTranscriptAssert.FindViolations(events);

        await Assert.That(violations.Count).IsEqualTo(1);
        await Assert.That(violations[0]).Contains(expected);
    }

    [Test]
    public async Task BareToolEnd_AssertAlsoThrowsCarryingTheViolation()
    {
        AgentEvent[] events = [TestEvents.ToolExecutionEnd("tcX")];

        EventTranscriptException thrown = Assert.Throws<EventTranscriptException>(
            () => EventTranscriptAssert.AssertWellFormed(events));

        await Assert.That(thrown.Violations.Count).IsEqualTo(1);
        await Assert.That(thrown.Message).Contains("ToolExecutionEnd for 'tcX'");
    }

    [Test]
    public async Task SeveralViolations_AreAllReported()
    {
        AgentEvent[] events =
        [
            TestEvents.AgentEnd(),
            TestEvents.MessageEnd(),
            TestEvents.ToolExecutionEnd("tc1"),
        ];

        EventTranscriptException thrown = Assert.Throws<EventTranscriptException>(
            () => EventTranscriptAssert.AssertWellFormed(events));

        await Assert.That(thrown.Violations.Count).IsEqualTo(3);
        await Assert.That(thrown.Message).Contains("AgentEnd without");
        await Assert.That(thrown.Message).Contains("MessageEnd without");
        await Assert.That(thrown.Message).Contains("ToolExecutionEnd for 'tc1'");
    }

    private enum Malformed
    {
        BareToolEnd,
        SecondToolEnd,
        OrphanToolUpdate,
        ReusedToolId,
        MessageEnd,
        TurnEnd,
        AgentEnd,
        CompactionEnd,
    }

    private static AgentEvent[] Transcript(Malformed kind) => kind switch
    {
        Malformed.BareToolEnd => [TestEvents.ToolExecutionEnd("tcX")],
        Malformed.SecondToolEnd =>
        [
            TestEvents.ToolExecutionStart("tc1"),
            TestEvents.ToolExecutionEnd("tc1"),
            TestEvents.ToolExecutionEnd("tc1"),
        ],
        Malformed.OrphanToolUpdate =>
        [
            TestEvents.ToolExecutionStart("tc1"),
            TestEvents.ToolExecutionEnd("tc1"),
            TestEvents.ToolExecutionUpdate("tc1"),
        ],
        Malformed.ReusedToolId =>
        [
            TestEvents.ToolExecutionStart("tc1"),
            TestEvents.ToolExecutionEnd("tc1"),
            TestEvents.ToolExecutionStart("tc1"),
        ],
        Malformed.MessageEnd => [TestEvents.MessageEnd()],
        Malformed.TurnEnd => [TestEvents.TurnEnd()],
        Malformed.AgentEnd => [TestEvents.AgentEnd()],
        Malformed.CompactionEnd => [TestEvents.CompactionFailed("s1")],
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
