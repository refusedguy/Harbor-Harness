using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Application.Agents;

namespace Harbor.Core.Tests;

/// <summary>
///     Issue #2673 item A3: the event stream projects into a run-scoped
///     execution graph. These tests pin the shape (run → root → turn → tool),
///     the terminal state propagation and the fact that finished runs stay
///     auditable instead of being dropped when a new run starts.
/// </summary>
public class ExecutionNodeProjectionTests
{
    private static AgentEvent Start(string session = "s1") =>
        new AgentStartEvent(session, Array.Empty<AgentMessage>());

    private static AgentEvent TurnStart(int index = 1, string session = "s1") =>
        new TurnStartEvent(index, session);

    private static AgentEvent ToolStart(string id = "tc1", string name = "bash") =>
        new ToolExecutionStartEvent(id, name, default);

    private static AgentEvent ToolEnd(string id = "tc1", bool error = false) =>
        new ToolExecutionEndEvent(id, ToolResult.Success("ok"), error);

    private static AgentEvent TurnEnd(string session = "s1") =>
        new TurnEndEvent(AssistantMessage.Empty(session, "test"), Array.Empty<ToolResultMessage>(), session);

    private static AgentEvent End(bool cancelled = false) =>
        new AgentEndEvent(Array.Empty<AgentMessage>(), cancelled);

    private static IReadOnlyList<ExecutionNode> NodesOf(ExecutionNodeProjection projection, int runIndex = 0) =>
        projection.NodesByRun.Values.ElementAt(runIndex).ToArray();

    private static ExecutionNode Node(IReadOnlyList<ExecutionNode> nodes, ExecutionNodeKind kind) =>
        nodes.Single(n => n.Kind == kind);

    [Test]
    public async Task AgentStart_OpensRunWithRootChild()
    {
        var projection = new ExecutionNodeProjection();

        projection.Apply(Start("session-A"));

        await Assert.That(projection.NodesByRun.Count).IsEqualTo(1);
        await Assert.That(projection.ActiveRunId).IsNotNull();
        var nodes = NodesOf(projection);
        await Assert.That(nodes.Count).IsEqualTo(2);
        ExecutionNode run = Node(nodes, ExecutionNodeKind.Run);
        ExecutionNode root = Node(nodes, ExecutionNodeKind.Root);
        await Assert.That(root.ParentId!.Value).IsEqualTo(run.Id.Value);
        await Assert.That(run.ParentId).IsNull();
        await Assert.That(run.SessionId).IsEqualTo("session-A");
        await Assert.That(run.State).IsEqualTo(ExecutionNodeState.Active);
        await Assert.That(root.State).IsEqualTo(ExecutionNodeState.Active);
    }

    [Test]
    public async Task TurnAndTool_NestUnderTheirParents()
    {
        var projection = new ExecutionNodeProjection();
        projection.Apply(Start());
        projection.Apply(TurnStart(1));
        projection.Apply(ToolStart("tc1", "read"));

        var nodes = NodesOf(projection);
        await Assert.That(nodes.Count).IsEqualTo(4);
        ExecutionNode root = Node(nodes, ExecutionNodeKind.Root);
        ExecutionNode turn = Node(nodes, ExecutionNodeKind.Turn);
        ExecutionNode tool = Node(nodes, ExecutionNodeKind.Tool);
        await Assert.That(turn.ParentId!.Value).IsEqualTo(root.Id.Value);
        await Assert.That(tool.ParentId!.Value).IsEqualTo(turn.Id.Value);
        await Assert.That(tool.Label).IsEqualTo("read");
        await Assert.That(turn.Label).IsEqualTo("turn 1");
    }

    [Test]
    public async Task ToolEnd_ClosesOnlyTheMatchingNode()
    {
        var projection = new ExecutionNodeProjection();
        projection.Apply(Start());
        projection.Apply(TurnStart());
        projection.Apply(ToolStart("tc1", "bash"));
        projection.Apply(ToolStart("tc2", "grep"));

        projection.Apply(ToolEnd("tc1", error: true));

        var tools = NodesOf(projection).Where(n => n.Kind == ExecutionNodeKind.Tool).ToArray();
        await Assert.That(tools.Count).IsEqualTo(2);
        ExecutionNode failed = tools.Single(n => n.Label == "bash");
        ExecutionNode stillRunning = tools.Single(n => n.Label == "grep");
        await Assert.That(failed.State).IsEqualTo(ExecutionNodeState.Failed);
        await Assert.That(failed.EndedAt).IsNotNull();
        await Assert.That(stillRunning.State).IsEqualTo(ExecutionNodeState.Active);
        await Assert.That(stillRunning.EndedAt).IsNull();
    }

