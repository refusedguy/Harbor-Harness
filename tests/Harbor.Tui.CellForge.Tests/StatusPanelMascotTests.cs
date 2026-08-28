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
}
