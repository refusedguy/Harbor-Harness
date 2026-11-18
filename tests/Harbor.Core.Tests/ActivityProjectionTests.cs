using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Application.Agents;

namespace Harbor.Core.Tests;

/// <summary>
///     Issue #2673 item A9: the execution graph flattens into a run-scoped
///     activity feed. These tests pin the mapping (RunId/NodeId/ParentId), the
///     tool-call hiding flag, run grouping and the deterministic line renderer.
/// </summary>
public class ActivityProjectionTests
{
    private static AgentEvent Start(string session = "s1") =>
        new AgentStartEvent(session, Array.Empty<AgentMessage>());

    private static AgentEvent TurnStart(int index = 1, string session = "s1") =>
        new TurnStartEvent(index, session);

    private static AgentEvent ToolStart(string id = "tc1", string name = "bash") =>
        new ToolExecutionStartEvent(id, name, default);

    private static AgentEvent ToolEnd(string id = "tc1", bool error = false) =>
        new ToolExecutionEndEvent(id, ToolResult.Success("ok"), error);

    private static AgentEvent End() =>
        new AgentEndEvent(Array.Empty<AgentMessage>(), false);

    [Test]
    public async Task Events_ProduceEntriesCarryingRunAndNodeIds()
    {
        var projection = new ActivityProjection();

        projection.Apply(Start("session-A"));
        projection.Apply(TurnStart(1, "session-A"));
        projection.Apply(ToolStart("tc1", "read"));

        IReadOnlyList<ActivityEntry> entries = projection.Entries;
        await Assert.That(entries.Count).IsEqualTo(4);
        await Assert.That(entries.All(e => e.RunId == projection.ActiveRunId)).IsTrue();
        await Assert.That(entries.All(e => e.SessionId == "session-A")).IsTrue();
        await Assert.That(entries.All(e => e.NodeId is not null)).IsTrue();

        ActivityEntry tool = entries.Single(e => e.Kind == ExecutionNodeKind.Tool);
        ActivityEntry turn = entries.Single(e => e.Kind == ExecutionNodeKind.Turn);
        ActivityEntry root = entries.Single(e => e.Kind == ExecutionNodeKind.Root);
        await Assert.That(tool.Label).IsEqualTo("read");
        await Assert.That(tool.ParentId!.Value).IsEqualTo(turn.NodeId.Value);
        await Assert.That(turn.ParentId!.Value).IsEqualTo(root.NodeId.Value);
        await Assert.That(tool.RunId).IsEqualTo(turn.RunId);
    }

    [Test]
    public async Task Entries_AreOrderedByStartTime()
    {
        var projection = new ActivityProjection();
        projection.Apply(Start());
        projection.Apply(TurnStart(1));
        projection.Apply(ToolStart());
        projection.Apply(ToolEnd());
        projection.Apply(End());

        IReadOnlyList<ActivityEntry> entries = projection.Entries;
        await Assert.That(entries.Count).IsEqualTo(4);
        for (int i = 1; i < entries.Count; i++)
        {
            await Assert.That(entries[i].StartedAt >= entries[i - 1].StartedAt).IsTrue();
        }
    }

    [Test]
    public async Task EntriesByRun_GroupsEachRunSeparately()
    {
        var projection = new ActivityProjection();
        projection.Apply(Start("s1"));
        projection.Apply(TurnStart());
        projection.Apply(Start("s2"));
        projection.Apply(TurnStart(2, "s2"));

        await Assert.That(projection.EntriesByRun.Count).IsEqualTo(2);
        foreach (KeyValuePair<RunId, IReadOnlyList<ActivityEntry>> run in projection.EntriesByRun)
        {
            await Assert.That(run.Value.Count).IsEqualTo(3);
            await Assert.That(run.Value.All(e => e.RunId == run.Key)).IsTrue();
        }
    }

    [Test]
    public async Task HideToolCalls_FiltersOnlyToolLinesAndIsReversible()
    {
        var projection = new ActivityProjection();
        projection.Apply(Start());
        projection.Apply(TurnStart());
        projection.Apply(ToolStart("tc1", "bash"));
        projection.Apply(ToolStart("tc2", "grep"));

        await Assert.That(projection.Entries.Count).IsEqualTo(5);

        projection.HideToolCalls = true;
        IReadOnlyList<ActivityEntry> hidden = projection.Entries;
        await Assert.That(hidden.Count).IsEqualTo(3);
        await Assert.That(hidden.Any(e => e.IsToolCall)).IsFalse();

        projection.HideToolCalls = false;
        await Assert.That(projection.Entries.Count).IsEqualTo(5);
    }

    [Test]
    public async Task EndEvents_CloseNodesAndPopulateDuration()
    {
        var projection = new ActivityProjection();
        projection.Apply(Start());
        projection.Apply(TurnStart());
        projection.Apply(ToolStart());
        projection.Apply(ToolEnd(error: true));
        projection.Apply(End());

        ActivityEntry tool = projection.Entries.Single(e => e.Kind == ExecutionNodeKind.Tool);
        await Assert.That(tool.State).IsEqualTo(ExecutionNodeState.Failed);
        await Assert.That(tool.EndedAt).IsNotNull();
        await Assert.That(tool.Duration).IsNotNull();
        await Assert.That(projection.ActiveRunId).IsNull();
    }

    [Test]
    public async Task Describe_RendersLabelStateAndInvariantDuration()
    {
        var start = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        var entry = new ActivityEntry(
            RunId.New(),
            ExecutionNodeKind.Tool,
            ExecutionNodeId.New(),
            null,
            "s1",
            "read",
            start,
            start.AddMilliseconds(1500),
            ExecutionNodeState.Completed);

        await Assert.That(entry.Describe()).IsEqualTo("read [done] (1.50s)");
    }

    [Test]
    public async Task Describe_UsesKindWhenLabelMissing()
    {
        var entry = new ActivityEntry(
            RunId.New(),
            ExecutionNodeKind.Root,
            ExecutionNodeId.New(),
            null,
            "s1",
            null,
            DateTimeOffset.UnixEpoch,
            null,
            ExecutionNodeState.Active);

        await Assert.That(entry.Describe()).IsEqualTo("Root [running]");
    }

    [Test]
    public async Task Apply_NullEventThrows()
    {
        var projection = new ActivityProjection();
        Assert.Throws<ArgumentNullException>(() => projection.Apply(null!));

        await Assert.That(projection.Entries.Count).IsEqualTo(0);
    }
}