    [Test]
    public async Task AgentEnd_ClosesEveryOpenNodeInTheRun()
    {
        var projection = new ExecutionNodeProjection();
        projection.Apply(Start());
        projection.Apply(TurnStart());
        projection.Apply(ToolStart());

        projection.Apply(End());

        await Assert.That(projection.ActiveRunId).IsNull();
        var nodes = NodesOf(projection);
        await Assert.That(nodes.Count).IsEqualTo(4);
        await Assert.That(nodes.All(n => n.State == ExecutionNodeState.Completed)).IsTrue();
        await Assert.That(nodes.All(n => n.EndedAt is not null)).IsTrue();
    }

    [Test]
    public async Task CancelledAgentEnd_MarksTheRunCancelled()
    {
        var projection = new ExecutionNodeProjection();
        projection.Apply(Start());
        projection.Apply(TurnStart());

        projection.Apply(End(cancelled: true));

        var nodes = NodesOf(projection);
        await Assert.That(Node(nodes, ExecutionNodeKind.Run).State).IsEqualTo(ExecutionNodeState.Cancelled);
        await Assert.That(Node(nodes, ExecutionNodeKind.Root).State).IsEqualTo(ExecutionNodeState.Cancelled);
        await Assert.That(Node(nodes, ExecutionNodeKind.Turn).State).IsEqualTo(ExecutionNodeState.Cancelled);
    }

    [Test]
    public async Task AgentError_MarksTheRunFailed()
    {
        var projection = new ExecutionNodeProjection();
        projection.Apply(Start());
        projection.Apply(TurnStart());

        projection.Apply(new AgentErrorEvent("provider exploded"));

        var nodes = NodesOf(projection);
        await Assert.That(Node(nodes, ExecutionNodeKind.Run).State).IsEqualTo(ExecutionNodeState.Failed);
        await Assert.That(Node(nodes, ExecutionNodeKind.Turn).State).IsEqualTo(ExecutionNodeState.Failed);
        await Assert.That(projection.ActiveRunId).IsNull();
    }

    [Test]
    public async Task SecondStart_PreservesFirstRunAndClosesItAsCancelled()
    {
        var projection = new ExecutionNodeProjection();
        projection.Apply(Start("s1"));
        projection.Apply(TurnStart());

        projection.Apply(Start("s2"));

        await Assert.That(projection.NodesByRun.Count).IsEqualTo(2);
        var first = NodesOf(projection, 0);
        var second = NodesOf(projection, 1);
        await Assert.That(first.All(n => n.State == ExecutionNodeState.Cancelled)).IsTrue();
        await Assert.That(second.All(n => n.State == ExecutionNodeState.Active)).IsTrue();
        await Assert.That(Node(second, ExecutionNodeKind.Run).SessionId).IsEqualTo("s2");
    }

    [Test]
    public async Task UnmatchedEndEvents_DoNotCreateOrLeakNodes()
    {
        var projection = new ExecutionNodeProjection();

        projection.Apply(ToolEnd("never-started"));
        projection.Apply(TurnEnd());
        projection.Apply(End());

        await Assert.That(projection.NodesByRun.Count).IsEqualTo(0);
        await Assert.That(projection.ActiveRunId).IsNull();
    }

    [Test]
    public async Task NodesByRun_ReturnsSnapshotThatDoesNotTrackLaterEvents()
    {
        var projection = new ExecutionNodeProjection();
        projection.Apply(Start());
        var snapshot = NodesOf(projection);

        projection.Apply(TurnStart());
        projection.Apply(ToolStart());

        await Assert.That(snapshot.Count).IsEqualTo(2);
        await Assert.That(NodesOf(projection).Count).IsEqualTo(4);
    }

    [Test]
    public async Task ExecutionNodeId_CreateRejectsBlankValues()
    {
        Assert.Throws<ArgumentException>(() => ExecutionNodeId.Create(string.Empty));
        Assert.Throws<ArgumentException>(() => ExecutionNodeId.Create("  "));

        await Assert.That(ExecutionNodeId.Create("n1").Value).IsEqualTo("n1");
    }
}
