using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Terminal.Abstractions.Views;
using Harbor.Terminal.Abstractions.Views.Grouping;

namespace Harbor.Tui.Tests;

/// <summary>
///     Tests for [steal/opencode] #1171 (epic #1155): transcript grouping by
///     verbosity over the generic <see cref="GroupTree"/> engine.
/// </summary>
public class GroupTreeTests
{
    private static IReadOnlyList<string> Path((string Id, string[] Path) entry) => entry.Path;

    [Test]
    public async Task GroupEntries_GroupsAdjacentByPath_CountsLeaves()
    {
        var read = ("read", new[] { "activity", "exploration" });
        var search = ("search", new[] { "activity", "exploration" });
        var thought = ("thought", new[] { "activity", "reasoning" });
        var text = ("text", Array.Empty<string>());

        var nodes = GroupTree.GroupEntries(
            new[] { read, search, thought, text, read }, Path);

        await Assert.That(nodes.Count).IsEqualTo(3);
        var activity = (GroupNode<(string, string[]), string>.Group)nodes[0];
        await Assert.That(activity.Kind).IsEqualTo("activity");
        await Assert.That(activity.Size).IsEqualTo(3);
        await Assert.That(nodes[1]).IsTypeOf<GroupNode<(string, string[]), string>.Entry>();
        var tail = (GroupNode<(string, string[]), string>.Group)nodes[2];
        await Assert.That(tail.Size).IsEqualTo(1);
    }

    [Test]
    public async Task GroupEntries_DirectChildBreaksSubgroup_KeepsOuter()
    {
        var read = ("read", new[] { "activity", "exploration" });
        var shell = ("shell", new[] { "activity" });
        var search = ("search", new[] { "activity", "exploration" });

        var nodes = GroupTree.GroupEntries(new[] { read, shell, search }, Path);

        await Assert.That(nodes.Count).IsEqualTo(1);
        var activity = (GroupNode<(string, string[]), string>.Group)nodes[0];
        await Assert.That(activity.Size).IsEqualTo(3);
        await Assert.That(activity.Children.Count).IsEqualTo(3);
    }

    [Test]
    public async Task MergeGroups_AtPageSeam_KeepsUntouchedIdentity()
    {
        var read = ("read", new[] { "activity", "exploration" });
        var search = ("search", new[] { "activity", "exploration" });
        var thought = ("thought", new[] { "activity", "reasoning" });
        var text = ("text", Array.Empty<string>());

        var left = GroupTree.GroupEntries(new[] { text, read }, Path);
        var right = GroupTree.GroupEntries(new[] { search, thought }, Path);
        var merged = GroupTree.MergeGroups(left, right);
        var whole = GroupTree.GroupEntries(new[] { text, read, search, thought }, Path);

        await Assert.That(merged.Count).IsEqualTo(whole.Count);
        await Assert.That(ReferenceEquals(merged[0], left[0])).IsTrue();
    }

    [Test]
    public async Task SplitGroups_AtEveryBoundary_MergesBack()
    {
        var read = ("read", new[] { "activity", "exploration" });
        var search = ("search", new[] { "activity", "exploration" });
        var thought = ("thought", new[] { "activity", "reasoning" });
        var text = ("text", Array.Empty<string>());
        var entries = new[] { read, search, thought, text };
        var tree = GroupTree.GroupEntries(entries, Path);

        for (int count = 0; count <= entries.Length; count++)
        {
            var (l, right) = GroupTree.SplitGroups(tree, count);
            await Assert.That(LeafCount(l)).IsEqualTo(count);
            await Assert.That(LeafCount(right)).IsEqualTo(entries.Length - count);
            var back = GroupTree.MergeGroups(l, right);
            await Assert.That(back.Count).IsEqualTo(tree.Count);
        }
    }

    private static int LeafCount(
        IReadOnlyList<GroupNode<(string, string[]), string>> nodes)
    {
        int total = 0;
        foreach (var node in nodes)
        {
            total += node.Size;
        }

        return total;
    }

