using Harbor.Abstractions.Events;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Issue #170: the mascot died forever after an error (150-tick latch, and no
/// Idle heartbeat so ticks froze and the latch never expired). The latch is
/// wall-clock now, <see cref="AgentPhase.Auto"/> clears stale state on the run
/// boundary, and <see cref="ChatScreenBridge.IsMascotAnimating"/> keeps the
/// heartbeat alive until the sequence plays out.
/// </summary>
public class MascotReviveTests
{
    private static StatusViewModel IdleStatus(AgentPhase phase = AgentPhase.Auto) =>
        new() { Model = "m", Mode = StatusBarMode.Idle, Phase = phase };

    [Test]
    public async Task Latch_Expires_ByTime_NotByTicks()
    {
        var director = new MascotDirector(moodLatchMs: 100);
        var status = IdleStatus(AgentPhase.Errored);

        await Assert.That(director.Advance(status, tick: 1)).IsEqualTo(MascotMood.Error);
        await Assert.That(director.HasActiveAnimation).IsTrue();

        // Fast frames past the old 150-tick budget must NOT expire a time latch.
        for (long tick = 2; tick < 250; tick++)
        {
            _ = director.Advance(status, tick);
        }

        await Assert.That(director.Advance(status, tick: 250)).IsEqualTo(MascotMood.Error);

        await Task.Delay(300);
        await Assert.That(director.Advance(status, tick: 251)).IsEqualTo(MascotMood.Idle);
        await Assert.That(director.HasActiveAnimation).IsFalse();
    }

    [Test]
    public async Task Auto_Clears_StaleErrorLatch_Immediately()
    {
        // Hour-long latch: only the run boundary may clear it here, not time.
        var director = new MascotDirector(moodLatchMs: 3_600_000);
        var status = IdleStatus(AgentPhase.Errored);

        await Assert.That(director.Advance(status, tick: 1)).IsEqualTo(MascotMood.Error);
        await Assert.That(director.HasActiveAnimation).IsTrue();

        status.Phase = AgentPhase.Auto;
        await Assert.That(director.Advance(status, tick: 2)).IsEqualTo(MascotMood.Idle);
        await Assert.That(director.HasActiveAnimation).IsFalse();
    }

    [Test]
    public async Task HasActiveAnimation_Tracks_Reaction_Then_Goes_Quiet()
    {
        var director = new MascotDirector();
        var status = IdleStatus();

        await Assert.That(director.HasActiveAnimation).IsFalse();

        director.Notify(MascotReaction.ErrorBlink, tick: 1);
        await Assert.That(director.HasActiveAnimation).IsTrue();

        // Drain the reaction the way panels do: Advance + TryReactionFrame tick.
        long tick = 1;
        while (director.TryReactionFrame(tick, out _, out _))
        {
            _ = director.Advance(status, tick);
            tick++;
            if (tick > 100)
            {
                break; // guard: the blink is 9 ticks
            }
        }

        await Assert.That(director.HasActiveAnimation).IsFalse();
    }

    [Test]
    public async Task Bridge_IsMascotAnimating_Tracks_Error_Then_Auto_Revive_Footer()
    {
        var bus = new FakeEventBus();
        var composer = new ComposerController();
        var status = new StatusViewModel { Model = "m", Mode = StatusBarMode.Idle };
        var screen = ChatScreen.Build(composer, status, includeSidebar: false);
        var buffer = new ScreenBuffer(120, 8);
        screen.Tree.Solve(120, 8);
        using var bridge = new ChatScreenBridge(bus, screen.Timeline, status);
        bridge.TrackMascot(screen.Mascot, screen.Status);

        await Assert.That(bridge.IsMascotAnimating).IsFalse();

        await bus.PublishAsync(new AgentErrorEvent("boom"));
        PaintAll(screen, buffer); // footer consumes the blink + arms the latch
        await Assert.That(bridge.IsMascotAnimating).IsTrue();

        // Next run boundary clears the stale latch; draining the blink ticks
        // settles the footer cat with no heartbeat debt left.
        await bus.PublishAsync(new AgentStartEvent("s1", []));
        PaintAll(screen, buffer, MascotDirector.ReactionFrames * MascotDirector.ReactionFrameTicks + 2);
        await Assert.That(bridge.IsMascotAnimating).IsFalse();
    }

    [Test]
    public async Task Bridge_IsMascotAnimating_Tracks_Error_Then_Auto_Revive_Panel()
    {
        var bus = new FakeEventBus();
        var composer = new ComposerController();
        var status = new StatusViewModel { Model = "m", Mode = StatusBarMode.Idle };
        var screen = ChatScreen.Build(composer, status, includeSidebar: false, mascotMode: MascotMode.Panel);
        var buffer = new ScreenBuffer(120, 24);
        screen.Tree.Solve(120, 24);
        using var bridge = new ChatScreenBridge(bus, screen.Timeline, status);
        bridge.TrackMascot(screen.Mascot, screen.Status);

        await Assert.That(screen.Mascot).IsNotNull();
        await Assert.That(bridge.IsMascotAnimating).IsFalse();

        await bus.PublishAsync(new AgentErrorEvent("boom"));
        PaintAll(screen, buffer); // panel consumes the blink + arms the latch
        await Assert.That(bridge.IsMascotAnimating).IsTrue();

        await bus.PublishAsync(new AgentStartEvent("s1", []));
        PaintAll(screen, buffer, MascotDirector.ReactionFrames * MascotDirector.ReactionFrameTicks + 2);
        await Assert.That(bridge.IsMascotAnimating).IsFalse();
    }

    private static void PaintAll(ChatScreen screen, ScreenBuffer buffer, int frames = 1)
    {
        for (int i = 0; i < frames; i++)
        {
            foreach (var panel in screen.Tree.Panels)
            {
                panel.Paint(buffer);
            }
        }
    }
}
