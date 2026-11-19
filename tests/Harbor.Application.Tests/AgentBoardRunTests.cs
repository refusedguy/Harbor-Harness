// #3384: behavior proof that the previously dead AgentBoard now has a real
// product caller. The agent loop publishes Info/Result messages through the
// IAgentBoard port; this test drives the real loop with the real board and
// reads the messages back through the board's API (which exercises the
// Message / Kind / State companions, not a test double).
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using Harbor.Tools.Builtin;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TestSessionContext = Harbor.TestKit.TestSessionContext;
using TestSessionContext = Harbor.TestKit.TestSessionContext;

namespace Harbor.Application.Tests;

public class AgentBoardRunTests
{
    [Test]
    public async Task RunAsync_PublishesAgentMessagesToListenersOfTheBoard()
    {
        var client = new ScriptedLlmClient(
        [
            [TestEvents.TextDelta(delta: "done"), TestEvents.StepFinish(usage: new Usage(1, 1))]
        ]);
        var board = new AgentBoard();
        var loop = TestLoops.CreateWithBoard(client, board);
        var session = new TestSessionContext(
            Session.Create("/tmp/harbor-agentboard-tests", "code", "test", "test-model"));

        Result result = await loop.RunAsync(session, TestAgents.AllowAll());

        await Assert.That(result.IsSuccess).IsTrue();
        string sessionId = session.Session.Id;

        IReadOnlyList<AgentBoardMessage> pending = board.PendingFor(sessionId);
        await Assert.That(pending.Count).IsGreaterThanOrEqualTo(2);

        AgentBoardMessage started = pending[0];
        await Assert.That(started.Kind).IsEqualTo(AgentBoardMessageKind.Info);
        await Assert.That(started.State).IsEqualTo(AgentBoardMessageState.Pending);
        await Assert.That(started.SourceSession).IsEqualTo(sessionId);
        await Assert.That(started.IsBroadcast).IsTrue();
        await Assert.That(started.Body).Contains("started");

        AgentBoardMessage finished = pending[1];
        await Assert.That(finished.Kind).IsEqualTo(AgentBoardMessageKind.Result);
        await Assert.That(finished.State).IsEqualTo(AgentBoardMessageState.Pending);
        await Assert.That(finished.Body).Contains("finished");

        // History is newest-first and includes both terminal and pending entries.
        IReadOnlyList<AgentBoardMessage> history = board.HistoryFor(sessionId, AgentBoard.MaxMessages);
        await Assert.That(history.Count).IsEqualTo(2);
        await Assert.That(history[0].Id).IsGreaterThan(history[1].Id);
    }
}