    [Test]
    public async Task SplitGroups_InvalidOffsets_Throw()
    {
        var read = ("read", new[] { "activity", "exploration" });
        var tree = GroupTree.GroupEntries(new[] { read }, Path);

        await Assert.That(() => { GroupTree.SplitGroups(tree, -1); }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => { GroupTree.SplitGroups(tree, 2); }).Throws<ArgumentOutOfRangeException>();
    }
}

public class TranscriptPathTests
{
    private static ChatEntry Tool(string id, string name) =>
        new("tool", $"→ {name}", DateTimeOffset.UtcNow, id, name);

    private static ChatEntry Result(string id, string name) =>
        new("tool-result", "✓ out", DateTimeOffset.UtcNow, id, name);

    [Test]
    public async Task AdjacentThinking_GroupsReasoning_TextSplits()
    {
        var t = DateTimeOffset.UtcNow;
        var rows = TranscriptGrouping.ProjectEntries(new[]
        {
            new ChatEntry("thinking", "hmm", t),
            new ChatEntry("thinking", "ah", t),
            new ChatEntry("assistant", "done", t),
            new ChatEntry("thinking", "more", t),
        });

        await Assert.That(rows.Count).IsEqualTo(3);
        await Assert.That(rows[0]).IsTypeOf<TranscriptRow.Group>();
        await Assert.That(((TranscriptRow.Group)rows[0]).Kind).IsEqualTo(TranscriptGroupKind.Reasoning);
        await Assert.That(((TranscriptRow.Group)rows[0]).Size).IsEqualTo(2);
        await Assert.That(rows[1]).IsTypeOf<TranscriptRow.Single>();
    }

    [Test]
    public async Task ExplorationTools_Group_ExecStaysSolo()
    {
        var rows = TranscriptGrouping.ProjectEntries(new[]
        {
            Tool("a", "read"),
            Result("a", "read"),
            Tool("b", "glob"),
            Tool("c", "bash"),
        });

        await Assert.That(rows.Count).IsEqualTo(2);
        var group = (TranscriptRow.Group)rows[0];
        await Assert.That(group.Kind).IsEqualTo(TranscriptGroupKind.Exploration);
        await Assert.That(group.Size).IsEqualTo(3);
        await Assert.That(rows[1]).IsTypeOf<TranscriptRow.Single>();
    }

