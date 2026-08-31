using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Ambient mascot footer wiring (sprint UI-V2 P6.1): the status panel frames
/// the mascot at the trailing edge on wide rows only — narrow rows keep the
/// full width for status segments. Deterministic ticks, no wall clock
/// (the sleeping mood needs 60s of idle uptime and is not asserted here).
/// </summary>
public class StatusPanelMascotTests
{
    private static string PaintStatusRow(int cols, StatusBarMode mode)
    {
        var composer = new ComposerController();
        var status = new StatusViewModel { Model = "m", Mode = mode };
        var screen = ChatScreen.Build(composer, status, includeSidebar: false);
        var buffer = new ScreenBuffer(cols, 8);
        screen.Tree.Solve(cols, 8);
        foreach (var panel in screen.Tree.Panels)
        {
            panel.Paint(buffer);
        }

        return GridDump.Art(buffer);
    }

    [Test]
    public async Task WideRow_Running_ShowsWorkingMascotAtTrailingEdge()
    {
        string art = PaintStatusRow(120, StatusBarMode.Running);

        // First paint → Tick = 1 → WorkingFrames[1].
        await Assert.That(art).Contains(AmbientMascot.WorkingFrames[1]);
    }

    [Test]
    public async Task WideRow_AwaitingApproval_ShowsAwaitingMascot()
    {
        string art = PaintStatusRow(120, StatusBarMode.AwaitingApproval);

        await Assert.That(art).Contains(AmbientMascot.AwaitingFrames[1]);
    }

    [Test]
    public async Task NarrowRow_NeverPaintsMascot()
    {
        foreach (var mode in Enum.GetValues<StatusBarMode>())
        {
            string art = PaintStatusRow(72, mode);
            foreach (var frame in AmbientMascot.WorkingFrames
                         .Concat(AmbientMascot.AwaitingFrames)
                         .Concat(AmbientMascot.IdleFrames)
                         .Concat(AmbientMascot.SleepingFrames))
            {
                await Assert.That(art.Contains(frame)).IsFalse();
            }
        }
    }

    [Test]
    public async Task WideRow_Idle_ShowsIdleMascot_BeforeSleepThreshold()
    {
        string art = PaintStatusRow(120, StatusBarMode.Idle);

        await Assert.That(art).Contains(AmbientMascot.IdleFrames[1]);
    }

    [Test]
    public async Task WideRow_ThinkingPhase_ShowsThinkingMascot()
    {
        var composer = new ComposerController();
        var status = new StatusViewModel { Model = "m", Mode = StatusBarMode.Running, Phase = AgentPhase.Thinking };
        var screen = ChatScreen.Build(composer, status, includeSidebar: false);
        var buffer = new ScreenBuffer(120, 8);
        screen.Tree.Solve(120, 8);

        string art = PaintFrames(screen, buffer, 4);
        await Assert.That(art).Contains(AmbientMascot.ThinkingFrames[1]);
        await Assert.That(art).Contains(AmbientMascot.ThinkingFrames[3]);
    }

    [Test]
    public async Task WideRow_ToolCallPhase_ShowsToolCallMascot()
    {
        var composer = new ComposerController();
        var status = new StatusViewModel { Model = "m", Mode = StatusBarMode.Running, Phase = AgentPhase.ToolCall };
        var screen = ChatScreen.Build(composer, status, includeSidebar: false);
        var buffer = new ScreenBuffer(120, 8);
        screen.Tree.Solve(120, 8);

        string art = PaintFrames(screen, buffer, 4);
        await Assert.That(art).Contains(AmbientMascot.ToolCallFrames[1]);
        await Assert.That(art).Contains(AmbientMascot.ToolCallFrames[2]);
    }

    [Test]
    public async Task ErroredPhase_LatchesErrorMood_ThenRevertsToIdle()
    {
        var composer = new ComposerController();
        var status = new StatusViewModel { Model = "m", Mode = StatusBarMode.Idle, Phase = AgentPhase.Errored };
        var screen = ChatScreen.Build(composer, status, includeSidebar: false);
        var buffer = new ScreenBuffer(120, 8);
        screen.Tree.Solve(120, 8);

        string first = PaintLastFrame(screen, buffer, 1);
        await Assert.That(first).Contains(AmbientMascot.ErrorFrames[1]);

        // Latch expires after MoodLatchFrames ticks from its start (tick 1):
        // the last of the 150 warm-up paints lands on tick 151 → derived mood.
        string after = PaintLastFrame(screen, buffer, MascotDirector.MoodLatchFrames);
        await Assert.That(after).Contains(AmbientMascot.IdleFrames[(1 + MascotDirector.MoodLatchFrames) % AmbientMascot.IdleFrames.Length]);
        await Assert.That(after).DoesNotContain(AmbientMascot.ErrorFrames[0]);
        await Assert.That(after).DoesNotContain(AmbientMascot.ErrorFrames[1]);
    }

    [Test]
    public async Task SucceededPhase_LatchesSuccessMood_ThenRevertsToIdle()
    {
        var composer = new ComposerController();
        var status = new StatusViewModel { Model = "m", Mode = StatusBarMode.Idle, Phase = AgentPhase.Succeeded };
        var screen = ChatScreen.Build(composer, status, includeSidebar: false);
        var buffer = new ScreenBuffer(120, 8);
        screen.Tree.Solve(120, 8);

        string first = PaintLastFrame(screen, buffer, 1);
        await Assert.That(first).Contains(AmbientMascot.SuccessFrames[1]);

        string after = PaintLastFrame(screen, buffer, MascotDirector.MoodLatchFrames);
        await Assert.That(after).DoesNotContain(AmbientMascot.SuccessFrames[0]);
        await Assert.That(after).DoesNotContain(AmbientMascot.SuccessFrames[1]);
    }

    /// <summary>Paints all panels <paramref name="frames" /> times, returning the concatenated art.</summary>
    private static string PaintFrames(ChatScreen screen, ScreenBuffer buffer, int frames)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < frames; i++)
        {
            foreach (var panel in screen.Tree.Panels)
            {
                panel.Paint(buffer);
            }

            sb.Append(GridDump.Art(buffer));
            sb.Append('\n');
        }

        return sb.ToString();
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
}
