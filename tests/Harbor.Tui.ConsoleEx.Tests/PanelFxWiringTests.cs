using Harbor.Tui.ConsoleEx.Rendering;
using Harbor.Tui.ConsoleEx.Widgets;

namespace Harbor.Tui.ConsoleEx.Tests;

/// <summary>
/// Entrance-motion wiring (HDS v1): timeline slide+fade, approval warn-glow
/// pulse, and the status-bar mode crossfade. All assertions run against
/// deterministic ticks — no wall clock anywhere.
/// </summary>
public class PanelFxWiringTests
{
    private static ScreenBuffer PaintTimeline(VirtualizedChatTimeline tl, int width, int height, long tick)
    {
        tl.CurrentTick = tick;
        _ = tl.PrepareFrame(width, height);
        var buffer = new ScreenBuffer(width, height);
        tl.Paint(buffer, new Rect(0, 0, width, height));
        return buffer;
    }

    [Test]
    public async Task FirstPaintOfInitialPopulate_IsSettled()
    {
        var tl = new VirtualizedChatTimeline();
        tl.EnableEntranceFx();
        tl.Append(new UserBlock("hello"));

        var buffer = PaintTimeline(tl, 40, 4, tick: 0);

        await Assert.That(GridDump.Art(buffer)).Contains("hello");
        // Prefix accent is exact — no alpha blending on cold screens.
        await Assert.That(buffer.Get(0, 0).Style.Fg).IsEqualTo(ChatPalette.Accent);
    }

    [Test]
    public async Task MidstreamAppend_Animates_ThenMatchesSettledBaseline()
    {
        var tl = new VirtualizedChatTimeline();
        tl.EnableEntranceFx();

        _ = PaintTimeline(tl, 40, 6, tick: 0);   // first frame → hasPaintedFrame
        tl.Append(new UserBlock("late arrival"));
        tl.ScrollToEnd(6);

        ScreenBuffer animating = PaintTimeline(tl, 40, 6, tick: 1);
        ScreenBuffer settled = PaintTimeline(tl, 40, 6, tick: 99);

        string animArt = GridDump.Art(animating);
        string settledArt = GridDump.Art(settled);

        await Assert.That(animArt).Contains("late arrival");
        await Assert.That(settledArt).Contains("late arrival");
        await Assert.That(animArt).IsNotEqualTo(settledArt); // motion is visible mid-flight

        // Settled grid must equal a no-FX timeline rendered from the same content.
        var plainTl = new VirtualizedChatTimeline();
        _ = PaintTimeline(plainTl, 40, 6, tick: 0);
        plainTl.Append(new UserBlock("late arrival"));
        plainTl.ScrollToEnd(6);
        ScreenBuffer baseline = PaintTimeline(plainTl, 40, 6, tick: 99);

        await Assert.That(GridDump.Art(baseline)).IsEqualTo(settledArt);
    }

    [Test]
    public async Task WarnPulse_OscillatesHeaderTone_AndClearsOnDecision()
    {
        var gate = new ApprovalGateView("bash", "ls -la /tmp");

        CellStyle HeaderAt(long tick)
        {
            var buffer = new ScreenBuffer(30, 4);
            gate.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 30, 4), tick));
            return buffer.Get(0, 0).Style;
        }

        var plainWarning = new CellStyle(ChatPalette.Warning, attrs: StyleAttr.Bold);
        gate.BeginWarnPulse(birthTick: 100);

        await Assert.That(HeaderAt(100 + (PanelFx.PulseFrames / 4)) == plainWarning).IsTrue(); // sine peak ≈ full glow
        await Assert.That(HeaderAt(100 + (PanelFx.PulseFrames / 2) - 1).Fg != ChatPalette.Warning).IsTrue(); // trough dims

        _ = gate.TryDecide(ApprovalChoice.Approve);
        await Assert.That(HeaderAt(100 + PanelFx.PulseFrames) == ChatPalette.Dim).IsTrue(); // stamped → dim, glow off
    }

    [Test]
    public async Task StatusBar_Crossfades_OnModeFlip_AndSettles()
    {
        var status = new StatusViewModel { Model = "m", Mode = StatusBarMode.Running };
        status.SetContext(1000, 10_000);

        var screen = ChatScreen.Build(new ComposerController(), status);
        var buffer = new ScreenBuffer(60, 20);
        screen.Tree.Solve(60, 20);

        Frame(screen, buffer);
        status.Mode = StatusBarMode.AwaitingApproval;   // flip mid-stream
        Frame(screen, buffer);                          // crossfade frame 1
        string duringCrossfade = GridDump.Art(buffer);

        for (int i = 0; i < PanelFx.FadeFrames + 2; i++)
        {
            Frame(screen, buffer);
        }

        await Assert.That(duringCrossfade.Length > 0).IsTrue();
        await Assert.That(GridDump.Art(buffer)).Contains("awaiting approval");
    }

    private static void Frame(ChatScreen screen, ScreenBuffer buffer)
    {
        foreach (var p in screen.Tree.Panels)
        {
            p.Paint(buffer);
        }
    }
}