    [Test]
    public async Task QuestionTool_AlwaysStandsAlone()
    {
        var rows = TranscriptGrouping.ProjectEntries(new[]
        {
            Tool("a", "question"),
            Tool("b", "question"),
        });

        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows[0]).IsTypeOf<TranscriptRow.Single>();
        await Assert.That(rows[1]).IsTypeOf<TranscriptRow.Single>();
    }

    [Test]
    public async Task LowVerbosity_WrapsRunInActivity()
    {
        var t = DateTimeOffset.UtcNow;
        var rows = TranscriptGrouping.ProjectEntries(new[]
        {
            Tool("a", "read"),
            new ChatEntry("thinking", "hmm", t),
            Tool("b", "bash"),
        }, TranscriptVerbosity.Low);

        await Assert.That(rows.Count).IsEqualTo(1);
        var activity = (TranscriptRow.Group)rows[0];
        await Assert.That(activity.Kind).IsEqualTo(TranscriptGroupKind.Activity);
        await Assert.That(activity.Size).IsEqualTo(3);
    }

    [Test]
    public async Task SystemEntry_GroupsInstructions()
    {
        var rows = TranscriptGrouping.ProjectEntries(new[]
        {
            new ChatEntry("system", "loaded a.cs", DateTimeOffset.UtcNow),
        });

        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(((TranscriptRow.Group)rows[0]).Kind).IsEqualTo(TranscriptGroupKind.Instructions);
    }

    [Test]
    public async Task Append_MergesIntoPreviousSameKind_CompletesOnSolo()
    {
        var t = DateTimeOffset.UtcNow;
        var rows = TranscriptGrouping.ProjectEntries(new[]
        {
            new ChatEntry("thinking", "one", t),
        });
        var live = (TranscriptRow.Group)rows[0];
        await Assert.That(live.Completed).IsFalse();

        TranscriptGrouping.Append(rows, new ChatEntry("thinking", "two", t));
        live = (TranscriptRow.Group)rows[0];
        await Assert.That(live.Size).IsEqualTo(2);
        await Assert.That(rows.Count).IsEqualTo(1);

        TranscriptGrouping.Append(rows, new ChatEntry("assistant", "done", t));
        live = (TranscriptRow.Group)rows[0];
        await Assert.That(live.Completed).IsTrue();
        await Assert.That(rows.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Append_InsertsBeforePendingTail()
    {
        var rows = new List<TranscriptRow>();
        TranscriptGrouping.Append(rows, Tool("blocked", "read"));
        TranscriptGrouping.PartitionPending(rows, new HashSet<string>(StringComparer.Ordinal) { "blocked" });
        TranscriptGrouping.Append(rows, Tool("fresh", "read"));

        var group = (TranscriptRow.Group)rows[0];
        await Assert.That(TranscriptGrouping.GroupRefs(group).Contains("fresh")).IsTrue();
        await Assert.That(group.PendingToolCallIds.Count).IsEqualTo(1);
        await Assert.That(group.PendingToolCallIds[0]).IsEqualTo("blocked");
        await Assert.That(string.Join(",", TranscriptGrouping.GroupRefs(group, includePending: true)))
            .IsEqualTo("fresh,blocked");
    }

    [Test]
    public async Task PartitionPending_ParksBlockedAtEnd_WithoutReorder()
    {
        var rows = new List<TranscriptRow>();
        TranscriptGrouping.Append(rows, Tool("reused-a", "read"));
        TranscriptGrouping.Append(rows, Tool("later", "read"));
        var pending = new HashSet<string>(StringComparer.Ordinal) { "reused-a" };
        TranscriptGrouping.PartitionPending(rows, pending);

        var group = (TranscriptRow.Group)rows[0];
        await Assert.That(string.Join(",", TranscriptGrouping.GroupRefs(group))).IsEqualTo("later");
        await Assert.That(group.PendingToolCallIds.Count).IsEqualTo(1);
        await Assert.That(group.PendingToolCallIds[0]).IsEqualTo("reused-a");

        TranscriptGrouping.PartitionPending(rows, new HashSet<string>(StringComparer.Ordinal));
        // Dismissing the permission updates the pending list but never
        // re-shuffles the stable partition order.
        group = (TranscriptRow.Group)rows[0];
        await Assert.That(string.Join(",", TranscriptGrouping.GroupRefs(group))).IsEqualTo("later,reused-a");
        await Assert.That(group.PendingToolCallIds.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SummarizeActivity_CountsFinishedWork_SkipsBlocked()
    {
        var t = DateTimeOffset.UtcNow;
        var rows = TranscriptGrouping.ProjectEntries(new[]
        {
            Tool("sh", "bash"),
            Result("sh", "bash"),
            Tool("e", "edit"),
            Tool("r1", "read"),
            Tool("r2", "glob"),
            new ChatEntry("thinking", "hmm", t),
            new ChatEntry("assistant", "ok", t),
        }, TranscriptVerbosity.Low);

        var activity = (TranscriptRow.Group)rows[0];
        var summary = TranscriptGrouping.SummarizeActivity(activity);
        await Assert.That(summary.Label).IsEqualTo("1 command, 1 edit, 1 thought, 2 reads");
        await Assert.That(summary.Active).IsFalse();

        TranscriptGrouping.PartitionPending(rows, new HashSet<string>(StringComparer.Ordinal) { "sh" });
        activity = (TranscriptRow.Group)rows[0];
        summary = TranscriptGrouping.SummarizeActivity(activity);
        await Assert.That(summary.Label).IsEqualTo("1 edit, 1 thought, 2 reads");
        await Assert.That(summary.Active).IsTrue();
    }
}

public class TranscriptMountBudgetTests
{
    private static TranscriptRow.Group ExplorationGroup(params ChatEntry[] entries)
    {
        var nodes = GroupTree.GroupEntries(entries, _ => (IReadOnlyList<TranscriptGroupKind>)new[] { TranscriptGroupKind.Exploration });
        var group = (GroupNode<ChatEntry, TranscriptGroupKind>.Group)nodes[0];
        return new TranscriptRow.Group(group.Kind, group.Children, group.Size, Completed: true, PendingToolCallIds: []);
    }

    [Test]
    public async Task CollapsedGroup_WeighsOne_ExpandedWeighsHeaderPlusChildren()
    {
        var t = DateTimeOffset.UtcNow;
        var group = ExplorationGroup(
            new ChatEntry("tool", "→ read", t, "a", "read"),
            new ChatEntry("tool", "→ glob", t, "b", "glob"));
        var row = (TranscriptRow)group;

        int collapsed = TranscriptMountBudget.RowWeight(row, (_, _) => false, _ => true);
        await Assert.That(collapsed).IsEqualTo(1);

        int expanded = TranscriptMountBudget.RowWeight(row, (_, _) => true, _ => true);
        await Assert.That(expanded).IsEqualTo(3);

        int ungrouped = TranscriptMountBudget.RowWeight(row, (_, _) => true, _ => false);
        await Assert.That(ungrouped).IsEqualTo(2);
    }

    [Test]
    public async Task RowsBefore_After_SpendBudgetByWeight()
    {
        var weights = new[] { 1, 1, 5, 1 };

        await Assert.That(TranscriptMountBudget.RowsBefore(weights, 4, 2)).IsEqualTo(2);
        await Assert.That(TranscriptMountBudget.RowsAfter(weights, 0, 2)).IsEqualTo(2);
        await Assert.That(TranscriptMountBudget.RowsBefore(weights, 4, 100)).IsEqualTo(0);
    }

    [Test]
    public async Task GroupKey_IsStableAcrossReprojection()
    {
        var t = DateTimeOffset.UtcNow;
        var entries = new[]
        {
            new ChatEntry("tool", "→ read", t, "a", "read"),
            new ChatEntry("tool", "→ glob", t, "b", "glob"),
        };

        string Key() => TranscriptMountBudget.GroupKey((TranscriptRow.Group)TranscriptGrouping.ProjectEntries(entries)[0]);
        await Assert.That(Key()).IsEqualTo(Key());
    }

    [Test]
    public async Task CompleteGroupBoundary_LoadsUntilWholeGroup()
    {
        var head = ExplorationGroup(new ChatEntry("tool", "→ read", DateTimeOffset.UtcNow, "a", "read"));
        var prompt = new TranscriptRow.Single(new ChatEntry("user", "hi", DateTimeOffset.UtcNow));
        var rows = new List<TranscriptRow> { head };
        int messages = 1;
        int loads = 0;

        // First page stays inside the group (head unchanged), second page
        // reaches the preceding prompt.
        await TranscriptMountBudget.CompleteGroupBoundaryAsync(
            rows,
            messageCount: () => messages,
            hasMore: () => loads < 2,
            loadMoreAsync: () =>
            {
                loads++;
                messages += 20;
                if (loads == 2)
                {
                    rows.Insert(0, prompt);
                }

                return Task.CompletedTask;
            },
            isActive: () => true);

        await Assert.That(loads).IsEqualTo(2);
        await Assert.That(ReferenceEquals(rows[0], prompt)).IsTrue();
    }

    [Test]
    public async Task CompleteGroupBoundary_StopsOnEmptyPage_OrInactive()
    {
        var head = ExplorationGroup(new ChatEntry("tool", "→ read", DateTimeOffset.UtcNow, "a", "read"));
        var rows = new List<TranscriptRow> { head };
        int loads = 0;

        await TranscriptMountBudget.CompleteGroupBoundaryAsync(
            rows,
            messageCount: () => 1,
            hasMore: () => true,
            loadMoreAsync: () => { loads++; return Task.CompletedTask; },
            isActive: () => false);

        await Assert.That(loads).IsEqualTo(0);
    }
}

public class ChatHistoryGroupingViewTests
{
    private static ChatHistoryViewModel ModelWith(params ChatEntry[] entries)
    {
        var vm = new ChatHistoryViewModel();
        foreach (ChatEntry entry in entries)
        {
            vm.AddEntry(entry);
        }

        return vm;
    }

    [Test]
    public async Task Render_Medium_FoldsExplorationBehindHeader()
    {
        var t = DateTimeOffset.UtcNow;
        var vm = ModelWith(
            new ChatEntry("tool", "→ read", t, "a", "read"),
            new ChatEntry("tool-result", "✓ 1", t, "a", "read"),
            new ChatEntry("tool", "→ glob", t, "b", "glob"));
        var view = new ChatHistoryView { ViewModel = vm };
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        await Assert.That(ctx.Output).Contains("[tool] 2 reads");
        await Assert.That(ctx.Output).Contains("→ read");
        await Assert.That(ctx.Output).Contains("→ glob");
    }

    [Test]
    public async Task Render_Low_CollapsesActivityToSummary_BlockedStaysVisible()
    {
        var t = DateTimeOffset.UtcNow;
        var vm = ModelWith(
            new ChatEntry("tool", "→ bash", t, "sh", "bash"),
            new ChatEntry("thinking", "hmm", t),
            new ChatEntry("assistant", "ok", t));
        var view = new ChatHistoryView
        {
            ViewModel = vm,
            Verbosity = TranscriptVerbosity.Low,
            PendingToolCalls = new HashSet<string>(StringComparer.Ordinal) { "sh" },
        };
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        await Assert.That(ctx.Output).Contains("[activity] 1 thought");
        await Assert.That(ctx.Output).Contains("→ bash");
        await Assert.That(ctx.Output.Contains("hmm")).IsFalse();
    }

    [Test]
    public async Task Render_MountBudget_TrimsFromLiveTail()
    {
        var t = DateTimeOffset.UtcNow;
        var vm = ModelWith(
            new ChatEntry("user", "old question", t),
            new ChatEntry("user", "new question", t));
        var view = new ChatHistoryView { ViewModel = vm, MountBudget = 1 };
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        await Assert.That(ctx.Output.Contains("old question")).IsFalse();
        await Assert.That(ctx.Output).Contains("new question");
    }

    [Test]
    public async Task Render_ThinkingEntry_UsesThinkingPrefix()
    {
        var vm = ModelWith(new ChatEntry("thinking", "deliberation", DateTimeOffset.UtcNow));
        var view = new ChatHistoryView { ViewModel = vm };
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        await Assert.That(ctx.Output).Contains("[thinking]");
        await Assert.That(ctx.Output).Contains("deliberation");
    }
}

public class ChatHistoryGroupingModelTests
{
    [Test]
    public async Task MessageEnd_FinalizesThinkingBeforeAssistant()
    {
        var vm = new ChatHistoryViewModel();
        await vm.UpdateFromEventAsync(new MessageStartEvent(AssistantMessage.Empty("s1", "m")));
        await vm.UpdateFromEventAsync(new MessageUpdateEvent(
            new ThinkingDeltaEvent("0", "let me think"), AssistantMessage.Empty("s1", "m")));
        await vm.UpdateFromEventAsync(new MessageUpdateEvent(
            new TextDeltaEvent("0", "answer"), AssistantMessage.Empty("s1", "m")));
        await vm.UpdateFromEventAsync(new MessageEndEvent(AssistantMessage.Empty("s1", "m")));

        await Assert.That(vm.Entries.Count).IsEqualTo(2);
        await Assert.That(vm.Entries[0].Role).IsEqualTo("thinking");
        await Assert.That(vm.Entries[0].Content).IsEqualTo("let me think");
        await Assert.That(vm.Entries[1].Role).IsEqualTo("assistant");
    }

    [Test]
    public async Task ToolResult_NamesItsTool()
    {
        var vm = new ChatHistoryViewModel();
        await vm.UpdateFromEventAsync(new MessageUpdateEvent(
            new ToolCallStartEvent("tc1", "read"), AssistantMessage.Empty("s1", "m")));
        await vm.UpdateFromEventAsync(new ToolExecutionEndEvent("tc1", new ToolResult("ok", false), false));

        await Assert.That(vm.Entries[0].ToolCallId).IsEqualTo("tc1");
        await Assert.That(vm.Entries[0].ToolName).IsEqualTo("read");
        await Assert.That(vm.Entries[1].ToolCallId).IsEqualTo("tc1");
        await Assert.That(vm.Entries[1].ToolName).IsEqualTo("read");
    }
}
