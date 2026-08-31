using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Panel-mode mascot (mascot-brand T2): a 3-row cat beside the composer via
/// LayoutTree.Split, toggled by HARBOR_MASCOT_MODE=panel. Footer mode must be
/// byte-unchanged and the HARBOR_MASCOT=off kill-switch must keep winning.
/// </summary>
public class MascotPanelTests
{
    private static (ChatScreen Screen, ScreenBuffer Buffer) BuildScreen(MascotMode mode, int cols, int rows)
    {
        var composer = new ComposerController();
        var status = new StatusViewModel { Model = "m", Mode = StatusBarMode.Running };
        var screen = ChatScreen.Build(composer, status, includeSidebar: false, mascotMode: mode);
        var buffer = new ScreenBuffer(cols, rows);
        screen.Tree.Solve(cols, rows);
        return (screen, buffer);
    }

    private static string Paint(ChatScreen screen, ScreenBuffer buffer, int frames = 1)
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

    [Test]
    public async Task FooterMode_Default_Build_HasNoMascotPanel()
    {
        var composer = new ComposerController();
        var status = new StatusViewModel();
        var screen = ChatScreen.Build(composer, status, includeSidebar: false);

        await Assert.That(screen.Mascot).IsNull();
        await Assert.That(screen.Status.FooterMascotEnabled).IsTrue();
    }

    [Test]
    public async Task PanelMode_TreeContainsMascot_AndFooterSuppressed()
    {
        var (screen, _) = BuildScreen(MascotMode.Panel, 120, 24);

        await Assert.That(screen.Mascot).IsNotNull();
        await Assert.That(screen.Status.FooterMascotEnabled).IsFalse();
        await Assert.That(screen.Mascot!.Rect.Height).IsGreaterThanOrEqualTo(3);
        await Assert.That(screen.Mascot.Rect.Width).IsGreaterThanOrEqualTo(AmbientMascot.PanelMinWidth);
    }

    [Test]
    public async Task PanelMode_PaintsEarsFacePawsRows()
    {
        var (screen, buffer) = BuildScreen(MascotMode.Panel, 120, 24);

        string art = Paint(screen, buffer, 2);
        await Assert.That(art).Contains(AmbientMascot.PanelEars(MascotMood.Working)[1]);
        await Assert.That(art).Contains(AmbientMascot.WorkingFrames[1]);
        await Assert.That(art).Contains(AmbientMascot.PanelPaws(MascotMood.Working)[1]);
    }

    [Test]
    public async Task PanelMode_MoodFollowsPhase()
    {
        var composer = new ComposerController();
        var status = new StatusViewModel { Model = "m", Mode = StatusBarMode.Running, Phase = AgentPhase.ToolCall };
        var screen = ChatScreen.Build(composer, status, includeSidebar: false, mascotMode: MascotMode.Panel);
        var buffer = new ScreenBuffer(120, 24);
        screen.Tree.Solve(120, 24);

        string art = Paint(screen, buffer, 2);
        await Assert.That(art).Contains(AmbientMascot.ToolCallFrames[1]);
        await Assert.That(art).Contains(AmbientMascot.PanelPaws(MascotMood.ToolCall)[1]);
    }

    [Test]
    public async Task PanelMode_StatusRowSpansFullWidth_BelowTheCat()
    {
        var (screen, buffer) = BuildScreen(MascotMode.Panel, 120, 24);

        _ = Paint(screen, buffer);
        await Assert.That(screen.Status.Rect.Width).IsEqualTo(120);
    }

    [Test]
    public async Task PanelMode_Collapses_OnNarrowRows()
    {
        // 19 columns: composer(10) + gap(1) + mascot(9) no longer fit, so the
        // solver sacrifices the low-priority cat while composer and status stay.
        var (screen, buffer) = BuildScreen(MascotMode.Panel, 19, 12);

        string art = Paint(screen, buffer, 2);
        await Assert.That(screen.Mascot!.Rect.Height).IsEqualTo(0);
        await Assert.That(art).DoesNotContain(AmbientMascot.PanelEars(MascotMood.Working)[0]);
        await Assert.That(screen.Status.Rect.Width).IsEqualTo(19);
    }

