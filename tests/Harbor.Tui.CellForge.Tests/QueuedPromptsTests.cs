using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// O7 (#1176, opencode steal): queued prompts — submit-while-busy waits in the
/// composer queue instead of interrupting, the dock-counter reads its depth,
/// and undo/cancel hand the text back to the composer buffer.
/// </summary>
public class ComposerQueuedPromptsTests
{
    [Test]
    public async Task Enqueue_Trims_DropsEmpties_Counts()
    {
        var composer = new ComposerController();
        composer.EnqueueQueued("  first  ");
        composer.EnqueueQueued(string.Empty);
        composer.EnqueueQueued("   ");

        await Assert.That(composer.QueuedCount).IsEqualTo(1);
        await Assert.That(composer.QueuedPrompts[0]).IsEqualTo("first");
    }

    [Test]
    public async Task Dequeue_IsFifo()
    {
        var composer = new ComposerController();
        composer.EnqueueQueued("a");
        composer.EnqueueQueued("b");
        composer.EnqueueQueued("c");

        await Assert.That(composer.TryDequeueQueued(out var first)).IsTrue();
        await Assert.That(first).IsEqualTo("a");
        await Assert.That(composer.TryDequeueQueued(out var second)).IsTrue();
        await Assert.That(second).IsEqualTo("b");
        await Assert.That(composer.TryDequeueQueued(out var third)).IsTrue();
        await Assert.That(third).IsEqualTo("c");
        await Assert.That(composer.TryDequeueQueued(out _)).IsFalse();
        await Assert.That(composer.QueuedCount).IsEqualTo(0);
    }

    [Test]
    public async Task Undo_RemovesNewest_And_RestoreToComposer_ReturnsText()
    {
        var composer = new ComposerController();
        composer.EnqueueQueued("first");
        composer.EnqueueQueued("second");

        await Assert.That(composer.TryUndoQueued(out var undone)).IsTrue();
        await Assert.That(undone).IsEqualTo("second");
        await Assert.That(composer.QueuedCount).IsEqualTo(1);

        composer.RestoreToComposer(undone);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("second");
    }

    [Test]
    public async Task Undo_And_ClearReturningLast_OnEmpty_ReturnFalse()
    {
        var composer = new ComposerController();
        await Assert.That(composer.TryUndoQueued(out _)).IsFalse();
        await Assert.That(composer.ClearQueuedReturningLast(out var last)).IsFalse();
        await Assert.That(last).IsNull();
    }

    [Test]
    public async Task ClearQueuedReturningLast_HandsBackNewest_And_Drains()
    {
        var composer = new ComposerController();
        composer.EnqueueQueued("old");
        composer.EnqueueQueued("new");

        await Assert.That(composer.ClearQueuedReturningLast(out var last)).IsTrue();
        await Assert.That(last).IsEqualTo("new");
        await Assert.That(composer.QueuedCount).IsEqualTo(0);
        await Assert.That(composer.QueuedCounterText()).IsNull();
    }

    [Test]
    public async Task QueuedCounterText_NullWhenEmpty_OtherwiseCounts()
    {
        var composer = new ComposerController();
        await Assert.That(composer.QueuedCounterText()).IsNull();

        composer.EnqueueQueued("a");
        composer.EnqueueQueued("b");
        await Assert.That(composer.QueuedCounterText()).IsEqualTo("⏳ 2 queued");
    }
}

public class StreamCoalescerQueueTests
{
    private static StreamCoalescer New(out ChatTimelinePanel panel)
    {
        panel = new ChatTimelinePanel("chat", 40, 8);
        return new StreamCoalescer(panel, new StatusViewModel());
    }

    [Test]
    public async Task IsStreaming_TracksLiveSlot()
    {
        var coalescer = New(out _);
        await Assert.That(coalescer.IsStreaming).IsFalse();

        coalescer.StartStream();
        await Assert.That(coalescer.IsStreaming).IsTrue();

        coalescer.FinishStream();
        await Assert.That(coalescer.IsStreaming).IsFalse();
    }

    [Test]
    public async Task QueuedCounter_NullWhenEmpty_ClampsNegative()
    {
        var coalescer = New(out _);
        await Assert.That(coalescer.QueuedCounterText()).IsNull();

        coalescer.SetQueuedCount(3);
        await Assert.That(coalescer.QueuedCount).IsEqualTo(3);
        await Assert.That(coalescer.QueuedCounterText()).IsEqualTo("⏳ 3 queued");

        coalescer.SetQueuedCount(-5);
        await Assert.That(coalescer.QueuedCount).IsEqualTo(0);
        await Assert.That(coalescer.QueuedCounterText()).IsNull();
    }
}
