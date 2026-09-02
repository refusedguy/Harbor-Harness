using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Event reactions (mascot-brand T3): error blink, success bounce, approval
/// wiggle — short overlay sequences that override the mood frames, tinted by
/// event accent, played exactly once per signal. Deterministic ticks only.
/// </summary>
public class MascotReactionTests
{
    private static (ChatScreen Screen, ScreenBuffer Buffer) BuildFooterScreen(int cols = 120, int rows = 8)
    {
        var composer = new ComposerController();
        var status = new StatusViewModel { Model = "m", Mode = StatusBarMode.Idle };
        var screen = ChatScreen.Build(composer, status, includeSidebar: false);
        var buffer = new ScreenBuffer(cols, rows);
        screen.Tree.Solve(cols, rows);
        return (screen, buffer);
    }

    /// <summary>Paints all panels <paramref name="frames" /> times, returning only the LAST frame's art.</summary>
    private static string PaintLastFrame(ChatScreen screen, ScreenBuffer buffer, int frames)
    {
        for (int i = 0; i < frames - 1; i++)
        {
            foreach (var panel in screen.Tree.Panels)
            {
                panel.Paint(buffer);
            }
        }

        foreach (var panel in screen.Tree.Panels)
        {
            panel.Paint(buffer);
        }

        return GridDump.Art(buffer);
    }

    [Test]
    public async Task ErrorBlink_PlaysThreeFrames_ThenMoodResumes()
    {
        var (screen, buffer) = BuildFooterScreen();
        var status = screen.Status.Vm;

        _ = PaintLastFrame(screen, buffer, 1); // settle, tick 1

        status.SignalMascot(MascotReaction.ErrorBlink);
        string f0 = PaintLastFrame(screen, buffer, 1); // notify tick — frame 0
        await Assert.That(f0).Contains(AmbientMascot.ErrorBlinkFrames[0]);

        string f1 = PaintLastFrame(screen, buffer, MascotDirector.ReactionFrameTicks);
        await Assert.That(f1).Contains(AmbientMascot.ErrorBlinkFrames[1]);

        string f2 = PaintLastFrame(screen, buffer, MascotDirector.ReactionFrameTicks);
        await Assert.That(f2).Contains(AmbientMascot.ErrorBlinkFrames[2]);

        string after = PaintLastFrame(screen, buffer, MascotDirector.ReactionFrameTicks); // expiry tick (11)
        await Assert.That(after).DoesNotContain(AmbientMascot.ErrorBlinkFrames[0]);
        await Assert.That(after).DoesNotContain(AmbientMascot.ErrorBlinkFrames[2]);
        await Assert.That(after).Contains(AmbientMascot.IdleFrames[11 % AmbientMascot.IdleFrames.Length]);
    }

    [Test]
    public async Task Blink_TintsMascot_WithEventAccent()
    {
        var (screen, buffer) = BuildFooterScreen();
        var status = screen.Status.Vm;

        _ = PaintLastFrame(screen, buffer, 1);
        status.SignalMascot(MascotReaction.ErrorBlink);
        _ = PaintLastFrame(screen, buffer, 1); // notify paint — settled tint

        // The '(' of the blink face sits at the trailing edge of the status row.
        int x = screen.Status.Rect.Right - AmbientMascot.Width(AmbientMascot.ErrorBlinkFrames[0]);
        int y = screen.Status.Rect.Y;
        await Assert.That(buffer.Get(x, y).Style == ChatPalette.ToolError).IsTrue();
    }

