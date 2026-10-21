using Harbor.Tui.CellForge.Generic.Widgets;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Rendering reads global TerminalColorPalette — serialized vs theme tests.
[NotInParallel("pty")]
public class TableBlockNavStateTests
{
    private static readonly string[] Sample =
    [
        "| name | age |",
        "| --- | ---: |",
        "| ada | 36 |",
        "| grace | 85 |",
    ];

    private static TableBlock ParseSample()
    {
        bool ok = TableBlock.TryParse(Sample, 0, out var block, out int next);
        if (!ok || block is null || next != Sample.Length)
        {
            throw new InvalidOperationException("Sample must parse.");
        }

        return block;
    }

    private static ScreenBuffer PaintWith(TableBlock block, GenericNavState state, int width = 40)
    {
        var buffer = TestRender.Buffer(width, block.Measure(width).MinLines);
        block.Paint(TestRender.Ctx(buffer), state);
        return buffer;
    }

    private static bool IsReversed(ScreenBuffer buffer, int x, int y) =>
        buffer.Get(x, y).Style.Attrs.HasFlag(StyleAttr.Reverse);

    [Test]
    public async Task Block_RowState_MirrorsBodyRows()
    {
        var block = ParseSample();
        await Assert.That(block.BodyRows).IsEqualTo(2);
        await Assert.That(block.RowState.Count).IsEqualTo(2);
        await Assert.That(block.RowState.Selected).IsEqualTo(0);
    }

    [Test]
    public async Task Block_ExternalState_DrivesHighlight()
    {
        var block = ParseSample();
        block.RowState.MoveTo(1);
        var buffer = PaintWith(block, block.RowState);

        // Body rows start at y=3 (top rule, header, mid rule); text at x=2.
        await Assert.That(IsReversed(buffer, 2, 3)).IsFalse();
        await Assert.That(IsReversed(buffer, 2, 4)).IsTrue();
    }

    [Test]
    public async Task Block_SharedState_DrivesTwoBlocks()
    {
        // The #1158 contract: state is external, so one state object can drive
        // two blocks and both paint the same selection.
        var left = ParseSample();
        var right = ParseSample();
        left.RowState.MoveTo(1);

        var buffer = PaintWith(right, left.RowState);
        await Assert.That(IsReversed(buffer, 2, 4)).IsTrue();
        await Assert.That(IsReversed(buffer, 2, 3)).IsFalse();
    }

    [Test]
    public async Task Block_MismatchedState_FallsBackToSelectedRow()
    {
        var block = ParseSample();
        block.SelectRow(0);
        var foreign = new GenericNavState();
        foreign.SetContent(99);
        foreign.MoveTo(50);

        var buffer = PaintWith(block, foreign);
        await Assert.That(IsReversed(buffer, 2, 3)).IsTrue();
        await Assert.That(IsReversed(buffer, 2, 4)).IsFalse();
    }

    [Test]
    public async Task Block_LegacyPaint_NoSelectionByDefault()
    {
        var block = ParseSample();
        var buffer = ScreenFixtures.PaintToBuffer(block);
        await Assert.That(IsReversed(buffer, 2, 3)).IsFalse();
        await Assert.That(IsReversed(buffer, 2, 4)).IsFalse();
    }

    [Test]
    public async Task Block_LegacyPaint_IgnoresRowState()
    {
        // RowState defaults to the first row, but the legacy path only reads
        // the widget-local selection — display-only hosts stay byte-identical.
        var block = ParseSample();
        var buffer = ScreenFixtures.PaintToBuffer(block);
        await Assert.That(IsReversed(buffer, 2, 3)).IsFalse();
        await Assert.That(IsReversed(buffer, 2, 4)).IsFalse();
    }
}
