using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// ENG12 #284 (TGui snapshot pattern): draw paths snapshot collections before
/// iterating so an event-thread mutation mid-draw cannot invalidate the pass.
/// These pins guard behavior preservation — identical painted output and
/// no-throw paint after structural changes (dismiss / further appends).
/// </summary>
public class Eng12SnapshotTests
{
    private sealed class FixedBlock : IChatBlock
    {
        public FixedBlock(string text) => Text = text;
        public string Text { get; }
        public string Kind => "fixed";
        public bool IsStreamContinuation => false;
        public int BudgetBytes => 64;
        public BlockMeasure Measure(int width) => BlockMeasure.Exact(1);
        public int CheapEstimate(int width) => 1;

        public void Paint(in BlockPaintContext ctx)
        {
            ctx.Buffer.SetText(ctx.Rect.X, ctx.Rect.Y, Text, CellStyle.Plain);
        }

        public string RawText() => Text;
    }

    [Test]
    public async Task TimelinePaint_PaintsSnapshotWindow_UnchangedOutput()
    {
        var buffer = new ScreenBuffer(40, 12);
        var tl = new VirtualizedChatTimeline();
        tl.Append(new FixedBlock("alpha"));
        tl.Append(new FixedBlock("beta"));
        tl.Append(new FixedBlock("gamma"));
        _ = tl.PrepareFrame(40, 4);

        tl.Paint(buffer, new Rect(0, 0, 40, 4));

        string art = GridDump.Art(buffer);
        await Assert.That(art.Contains("alpha")).IsTrue();
        await Assert.That(art.Contains("beta")).IsTrue();
        await Assert.That(art.Contains("gamma")).IsTrue();
    }

    [Test]
    public async Task ToastPaint_AfterDismiss_PaintsSurvivor()
    {
        var buffer = new ScreenBuffer(40, 12);
        var overlay = new ToastOverlay();
        overlay.Show("first toast");
        overlay.Show("second toast");

        // First pass drains the pending queue; second pass paints.
        overlay.Paint(buffer, new Rect(0, 0, 40, 6));
        overlay.Paint(buffer, new Rect(0, 0, 40, 6));

        string art = GridDump.Art(buffer);
        await Assert.That(art.Contains("first toast")).IsTrue();
        await Assert.That(art.Contains("second toast")).IsTrue();

        _ = overlay.Dismiss(overlay.Active[0].Id);
        overlay.Paint(buffer, new Rect(0, 0, 40, 6));

        string after = GridDump.Art(buffer);
        await Assert.That(after.Contains("second toast")).IsTrue();
        await Assert.That(overlay.Active.Count).IsEqualTo(1);
    }
}
