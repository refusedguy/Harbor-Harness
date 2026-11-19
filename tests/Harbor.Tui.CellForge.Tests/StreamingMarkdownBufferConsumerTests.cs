using Harbor.TestKit;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.State;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #3363: MarkdownStreamBuffer is wired into the real MessageUpdateEvent
/// streaming path, not only exercised in isolation. The deltas below split
/// Markdown markers the way a provider does; the live block must hide a
/// construct until its closing marker streams in, and the committed block
/// must carry the exact assembled source.
/// </summary>
public class StreamingMarkdownBufferConsumerTests
{
    private static (FakeEventBus Bus, ChatTimelinePanel Panel, ChatScreenBridge Bridge) New()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 40, 8);
        var bridge = new ChatScreenBridge(bus, panel, new StatusViewModel());
        return (bus, panel, bridge);
    }

    /// <summary>Age the paced queue past its severe threshold so every queued
    /// line is pushed in one tick.</summary>
    private static void Drain(ChatScreenBridge bridge)
    {
        for (int i = 1; i <= 8; i++)
        {
            bridge.Tick(i * 200);
        }
    }

    private static StreamingMarkdownBlock Live(ChatTimelinePanel panel) =>
        (StreamingMarkdownBlock)panel.Timeline.BlockAt(0);

    [Test]
    public async Task SplitBoldDeltas_AssembleIntoOneMarkdownBlock()
    {
        var (bus, panel, bridge) = New();
        using var _ = bridge;

        await bus.PublishAsync(TestEvents.MessageStart());
        await bus.PublishAsync(TestEvents.TextDeltaUpdate(delta: "Intro **"));
        await bus.PublishAsync(TestEvents.TextDeltaUpdate(delta: "bold text"));
        await bus.PublishAsync(TestEvents.TextDeltaUpdate(delta: "\n"));
        Drain(bridge);

        // The bold run is still open: the live block shows only the safe prefix.
        string mid = Live(panel).RawText();
        await Assert.That(mid.Contains("Intro")).IsTrue();
        await Assert.That(mid.Contains("bold")).IsFalse();

        await bus.PublishAsync(TestEvents.TextDeltaUpdate(delta: "** done\n"));
        Drain(bridge);

        // The closing marker widens the visible prefix; nothing is duplicated.
        string revealed = Live(panel).RawText();
        await Assert.That(revealed.Contains("bold text")).IsTrue();
        await Assert.That(revealed.Contains("Intro")).IsTrue();

        await bus.PublishAsync(TestEvents.MessageEnd(WidgetFixtures.AssistantMsg("s1", "Intro bold text done")));

        var committed = (AssistantMarkdownBlock)panel.Timeline.BlockAt(0);
        await Assert.That(committed.RawText()).IsEqualTo("Intro **bold text\n** done\n");
    }

    [Test]
    public async Task FenceDeltas_HoldBody_AndCommitVerbatim()
    {
        var (bus, panel, bridge) = New();
        using var _ = bridge;

        await bus.PublishAsync(TestEvents.MessageStart());
        await bus.PublishAsync(TestEvents.TextDeltaUpdate(delta: "before\n"));
        await bus.PublishAsync(TestEvents.TextDeltaUpdate(delta: "```py\n"));
        await bus.PublishAsync(TestEvents.TextDeltaUpdate(delta: "print(1)\n"));
        Drain(bridge);

        string mid = Live(panel).RawText();
        await Assert.That(mid.Contains("before")).IsTrue();
        await Assert.That(mid.Contains("print")).IsFalse();

        await bus.PublishAsync(TestEvents.TextDeltaUpdate(delta: "```\n"));
        await bus.PublishAsync(TestEvents.TextDeltaUpdate(delta: "after\n"));
        Drain(bridge);

        string revealed = Live(panel).RawText();
        await Assert.That(revealed.Contains("print(1)")).IsTrue();
        await Assert.That(revealed.Contains("after")).IsTrue();

        await bus.PublishAsync(TestEvents.MessageEnd(WidgetFixtures.AssistantMsg("s1", "finished")));

        var committed = (AssistantMarkdownBlock)panel.Timeline.BlockAt(0);
        await Assert.That(committed.RawText()).IsEqualTo("before\n```py\nprint(1)\n```\nafter\n");
    }
}
