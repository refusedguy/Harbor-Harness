using Harbor.Ui.Framework.State;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// CF-E-011: the tool-card diff budget — <c>DiffRenderer.MaxPreviewLines</c> and
/// its <c>"… diff truncated"</c> overflow row, read here from
/// <see cref="DiffPreview" />, the one producer of the block the card renders.
/// </summary>
/// <remarks>
/// These tests used to live in a file named for a second
/// <c>Harbor.Tui.CellForge.Rendering.DiffPreview</c> — a third copy of the same
/// index-alignment walk as the State one, in this assembly, with no product
/// caller (#570). Three of them painted a real card, so they were always about
/// <c>ToolCallBlock</c> and the copy was incidental; they moved here when the
/// copy went, rather than going with it.
/// </remarks>
// #58: serialized vs theme tests — rendering reads global TerminalColorPalette.
[NotInParallel("pty")]
public class ToolCallBlockDiffCapTests
{
    private static string PaintCard(string diffText, out int measuredLines, int width = 40)
    {
        var block = new ToolCallBlock(new ToolCallInfo("t1", "edit", "big.cs"));
        block.Complete(new ToolResultBody("ok", isError: false, TimeSpan.FromMilliseconds(3), diffText));
        measuredLines = block.Measure(width).MinLines;
        var buffer = new ScreenBuffer(width, measuredLines);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, width, measuredLines), 0));
        return GridDump.Art(buffer);
    }

    /// <summary>
    /// An unbounded foreign diff cannot blow the card's line budget: the overflow row
    /// is emitted and the tail is dropped.
    /// </summary>
    [Test]
    public async Task DiffRenderer_Caps_Long_Diff_At_Six_Plus_Overflow()
    {
        var lines = new string[10];
        for (int i = 0; i < 10; i++)
        {
            lines[i] = "- old" + i;
        }

        string art = PaintCard(string.Join("\n", lines), out int measured);

        await Assert.That(measured).IsEqualTo(1 + DiffPreview.MaxPreviewLines + 1);
        await Assert.That(art).Contains("- old0");
        await Assert.That(art).Contains(DiffPreview.DiffTruncatedSentinel);
        await Assert.That(art.Contains("- old6")).IsFalse();
    }

    /// <summary>A diff inside the budget carries no overflow marker — the cap is not a floor.</summary>
    [Test]
    public async Task DiffRenderer_Short_Diff_Has_No_Overflow_Marker()
    {
        string art = PaintCard("- gone\n+ fresh\n  ctx", out int measured);

        await Assert.That(measured).IsEqualTo(1 + 3);
        await Assert.That(art).Contains("+ fresh");
        await Assert.That(art.Contains(DiffPreview.DiffTruncatedSentinel)).IsFalse();
    }

    /// <summary>
    /// The card's two constants are the State's, so the budget and the marker a card
    /// paints are the ones the producer of its diff text already agreed on.
    /// </summary>
    [Test]
    public async Task CardBudget_IsTheProducersBudget()
    {
        var card = new ToolCallBlock(new ToolCallInfo("t1", "edit", "big.cs"));

        await Assert.That(DiffPreview.MaxPreviewLines).IsEqualTo(6)
            .Because("the visible inline preview is six lines and one overflow row, and this is the "
                   + "budget the tool card's DiffRenderer is compiled against; a silent change here "
                   + "would move the card's height without moving anything the user reads");
        await Assert.That(card.HasDiffText).IsFalse()
            .Because("a card built from ToolCallInfo alone carries no result body yet, so the diff "
                   + "surface is empty rather than absent — the guard is about the cap, and this "
                   + "asserts the card it is measured on is the real one");
    }
}