    [Test]
    public async Task OffMode_Build_DisablesEverything()
    {
        var (screen, buffer) = BuildScreen(MascotMode.Off, 120, 24);

        await Assert.That(screen.Mascot).IsNull();
        await Assert.That(screen.Status.FooterMascotEnabled).IsFalse();

        string art = Paint(screen, buffer, 2);
        foreach (var frame in AmbientMascot.WorkingFrames)
        {
            await Assert.That(art.Contains(frame)).IsFalse();
        }
    }

    [Test]
    public async Task PanelRowBanks_MatchFooterBankLengths_AndStayEightCells()
    {
        foreach (var mood in Enum.GetValues<MascotMood>())
        {
            var face = AmbientMascot.FramesOf(mood);
            var ears = AmbientMascot.PanelEars(mood);
            var paws = AmbientMascot.PanelPaws(mood);

            for (int i = 0; i < face.Length; i++)
            {
                await Assert.That(ears[i].Length).IsEqualTo(8);
                await Assert.That(paws[i].Length).IsEqualTo(8);
                await Assert.That(face[i].Length).IsEqualTo(8);
            }
        }
    }

    [Test]
    public async Task MoodFlip_Crossfades_OnFramesAfterDetection()
    {
        var composer = new ComposerController();
        var status = new StatusViewModel { Model = "m", Mode = StatusBarMode.Running };
        var screen = ChatScreen.Build(composer, status, includeSidebar: false, mascotMode: MascotMode.Panel);
        var buffer = new ScreenBuffer(120, 24);
        screen.Tree.Solve(120, 24);
        var mascot = screen.Mascot!;

        foreach (var panel in screen.Tree.Panels)
        {
            panel.Paint(buffer); // settle Working
        }

        status.Phase = AgentPhase.ToolCall; // flip
        foreach (var panel in screen.Tree.Panels)
        {
            panel.Paint(buffer); // detection frame paints the new mood settled
        }

        Rect face = mascot.Rect;
        int fx = face.X + 3; // inside the 8-cell face art
        int fy = face.Y + 1;

        await Assert.That(buffer.Get(fx, fy).Style == ChatPalette.Dim).IsTrue();

        foreach (var panel in screen.Tree.Panels)
        {
            panel.Paint(buffer); // first frame after detection — mid-crossfade
        }

        await Assert.That(buffer.Get(fx, fy).Style == ChatPalette.Dim).IsFalse();

        for (int i = 0; i < PanelFx.FadeFrames; i++)
        {
            foreach (var panel in screen.Tree.Panels)
            {
                panel.Paint(buffer);
            }
        }

        await Assert.That(buffer.Get(fx, fy).Style == ChatPalette.Dim).IsTrue();
    }

    [Test]
    public async Task PanelPaint_IsAllocationFree()
    {
        var (screen, buffer) = BuildScreen(MascotMode.Panel, 120, 24);
        var mascot = screen.Mascot!;

        // Warmup: every code path the measured loop exercises — steady paints,
        // latch edges, mid-crossfade blends, latch expiry, mood reverts. The
        // first execution of a path pays one-time tiered-JIT allocations that
        // must stay OUT of the measured window (see AllocationBudgetTests).
        Paint(screen, buffer, 20);
        mascot.Vm.Phase = AgentPhase.Succeeded; // edge → latch + flip + blends
        Paint(screen, buffer, MascotDirector.MoodLatchFrames + PanelFx.FadeFrames + 2); // blends + expiry + revert
        mascot.Vm.Phase = AgentPhase.Errored; // edge → Error mood + blends
        Paint(screen, buffer, 20);
        mascot.Vm.Phase = AgentPhase.Auto; // edge out → Working + blends
        Paint(screen, buffer, PanelFx.FadeFrames + 2);

        GC.WaitForPendingFinalizers();

        var before = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 200;
        for (int i = 0; i < iterations; i++)
        {
            mascot.Vm.Phase = i % 40 == 0 ? AgentPhase.Succeeded : AgentPhase.Auto;
            mascot.Paint(buffer);
        }

        var after = GC.GetAllocatedBytesForCurrentThread();

        await Assert.That(after - before).IsEqualTo(0);
    }
}
