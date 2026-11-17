using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.State;
using Harbor.TestKit;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Issue #2358: subscription lifetime belongs to the owner. Every renderer
///     bus subscription is held through <see cref="EventSubscription"/> in a
///     field of its bridge and is released when that bridge is disposed, so a
///     torn-down screen cannot leave a live handler receiving events.
///     <para>
///         The behavioural tests below publish on a real <see cref="FakeEventBus"/>
///         and assert the handler stops observing events after Dispose. That is
///         the leak/removal proof, not a proxy such as a disposed flag. The two
///         source guards pin the shape that makes the property hold: no
///         fire-and-forget async in the stream bridges, and no second ownership
///         primitive (all <c>.Subscribe(</c> in CellForge goes through
///         <see cref="EventSubscription"/>).
///     </para>
/// </summary>
public class SubscriptionLifetimeTests
{
    [Test]
    public async Task EventSubscription_AutoSubscribe_DisposeUnsubscribes()
    {
        var bus = new FakeEventBus();
        int seen = 0;
        var sub = new EventSubscription(bus, (_, _) =>
        {
            seen++;
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(TestEvents.TurnStart());
        await Assert.That(seen).IsEqualTo(1);

        sub.Dispose();
        await bus.PublishAsync(TestEvents.TurnStart());
        await Assert.That(seen).IsEqualTo(1)
            .Because("a disposed subscription must no longer receive bus events");
    }

    [Test]
    public async Task EventSubscription_DisposeTwice_IsSafe()
    {
        var bus = new FakeEventBus();
        int seen = 0;
        var sub = new EventSubscription(bus, (_, _) =>
        {
            seen++;
            return ValueTask.CompletedTask;
        });

        sub.Dispose();
        sub.Dispose();
        await bus.PublishAsync(TestEvents.TurnStart());

        await Assert.That(seen).IsEqualTo(0)
            .Because("double disposal is idempotent and never re-subscribes");
    }

    [Test]
    public async Task EventSubscription_NoAutoSubscribe_RegistersNothing()
    {
        var bus = new FakeEventBus();
        int seen = 0;
        using var sub = new EventSubscription(bus, (_, _) =>
        {
            seen++;
            return ValueTask.CompletedTask;
        }, autoSubscribe: false);

        await bus.PublishAsync(TestEvents.TurnStart());
        await Assert.That(seen).IsEqualTo(0)
            .Because("a driven host pumps events itself and must not be auto-subscribed");
    }

    [Test]
    public async Task ChatScreenBridge_Dispose_RemovesBusSubscription()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        var status = new StatusViewModel();
        var bridge = new ChatScreenBridge(bus, panel, status); // auto-subscribe
        using (bridge)
        {
            await bus.PublishAsync(TestEvents.AgentStart(messages: [WidgetFixtures.UserMsg("s1", "first")]));
            int before = panel.Timeline.Count;
            await Assert.That(before).IsGreaterThan(0);

            bridge.Dispose();

            // A second run with a different message would append another block
            // if the handler were still on the bus.
            await bus.PublishAsync(TestEvents.AgentStart(messages: [WidgetFixtures.UserMsg("s2", "second")]));
            await Assert.That(panel.Timeline.Count).IsEqualTo(before)
                .Because("the disposed bridge must not receive AgentStart any more");
        }
    }

    [Test]
    public async Task InlineAgentStreamBridge_Dispose_RemovesBusSubscription()
    {
        var bus = new FakeEventBus();
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend);
        var session = new InlineSession(writer);
        var composer = new ComposerController();
        var bridge = new InlineAgentStreamBridge(bus, writer, session, composer);
        using (bridge)
        {
            bridge.Dispose();

            // If the bridge were still subscribed, MessageStart + TextDelta
            // would populate its stream and RenderLiveRegion would paint "after".
            await bus.PublishAsync(new MessageStartEvent(null!));
            await bus.PublishAsync(new MessageUpdateEvent(TestEvents.TextDelta("t", "after"), null!));
            bridge.Tick(nowMs: 2);
            _ = bridge.RenderLiveRegion();
            await bridge.FlushAsync();

            await Assert.That(backend.Escaped.Contains("after", StringComparison.Ordinal)).IsFalse()
                .Because("a disposed inline bridge must not handle stream events");
        }
    }

    /// <summary>
    ///     §FP-003 from AGENTS.md: a discarded <c>_ = SomeAsync()</c> in a stream
    ///     bridge loses faults and can strand state. The bridges own their
    ///     subscription and drive work synchronously (or via awaited calls), so
    ///     no fire-and-forget call belongs here. Comments are stripped so prose
    ///     about "fire-and-forget hosts" does not trip the guard.
    /// </summary>
    [Test]
    public async Task StreamBridges_HaveNoFireAndForgetAsync()
    {
        string root = RepoPaths.Root;
        string dir = Path.Combine(root, "src", "Harbor.Tui.CellForge", "Chat", "Streaming");
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            int lineNo = 0;
            foreach (string raw in File.ReadAllText(file).Split('\n'))
            {
                lineNo++;
                string code = raw;
                int slash = code.IndexOf("//", StringComparison.Ordinal);
                if (slash >= 0)
                {
                    code = code[..slash];
                }

                if (code.Contains("_ = ", StringComparison.Ordinal)
                    && code.Contains("Async(", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{lineNo}: {raw.Trim()}");
                }
            }
        }

        await Assert.That(offenders).IsEmpty();
    }

    /// <summary>
    ///     #2358 closes ownership, not a new extension axis: every bus
    ///     <c>.Subscribe(</c> in the CellForge renderer assemblies lives in
    ///     <see cref="EventSubscription"/>, the single owned handle primitive.
    ///     A second hand-rolled subscription site would let a handle escape its
    ///     owner's Dispose and re-open the leak.
    /// </summary>
    [Test]
    public async Task EveryCellForgeBusSubscription_UsesEventSubscription()
    {
        string root = RepoPaths.Root;
        var offenders = new List<string>();

        foreach (string relDir in new[]
                 {
                     Path.Combine("src", "Harbor.Tui.CellForge"),
                     Path.Combine("src", "Harbor.Tui.CellForge.Engine"),
                     Path.Combine("src", "Harbor.Tui.CellForge.Syntax"),
                     Path.Combine("src", "Harbor.Tui.CellForge.Prompts"),
                 })
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(root, relDir), "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, file);
                if (Path.GetFileName(file) == "EventSubscription.cs")
                {
                    continue;
                }

                if (File.ReadAllText(file).Contains(".Subscribe(", StringComparison.Ordinal))
                {
                    offenders.Add(rel);
                }
            }
        }

        await Assert.That(offenders).IsEmpty();
    }
}