    [Test]
    public async Task SuccessBounce_PlaysThreeFrames_ThenMoodResumes()
    {
        var (screen, buffer) = BuildFooterScreen();
        var status = screen.Status.Vm;

        _ = PaintLastFrame(screen, buffer, 1);
        status.SignalMascot(MascotReaction.SuccessBounce);

        string f0 = PaintLastFrame(screen, buffer, 1);
        await Assert.That(f0).Contains(AmbientMascot.SuccessBounceFrames[0]);

        string f1 = PaintLastFrame(screen, buffer, MascotDirector.ReactionFrameTicks);
        await Assert.That(f1).Contains(AmbientMascot.SuccessBounceFrames[1]);

        string after = PaintLastFrame(screen, buffer, 2 * MascotDirector.ReactionFrameTicks);
        await Assert.That(after).DoesNotContain(AmbientMascot.SuccessBounceFrames[1]);
    }

    [Test]
    public async Task ApprovalWiggle_PlaysThreeFrames_ThenMoodResumes()
    {
        var (screen, buffer) = BuildFooterScreen();
        var status = screen.Status.Vm;

        _ = PaintLastFrame(screen, buffer, 1);
        status.SignalMascot(MascotReaction.ApprovalWiggle);

        string f0 = PaintLastFrame(screen, buffer, 1);
        await Assert.That(f0).Contains(AmbientMascot.ApprovalWiggleFrames[0]);

        string f1 = PaintLastFrame(screen, buffer, MascotDirector.ReactionFrameTicks);
        await Assert.That(f1).Contains(AmbientMascot.ApprovalWiggleFrames[1]);

        string after = PaintLastFrame(screen, buffer, 2 * MascotDirector.ReactionFrameTicks);
        await Assert.That(after).DoesNotContain(AmbientMascot.ApprovalWiggleFrames[0]);
    }

    [Test]
    public async Task ReactionBanks_AreEightCells_AndAsciiOnly()
    {
        foreach (var reaction in new[]
                 {
                     MascotReaction.ErrorBlink,
                     MascotReaction.SuccessBounce,
                     MascotReaction.ApprovalWiggle,
                 })
        {
            string[] faces = AmbientMascot.ReactionFramesOf(reaction);
            string[] ears = AmbientMascot.ReactionEars(reaction);
            string[] paws = AmbientMascot.ReactionPaws(reaction);

            await Assert.That(faces.Length).IsEqualTo(MascotDirector.ReactionFrames);
            await Assert.That(ears.Length).IsEqualTo(MascotDirector.ReactionFrames);
            await Assert.That(paws.Length).IsEqualTo(MascotDirector.ReactionFrames);

            for (int i = 0; i < faces.Length; i++)
            {
                await Assert.That(faces[i].Length).IsEqualTo(8);
                await Assert.That(ears[i].Length).IsEqualTo(8);
                await Assert.That(paws[i].Length).IsEqualTo(8);
                await Assert.That(faces[i].All(c => c < 128)).IsTrue();
                await Assert.That(ears[i].All(c => c < 128)).IsTrue();
                await Assert.That(paws[i].All(c => c < 128)).IsTrue();
            }
        }
    }

    [Test]
    public async Task Signal_IsConsumedExactlyOnce_ByTheRenderingPanel()
    {
        var (screen, buffer) = BuildFooterScreen();
        var status = screen.Status.Vm;

        status.SignalMascot(MascotReaction.ErrorBlink);
        _ = PaintLastFrame(screen, buffer, 1); // panel consumed it

        await Assert.That(status.ConsumeMascotSignal()).IsEqualTo(MascotReaction.None);
    }

    [Test]
    public async Task PanelMode_Blink_ShowsFlatEars_AndTint()
    {
        var composer = new ComposerController();
        var status = new StatusViewModel { Model = "m", Mode = StatusBarMode.Idle };
        var screen = ChatScreen.Build(composer, status, includeSidebar: false, mascotMode: MascotMode.Panel);
        var buffer = new ScreenBuffer(120, 24);
        screen.Tree.Solve(120, 24);
        var mascot = screen.Mascot!;

        mascot.Paint(buffer); // settle

        status.SignalMascot(MascotReaction.ErrorBlink);
        mascot.Paint(buffer); // notify paint — frame 0, settled tint

        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains(AmbientMascot.ErrorBlinkFrames[0]);
        await Assert.That(art).Contains(AmbientMascot.ReactionEars(MascotReaction.ErrorBlink)[0]);

        int fx = mascot.Rect.X + 3;
        int fy = mascot.Rect.Y + 1; // face row
        await Assert.That(buffer.Get(fx, fy).Style == ChatPalette.ToolError).IsTrue();
    }

