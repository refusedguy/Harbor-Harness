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

    /// <summary>
    /// Manual clock (#1007): the test owns the passage of time, so a latch
    /// assertion is a function of the elapsed time this test declares and
    /// cannot be perturbed by how fast the runner is. BCL only, no new
    /// package — the shape <c>RetryPolicyTests</c> established for
    /// <c>RetryPolicy</c> (#54).
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        /// <summary>
        /// One timestamp tick is one millisecond, so the director's scale
        /// factor is exactly 1.0 and every deadline below is exact integer
        /// arithmetic instead of a rounded conversion.
        /// </summary>
        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp() => ElapsedMs;

        /// <summary>Milliseconds this clock has been advanced by the test.</summary>
        public long ElapsedMs { get; private set; }

        public void AdvanceMs(long ms) => ElapsedMs += ms;
    }

    [Test]
    public async Task Latch_Expires_ByTime_NotByTicks()
    {
        var clock = new ManualTimeProvider();

        // The PRODUCTION latch, not a shrunken one. The old version passed
        // moodLatchMs: 100 and then raced 248 Advance calls against a real
        // Task.Delay, so the assertion that "time has NOT expired" depended on
        // 248 calls finishing inside 100 ms — it failed on a loaded runner by
        // failing the part that proves nothing yet. With the clock injected
        // there is nothing left to race, so the test states the real 12 s
        // budget instead of a 120x-compressed number the product never uses.
        var director = new MascotDirector(timeProvider: clock);
        var status = IdleStatus(AgentPhase.Errored);

        await Assert.That(director.Advance(status, tick: 1)).IsEqualTo(MascotMood.Error);
        await Assert.That(director.HasActiveAnimation).IsTrue();

        // 248 frames past the old 150-tick latch, with the clock still at zero:
        // ticks ran away, time did not.
        for (long tick = 2; tick < 250; tick++)
        {
            _ = director.Advance(status, tick);
        }

        await Assert.That(director.Advance(status, tick: 250)).IsEqualTo(MascotMood.Error);
        await Assert.That(director.HasActiveAnimation).IsTrue();

        // One millisecond short of the budget, at the SAME tick: still held.
        // This pins the deadline to MoodLatchMs rather than to "somewhere
        // under 12 s" — a latch that expired early, or one keyed to ticks, or
        // one keyed to frames, cannot satisfy both of the next two assertions.
        clock.AdvanceMs(MascotDirector.MoodLatchMs - 1);
        await Assert.That(director.Advance(status, tick: 250)).IsEqualTo(MascotMood.Error);
        await Assert.That(director.HasActiveAnimation).IsTrue();

        // The 12_000th millisecond, at the SAME tick again: flipped. Between
        // the assertion above and this one the tick did not move and the clock
        // crossed exactly one boundary, so elapsed time is the only thing that
        // can explain the change of mood.
        clock.AdvanceMs(1);
        await Assert.That(director.Advance(status, tick: 250)).IsEqualTo(MascotMood.Idle);
        await Assert.That(director.HasActiveAnimation).IsFalse();
    }

    [Test]
    public async Task Auto_Clears_StaleErrorLatch_Immediately()
    {
        var clock = new ManualTimeProvider();
        // Production latch on an injected clock: 1 ms is four orders of
        // magnitude short of the 12 s budget, so time cannot be the cause of
        // what happens next. The hour-long proxy this replaces existed only
        // to say the same thing (#1026).
        var director = new MascotDirector(timeProvider: clock);
        var status = IdleStatus(AgentPhase.Errored);

        await Assert.That(director.Advance(status, tick: 1)).IsEqualTo(MascotMood.Error);
        await Assert.That(director.HasActiveAnimation).IsTrue();

        clock.AdvanceMs(1);
        await Assert.That(director.Advance(status, tick: 2)).IsEqualTo(MascotMood.Error);
        await Assert.That(director.HasActiveAnimation).IsTrue();

        status.Phase = AgentPhase.Auto;
        await Assert.That(director.Advance(status, tick: 3)).IsEqualTo(MascotMood.Idle);
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
