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

    [Test]
    public async Task ToolEnd_WithoutStart_IsReported()
    {
        var events = new AgentEvent[] { TestEvents.ToolExecutionEnd("tcX") };

        IReadOnlyList<string> violations = EventTranscriptAssert.FindViolations(events);

        await Assert.That(violations.Count).IsEqualTo(1);
        await Assert.That(violations[0]).Contains("ToolExecutionEnd for 'tcX' without an open start");

        EventTranscriptException thrown = Assert.Throws<EventTranscriptException>(
            () => EventTranscriptAssert.AssertWellFormed(events));
        await Assert.That(thrown.Violations.Count).IsEqualTo(1);
        await Assert.That(thrown.Message).Contains("ToolExecutionEnd for 'tcX'");
    }

    [Test]
    public async Task SecondToolEnd_IsReported()
    {
        AgentEvent[] events =
        [
            TestEvents.ToolExecutionStart("tc1"),
            TestEvents.ToolExecutionEnd("tc1"),
            TestEvents.ToolExecutionEnd("tc1"),
        ];

        IReadOnlyList<string> violations = EventTranscriptAssert.FindViolations(events);

        await Assert.That(violations.Count).IsEqualTo(1);
        await Assert.That(violations[0]).Contains("[2] ToolExecutionEnd for 'tc1' without an open start");
    }

    [Test]
    public async Task ToolUpdate_WithoutOpenStart_IsReported()
    {
        AgentEvent[] events =
        [
            TestEvents.ToolExecutionStart("tc1"),
            TestEvents.ToolExecutionEnd("tc1"),
            TestEvents.ToolExecutionUpdate("tc1"),
        ];

        IReadOnlyList<string> violations = EventTranscriptAssert.FindViolations(events);

        await Assert.That(violations.Count).IsEqualTo(1);
        await Assert.That(violations[0]).Contains("ToolExecutionUpdate for 'tc1' with no open start");
    }

    [Test]
    public async Task ReusedToolCallId_IsReported()
    {
        AgentEvent[] events =
        [
            TestEvents.ToolExecutionStart("tc1"),
            TestEvents.ToolExecutionEnd("tc1"),
            TestEvents.ToolExecutionStart("tc1"),
        ];

        IReadOnlyList<string> violations = EventTranscriptAssert.FindViolations(events);

        await Assert.That(violations.Count).IsEqualTo(1);
        await Assert.That(violations[0]).Contains("reuses an earlier call id");
    }

    [Test]
    public async Task MessageEnd_WithoutStart_IsReported()
    {
        var events = new AgentEvent[] { TestEvents.MessageEnd() };

        IReadOnlyList<string> violations = EventTranscriptAssert.FindViolations(events);

        await Assert.That(violations.Count).IsEqualTo(1);
        await Assert.That(violations[0]).Contains("MessageEnd without a preceding MessageStart");
    }

    [Test]
    public async Task TurnEnd_WithoutStart_IsReported()
    {
        var events = new AgentEvent[] { TestEvents.TurnEnd() };

        IReadOnlyList<string> violations = EventTranscriptAssert.FindViolations(events);

        await Assert.That(violations.Count).IsEqualTo(1);
        await Assert.That(violations[0]).Contains("TurnEnd without a preceding TurnStart");
    }

    [Test]
    public async Task AgentEnd_WithoutStart_IsReported()
    {
        var events = new AgentEvent[] { TestEvents.AgentEnd() };

        IReadOnlyList<string> violations = EventTranscriptAssert.FindViolations(events);

        await Assert.That(violations.Count).IsEqualTo(1);
        await Assert.That(violations[0]).Contains("AgentEnd without a preceding AgentStart");
    }

    [Test]
    public async Task CompactionEnd_WithoutStart_IsReported()
    {
        var events = new AgentEvent[] { TestEvents.CompactionFailed("s1") };

        IReadOnlyList<string> violations = EventTranscriptAssert.FindViolations(events);

        await Assert.That(violations.Count).IsEqualTo(1);
        await Assert.That(violations[0]).Contains("CompactionFailed for 's1' without an open CompactionStarted");
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
}