    [Test]
    public async Task Bridge_Events_ArmTheMatchingReactions()
    {
        var bus = new FakeEventBus();
        var panel = new ChatTimelinePanel("chat", 20, 4);
        var status = new StatusViewModel();
        using var bridge = new ChatScreenBridge(bus, panel, status);

        await bus.PublishAsync(new AgentStartEvent("s1", []));
        await Assert.That(status.ConsumeMascotSignal()).IsEqualTo(MascotReaction.None);

        await bus.PublishAsync(new AgentErrorEvent("boom"));
        await Assert.That(status.ConsumeMascotSignal()).IsEqualTo(MascotReaction.ErrorBlink);
        await Assert.That(status.ConsumeMascotSignal()).IsEqualTo(MascotReaction.None);

        await bus.PublishAsync(new AgentEndEvent([])); // errored run — no bounce
        await Assert.That(status.ConsumeMascotSignal()).IsEqualTo(MascotReaction.None);

        await bus.PublishAsync(new AgentStartEvent("s1", []));
        await bus.PublishAsync(new AgentEndEvent([])); // clean run — bounce
        await Assert.That(status.ConsumeMascotSignal()).IsEqualTo(MascotReaction.SuccessBounce);

        _ = bridge.BeginApprovalGate("bash", "ls -la /tmp");
        await Assert.That(status.ConsumeMascotSignal()).IsEqualTo(MascotReaction.ApprovalWiggle);
    }

    [Test]
    public async Task ReactionPaint_IsAllocationFree()
    {
        var composer = new ComposerController();
        var status = new StatusViewModel { Model = "m", Mode = StatusBarMode.Idle };
        var screen = ChatScreen.Build(composer, status, includeSidebar: false, mascotMode: MascotMode.Panel);
        var buffer = new ScreenBuffer(120, 24);
        screen.Tree.Solve(120, 24);
        var mascot = screen.Mascot!;

        // Warmup: every reaction sequence (notify frame, held frames, blend
        // frames, expiry) plus mood blends — one-time tiered-JIT allocations
        // must stay out of the measured window.
        mascot.Paint(buffer);
        foreach (var reaction in new[]
                 {
                     MascotReaction.ErrorBlink,
                     MascotReaction.SuccessBounce,
                     MascotReaction.ApprovalWiggle,
                 })
        {
            status.SignalMascot(reaction);
            for (int i = 0; i < MascotDirector.ReactionFrames * MascotDirector.ReactionFrameTicks + 2; i++)
            {
                mascot.Paint(buffer);
            }
        }

        status.Phase = AgentPhase.Errored;
        for (int i = 0; i < 12; i++)
        {
            mascot.Paint(buffer);
        }

        status.Phase = AgentPhase.Auto;
        for (int i = 0; i < PanelFx.FadeFrames + 2; i++)
        {
            mascot.Paint(buffer);
        }

        GC.WaitForPendingFinalizers();

        // Declared before the measurement window — the array literal itself
        // would otherwise be counted against the zero-alloc budget.
        var reactions = new[]
        {
            MascotReaction.ErrorBlink,
            MascotReaction.SuccessBounce,
            MascotReaction.ApprovalWiggle,
        };

        var before = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 150;
        for (int i = 0; i < iterations; i++)
        {
            if (i % 15 == 0)
            {
                status.SignalMascot(reactions[(i / 15) % reactions.Length]);
            }

            mascot.Paint(buffer);
        }

        var after = GC.GetAllocatedBytesForCurrentThread();

        await Assert.That(after - before).IsEqualTo(0);
    }
}
