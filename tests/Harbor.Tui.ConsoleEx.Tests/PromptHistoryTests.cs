using Harbor.Tui.ConsoleEx.Rendering;

namespace Harbor.Tui.ConsoleEx.Tests;

public class PromptHistoryTests
{
    [Test]
    public async Task Push_Trims_DropsEmpty_AndCollapsesConsecutiveDupes()
    {
        var h = new PromptHistory();

        h.Push("   ");
        h.Push("run tests");
        h.Push("run tests");
        h.Push("\nbuild solution\n");

        await Assert.That(h.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Recall_BackAndForth_PreservesDraft()
    {
        var h = new PromptHistory();
        h.Push("first");
        h.Push("second");

        // Up from draft «wip» → newest, then oldest.
        await Assert.That(h.TryRecallPrevious("wip", out var e1)).IsTrue();
        await Assert.That(e1).IsEqualTo("second");
        await Assert.That(h.TryRecallPrevious(string.Empty, out var e2)).IsTrue();
        await Assert.That(e2).IsEqualTo("first");
        // Oldest boundary.
        await Assert.That(h.TryRecallPrevious(string.Empty, out _)).IsFalse();

        // Down walks back forward and finally restores the draft.
        await Assert.That(h.TryRecallNext(out var e3)).IsTrue();
        await Assert.That(e3).IsEqualTo("second");
        await Assert.That(h.TryRecallNext(out var e4)).IsTrue();
        await Assert.That(e4).IsEqualTo("wip");
        // Walk ended — further Down is plain caret movement.
        await Assert.That(h.TryRecallNext(out _)).IsFalse();
    }

    [Test]
    public async Task EmptyHistory_Recall_IsNoOp()
    {
        var h = new PromptHistory();
        await Assert.That(h.TryRecallPrevious("draft", out _)).IsFalse();
        await Assert.That(h.TryRecallNext(out _)).IsFalse();
    }

    [Test]
    public async Task Capacity_EvictsOldest()
    {
        var h = new PromptHistory(capacity: 3);
        h.Push("a");
        h.Push("b");
        h.Push("c");
        h.Push("d");

        await Assert.That(h.Count).IsEqualTo(3);
        await Assert.That(h.TryRecallPrevious("latest draft", out var newest)).IsTrue();
        await Assert.That(newest).IsEqualTo("d");
        await Assert.That(h.TryRecallPrevious(string.Empty, out var mid)).IsTrue();
        await Assert.That(mid).IsEqualTo("c");
        // Forward again: newest entry again, then the captured draft.
        await Assert.That(h.TryRecallNext(out var newestAgain)).IsTrue();
        await Assert.That(newestAgain).IsEqualTo("d");
        await Assert.That(h.TryRecallNext(out var draft)).IsTrue();
        await Assert.That(draft).IsEqualTo("latest draft");
    }

    [Test]
    public async Task Reset_AbandonsWalk_KeepsDraftFuturePushes()
    {
        var h = new PromptHistory();
        h.Push("one");

        _ = h.TryRecallPrevious("draft", out _);
        h.Reset();

        await Assert.That(h.TryRecallNext(out _)).IsFalse();
        h.Push("two");
        await Assert.That(h.TryRecallPrevious(string.Empty, out var entry)).IsTrue();
        await Assert.That(entry).IsEqualTo("two");
    }
}
