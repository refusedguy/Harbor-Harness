// #2680 F105. Behaviour tests for the shared agent board leaf. These pin the
// observable message kinds, the hold/veto/delivery state machine, recipient
// filtering and the bounded history without a session store, event bus or TUI.
using CSharpFunctionalExtensions;

namespace Harbor.Tools.Builtin.Tests;

public class AgentBoardTests
{
    private static Result<AgentBoardMessage> Post(
        AgentBoard board,
        AgentBoardMessageKind kind = AgentBoardMessageKind.Info,
        string source = "main",
        string? target = "worker-1",
        string body = "hello") =>
        board.Post(kind, source, target, body);

    [Test]
    public async Task Post_AssignsMonotonicIdsAndTrimsFields()
    {
        var board = new AgentBoard();

        Result<AgentBoardMessage> first = Post(board, target: "  worker-1  ", source: "  main ", body: "  hi  ");
        Result<AgentBoardMessage> second = Post(board, kind: AgentBoardMessageKind.Ask);

        await Assert.That(first.IsSuccess).IsTrue();
        await Assert.That(first.Value.Id).IsEqualTo(1);
        await Assert.That(first.Value.SourceSession).IsEqualTo("main");
        await Assert.That(first.Value.TargetSession).IsEqualTo("worker-1");
        await Assert.That(first.Value.Body).IsEqualTo("hi");
        await Assert.That(first.Value.State).IsEqualTo(AgentBoardMessageState.Pending);
        await Assert.That(first.Value.IsDeliverable).IsTrue();
        await Assert.That(first.Value.IsBroadcast).IsFalse();

        await Assert.That(second.IsSuccess).IsTrue();
        await Assert.That(second.Value.Id).IsEqualTo(2);
        await Assert.That(board.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Post_RejectsBlankSource(string? source)
    {
        Result<AgentBoardMessage> result = Post(new AgentBoard(), source: source!);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("Source session");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("\t")]
    public async Task Post_RejectsBlankBody(string? body)
    {
        Result<AgentBoardMessage> result = Post(new AgentBoard(), body: body!);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("body");
    }

    [Test]
    public async Task Post_RejectsUnknownKind()
    {
        Result<AgentBoardMessage> result = Post(new AgentBoard(), kind: (AgentBoardMessageKind)99);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("kind");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Post_MapsBlankTargetToBroadcast(string? target)
    {
        Result<AgentBoardMessage> result = Post(new AgentBoard(), target: target);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.TargetSession).IsNull();
        await Assert.That(result.Value.IsBroadcast).IsTrue();
    }

    [Test]
    public async Task PendingFor_FiltersByTargetAndIncludesBroadcasts()
    {
        var board = new AgentBoard();
        AgentBoardMessage direct = Post(board, target: "worker-1").Value;
        AgentBoardMessage other = Post(board, target: "worker-2", body: "not yours").Value;
        AgentBoardMessage broadcast = Post(board, target: null, body: "everyone").Value;

        IReadOnlyList<AgentBoardMessage> pending = board.PendingFor("worker-1");

        await Assert.That(pending.Count).IsEqualTo(2);
        await Assert.That(pending[0].Id).IsEqualTo(direct.Id);
        await Assert.That(pending[1].Id).IsEqualTo(broadcast.Id);
        await Assert.That(pending.Any(m => m.Id == other.Id)).IsFalse();
        await Assert.That(board.PendingFor("unknown").Count).IsEqualTo(1);
        await Assert.That(board.PendingFor("  ").Count).IsEqualTo(0);
    }

    [Test]
    public async Task Hold_WithholdsAndReleaseRestores()
    {
        var board = new AgentBoard();
        long id = Post(board).Value.Id;

        Result<AgentBoardMessage> held = board.Hold(id);

        await Assert.That(held.IsSuccess).IsTrue();
        await Assert.That(held.Value.State).IsEqualTo(AgentBoardMessageState.Held);
        await Assert.That(board.PendingFor("worker-1").Count).IsEqualTo(0);
        await Assert.That(board.Get(id)!.State).IsEqualTo(AgentBoardMessageState.Held);

        Result<AgentBoardMessage> released = board.Release(id);

        await Assert.That(released.IsSuccess).IsTrue();
        await Assert.That(released.Value.State).IsEqualTo(AgentBoardMessageState.Pending);
        await Assert.That(board.PendingFor("worker-1").Count).IsEqualTo(1);
    }

    [Test]
    public async Task Veto_IsTerminalFromPendingAndHeld()
    {
        var board = new AgentBoard();
        long pendingId = Post(board).Value.Id;
        long heldId = Post(board, body: "held").Value.Id;
        board.Hold(heldId);

        Result<AgentBoardMessage> vetoedPending = board.Veto(pendingId);
        Result<AgentBoardMessage> vetoedHeld = board.Veto(heldId);

        await Assert.That(vetoedPending.IsSuccess).IsTrue();
        await Assert.That(vetoedPending.Value.State).IsEqualTo(AgentBoardMessageState.Vetoed);
        await Assert.That(vetoedHeld.IsSuccess).IsTrue();
        await Assert.That(vetoedHeld.Value.State).IsEqualTo(AgentBoardMessageState.Vetoed);
        await Assert.That(board.PendingFor("worker-1").Count).IsEqualTo(0);
        await Assert.That(board.Veto(pendingId).IsFailure).IsTrue();
        await Assert.That(board.Release(pendingId).IsFailure).IsTrue();
        await Assert.That(board.Deliver(pendingId).IsFailure).IsTrue();
    }

    [Test]
    public async Task Deliver_ConsumesOnlyPendingMessages()
    {
        var board = new AgentBoard();
        long id = Post(board).Value.Id;
        long heldId = Post(board, body: "hold me").Value.Id;
        board.Hold(heldId);

        Result<AgentBoardMessage> delivered = board.Deliver(id);

        await Assert.That(delivered.IsSuccess).IsTrue();
        await Assert.That(delivered.Value.State).IsEqualTo(AgentBoardMessageState.Delivered);
        await Assert.That(board.PendingFor("worker-1").Count).IsEqualTo(0);
        await Assert.That(board.Deliver(id).IsFailure).IsTrue();
        await Assert.That(board.Deliver(heldId).IsFailure).IsTrue();
    }

    [Test]
    public async Task Transitions_RejectUnknownIds()
    {
        var board = new AgentBoard();

        await Assert.That(board.Hold(42).IsFailure).IsTrue();
        await Assert.That(board.Release(42).IsFailure).IsTrue();
        await Assert.That(board.Veto(42).IsFailure).IsTrue();
        await Assert.That(board.Deliver(42).IsFailure).IsTrue();
        await Assert.That(board.Get(42)).IsNull();
    }

    [Test]
    public async Task HistoryFor_ReturnsNewestFirstAndClampsLimit()
    {
        var board = new AgentBoard();
        for (int i = 0; i < 5; i++) Post(board, target: "worker-1", body: $"m{i}");
        Post(board, target: "worker-2", body: "other");

        IReadOnlyList<AgentBoardMessage> history = board.HistoryFor("worker-1", 3);

        await Assert.That(history.Count).IsEqualTo(3);
        await Assert.That(history[0].Body).IsEqualTo("m4");
        await Assert.That(history[2].Body).IsEqualTo("m2");
        await Assert.That(board.HistoryFor("worker-1", 0).Count).IsEqualTo(1);
        await Assert.That(board.HistoryFor("worker-1", 999).Count).IsEqualTo(5);
        await Assert.That(board.HistoryFor(" ", 10).Count).IsEqualTo(0);
    }

    [Test]
    public async Task BoundedCapacity_EvictsOldestWhenNothingIsTerminal()
    {
        var board = new AgentBoard();
        for (int i = 0; i < AgentBoard.MaxMessages; i++) Post(board, body: $"m{i}");

        await Assert.That(board.Count).IsEqualTo(AgentBoard.MaxMessages);
        await Assert.That(board.Get(1)).IsNotNull();

        Result<AgentBoardMessage> overflow = Post(board, body: "overflow");

        await Assert.That(overflow.IsSuccess).IsTrue();
        await Assert.That(board.Count).IsEqualTo(AgentBoard.MaxMessages);
        await Assert.That(board.Get(1)).IsNull();
        await Assert.That(board.Get(2)).IsNotNull();
        await Assert.That(board.Get(overflow.Value.Id)).IsNotNull();
    }

    [Test]
    public async Task BoundedCapacity_PrefersEvictingTerminalMessages()
    {
        var board = new AgentBoard();
        for (int i = 0; i < AgentBoard.MaxMessages; i++) Post(board, body: $"m{i}");
        board.Veto(1);

        Result<AgentBoardMessage> overflow = Post(board, body: "overflow");

        await Assert.That(overflow.IsSuccess).IsTrue();
        await Assert.That(board.Count).IsEqualTo(AgentBoard.MaxMessages);
        await Assert.That(board.Get(1)).IsNull();
        await Assert.That(board.Get(2)).IsNotNull();
    }
}
