using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using TestSessionContext = Harbor.Application.Tests.Fakes.TestSessionContext;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     Issue #1101 (god <c>AgentLoop</c>, slice 1): message snapshotting for events
///     moved verbatim to <c>MessageSnapshot</c>, keeping a forwarder in the loop.
///     These tests pin the observable contract of the extract through the loop:
///     start/end events carry a frozen copy of the history, never the live list.
/// </summary>
public class MessageSnapshotTests
{
    [Test]
    public async Task RunAsync_AgentStartEvent_CarriesPreRunSnapshotNotLiveList()
    {
        var client = new ScriptedLlmClient(
            [[new TextDeltaEvent("t", "hi"), new StepFinishEvent(0, "stop", new Usage(1, 1))]]);
        var bus = new FakeEventBus();
        var loop = TestLoops.Create(client, new FakeToolRegistry(), new FakeTokenTracker(), new FakeCompactionService(), bus);
        var session = Session.Create("/tmp/harbor-message-snapshot-tests", "code", "test", "test-model");
        var context = new TestSessionContext(session, [AgentFixtures.UserMsg(session.Id, 0), AgentFixtures.UserMsg(session.Id, 1)]);

        var result = await loop.RunAsync(context, TestAgents.AllowAll());

        await Assert.That(result.IsSuccess).IsTrue();
        AgentStartEvent? start = null;
        foreach (var evt in bus.Events)
        {
            if (evt is AgentStartEvent s)
            {
                start = s;
            }
        }

        await Assert.That(start is not null).IsTrue();
        // Frozen at run start: the turn below appends, the event must not grow.
        await Assert.That(start!.Messages.Count).IsEqualTo(2);
        await Assert.That(ReferenceEquals(start.Messages, context.Messages)).IsFalse();
        await Assert.That(context.Messages.Count).IsNotEqualTo(2);
    }

    [Test]
    public async Task RunAsync_AgentEndEvent_CarriesFullHistorySnapshotNotLiveList()
    {
        var client = new ScriptedLlmClient(
            [[new TextDeltaEvent("t", "hi"), new StepFinishEvent(0, "stop", new Usage(1, 1))]]);
        var bus = new FakeEventBus();
        var loop = TestLoops.Create(client, new FakeToolRegistry(), new FakeTokenTracker(), new FakeCompactionService(), bus);
        var session = Session.Create("/tmp/harbor-message-snapshot-tests", "code", "test", "test-model");
        var context = new TestSessionContext(session, [AgentFixtures.UserMsg(session.Id, 0), AgentFixtures.UserMsg(session.Id, 1)]);

        var result = await loop.RunAsync(context, TestAgents.AllowAll());

        await Assert.That(result.IsSuccess).IsTrue();
        AgentEndEvent? end = null;
        foreach (var evt in bus.Events)
        {
            if (evt is AgentEndEvent e)
            {
                end = e;
            }
        }

        await Assert.That(end is not null).IsTrue();
        await Assert.That(end!.NewMessages.Count).IsEqualTo(context.Messages.Count);
        await Assert.That(ReferenceEquals(end.NewMessages, context.Messages)).IsFalse();
    }
}
