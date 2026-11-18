using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #2706 P05: the domain-free navigable change-blocks state. Covers the
/// catalog contract: deterministic rows for a clip, independent per-side
/// navigation, a normalized range selection, and a transfer reported to the
/// caller without mutating the state.
/// </summary>
public class NavigableChangeBlocksTests
{
    private static ChangeLine Line(int number, string text) => new(number, text);

    private static ChangeBlock Block(string id, string label, params ChangeLine[] lines) =>
        new(id, label, lines);

    [Test]
    public async Task EmptyState_HasNoBlockOrSelection()
    {
        var state = new NavigableChangeBlocks();

        await Assert.That(state.Active).IsEqualTo(BlockSide.Left);
        await Assert.That(state.Blocks).IsEmpty();
        await Assert.That(state.TryGetBlock(out _)).IsFalse();
        await Assert.That(state.SelectedRange().IsEmpty).IsTrue();
        await Assert.That(state.Transfer().HasValue).IsFalse();

        IReadOnlyList<ChangeRow> rows = state.Rows(BlockSide.Left, 10, 4);
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Text).IsEqualTo("Left (0)");
        await Assert.That(rows[0].IsActive).IsTrue();
    }

    [Test]
    public async Task Replace_ClampsBothSidesAndDropsStaleAnchor()
    {
        var state = new NavigableChangeBlocks();
        ChangeBlock[] blocks =
        [
            Block("a", "first", Line(1, "one"), Line(2, "two"), Line(3, "three"), Line(4, "four")),
            Block("b", "second", Line(10, "ten"), Line(11, "eleven")),
        ];
        state.Replace(blocks);

        await Assert.That(state.MoveBlock(BlockSide.Left, 1)).IsTrue();
        await Assert.That(state.MoveLine(BlockSide.Left, 1)).IsTrue();
        state.SetAnchor(BlockSide.Left);
        await Assert.That(state.Anchor(BlockSide.Left)).IsEqualTo(1);

        await Assert.That(state.MoveBlock(BlockSide.Right, 1)).IsTrue();
        await Assert.That(state.BlockIndex(BlockSide.Right)).IsEqualTo(1);

        // Shrinking to one short block pulls both block indices back, clamps
        // line cursors and drops the anchor whose block no longer has that line.
        ChangeBlock[] shortened = [Block("a", "first", Line(1, "one"))];
        await Assert.That(state.Replace(shortened)).IsTrue();
        await Assert.That(state.BlockIndex(BlockSide.Left)).IsEqualTo(0);
        await Assert.That(state.LineCursor(BlockSide.Left)).IsEqualTo(0);
        await Assert.That(state.Anchor(BlockSide.Left)).IsNull();
        await Assert.That(state.BlockIndex(BlockSide.Right)).IsEqualTo(0);
        await Assert.That(state.LineCursor(BlockSide.Right)).IsEqualTo(0);

        // A replacement that changes nothing is a no-op.
        await Assert.That(state.Replace(shortened)).IsFalse();
    }

    [Test]
    public async Task MoveBlock_ClearsAnchorAndLeavesOtherSideAlone()
    {
        var state = new NavigableChangeBlocks();
        state.Replace(
        [
            Block("a", "first", Line(1, "one"), Line(2, "two")),
            Block("b", "second", Line(3, "three")),
        ]);
        state.MoveLine(BlockSide.Left, 1);
        state.SetAnchor(BlockSide.Left);

        await Assert.That(state.MoveBlock(BlockSide.Left, 1)).IsTrue();
        await Assert.That(state.BlockIndex(BlockSide.Left)).IsEqualTo(1);
        await Assert.That(state.LineCursor(BlockSide.Left)).IsEqualTo(0);
        await Assert.That(state.Anchor(BlockSide.Left)).IsNull();
        await Assert.That(state.BlockIndex(BlockSide.Right)).IsEqualTo(0);

        // Bounds clamp rather than wrap; a clamp that lands on the current
        // block is a no-op.
        await Assert.That(state.MoveBlock(BlockSide.Left, 10)).IsFalse();
        await Assert.That(state.MoveBlock(BlockSide.Left, -10)).IsTrue();
        await Assert.That(state.BlockIndex(BlockSide.Left)).IsEqualTo(0);
    }

    [Test]
    public async Task MoveLine_ClampsAndTouchesActiveSideOnly()
    {
        var state = new NavigableChangeBlocks();
        state.Replace([Block("a", "first", Line(1, "one"), Line(2, "two"), Line(3, "three"))]);

        await Assert.That(state.MoveLine(1)).IsTrue();
        await Assert.That(state.LineCursor(BlockSide.Left)).IsEqualTo(1);
        await Assert.That(state.LineCursor(BlockSide.Right)).IsEqualTo(0);

        await Assert.That(state.SwitchActive()).IsTrue();
        await Assert.That(state.MoveLine(1)).IsTrue();
        await Assert.That(state.LineCursor(BlockSide.Left)).IsEqualTo(1);
        await Assert.That(state.LineCursor(BlockSide.Right)).IsEqualTo(1);

        await Assert.That(state.MoveLine(BlockSide.Right, 10)).IsTrue();
        await Assert.That(state.LineCursor(BlockSide.Right)).IsEqualTo(2);
        await Assert.That(state.MoveLine(BlockSide.Right, 10)).IsFalse();
        await Assert.That(state.MoveLine(BlockSide.Right, -10)).IsTrue();
        await Assert.That(state.LineCursor(BlockSide.Right)).IsEqualTo(0);
    }

    [Test]
    public async Task SwitchActive_DoesNotMoveCursorsOrSelections()
    {
        var state = new NavigableChangeBlocks();
        state.Replace([Block("a", "first", Line(1, "one"), Line(2, "two"), Line(3, "three"))]);
        state.MoveLine(BlockSide.Left, 2);
        state.SetAnchor(BlockSide.Left);
        state.MoveLine(BlockSide.Left, -1);

        await Assert.That(state.SwitchActive()).IsTrue();
        await Assert.That(state.Active).IsEqualTo(BlockSide.Right);
        await Assert.That(state.LineCursor(BlockSide.Left)).IsEqualTo(1);
        await Assert.That(state.LineCursor(BlockSide.Right)).IsEqualTo(0);
        await Assert.That(state.Anchor(BlockSide.Left)).IsEqualTo(2);
        await Assert.That(state.Anchor(BlockSide.Right)).IsNull();

        await Assert.That(state.SetActive(BlockSide.Right)).IsFalse();
        await Assert.That(state.SwitchActive()).IsTrue();
        await Assert.That(state.Active).IsEqualTo(BlockSide.Left);
    }

    [Test]
    public async Task Anchor_SelectionRangeNormalizesLowToHigh()
    {
        var state = new NavigableChangeBlocks();
        state.Replace([Block("a", "first", Line(1, "one"), Line(2, "two"), Line(3, "three"))]);

        // Without an anchor the selection is the single cursor line.
        state.MoveLine(2);
        BlockLineRange single = state.SelectedRange();
        await Assert.That(single.Start).IsEqualTo(2);
        await Assert.That(single.End).IsEqualTo(3);
        await Assert.That(single.Length).IsEqualTo(1);
        await Assert.That(single.Contains(2)).IsTrue();
        await Assert.That(single.Contains(1)).IsFalse();

        // Anchoring at line 2 and moving up to line 0 yields [0, 3).
        state.SetAnchor();
        state.MoveLine(-2);
        BlockLineRange range = state.SelectedRange();
        await Assert.That(range.Start).IsEqualTo(0);
        await Assert.That(range.End).IsEqualTo(3);
        await Assert.That(range.Length).IsEqualTo(3);

        await Assert.That(state.ClearAnchor()).IsTrue();
        await Assert.That(state.ClearAnchor()).IsFalse();
        // The anchor is gone and the cursor sits at line 0, so the selection
        // collapses back to that single line, not to where the anchor was set.
        await Assert.That(state.SelectedRange()).IsEqualTo(new BlockLineRange(0, 1));
    }

    [Test]
    public async Task Transfer_ForwardAndReverseReportWithoutMutation()
    {
        var state = new NavigableChangeBlocks();
        state.Replace(
        [
            Block("a", "first", Line(1, "one"), Line(2, "two"), Line(3, "three")),
            Block("b", "second", Line(10, "ten"), Line(11, "eleven")),
        ]);
        state.MoveLine(BlockSide.Left, 1);
        state.SetAnchor(BlockSide.Left);
        state.MoveLine(BlockSide.Left, 2);
        state.MoveBlock(BlockSide.Right, 1);

        BlockTransfer? forward = state.Transfer();
        await Assert.That(forward.HasValue).IsTrue();
        BlockTransfer value = forward!.Value;
        await Assert.That(value.BlockId).IsEqualTo("a");
        await Assert.That(value.Range).IsEqualTo(new BlockLineRange(1, 3));
        await Assert.That(value.From).IsEqualTo(BlockSide.Left);
        await Assert.That(value.To).IsEqualTo(BlockSide.Right);

        BlockTransfer? reverse = state.Transfer(reverse: true);
        await Assert.That(reverse.HasValue).IsTrue();
        BlockTransfer reverseValue = reverse!.Value;
        await Assert.That(reverseValue.BlockId).IsEqualTo("b");
        await Assert.That(reverseValue.From).IsEqualTo(BlockSide.Right);
        await Assert.That(reverseValue.To).IsEqualTo(BlockSide.Left);

        // Reporting is read-only: no side, block, cursor, anchor or collection change.
        await Assert.That(state.Active).IsEqualTo(BlockSide.Left);
        await Assert.That(state.BlockIndex(BlockSide.Left)).IsEqualTo(0);
        await Assert.That(state.LineCursor(BlockSide.Left)).IsEqualTo(2);
        await Assert.That(state.Anchor(BlockSide.Left)).IsEqualTo(1);
        await Assert.That(state.BlockIndex(BlockSide.Right)).IsEqualTo(1);
        await Assert.That(state.Blocks.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Rows_AreDeterministicAndMarkCursorAndSelection()
    {
        var state = new NavigableChangeBlocks();
        state.Replace([Block("a", "first", Line(1, "one"), Line(2, "two"), Line(3, "three"))]);
        state.MoveLine(1);
        state.SetAnchor();
        state.MoveLine(2);

        IReadOnlyList<ChangeRow> first = state.Rows(BlockSide.Left, 14, 6);
        IReadOnlyList<ChangeRow> second = state.Rows(BlockSide.Left, 14, 6);
        await Assert.That(first.Count).IsEqualTo(5);
        for (int i = 0; i < first.Count; i++)
        {
            await Assert.That(second[i].Text).IsEqualTo(first[i].Text);
            await Assert.That(second[i].IsCursor).IsEqualTo(first[i].IsCursor);
            await Assert.That(second[i].IsSelected).IsEqualTo(first[i].IsSelected);
            await Assert.That(second[i].IsActive).IsEqualTo(first[i].IsActive);
        }

        await Assert.That(first[0].Text).IsEqualTo("Left (1)");
        await Assert.That(first[0].IsActive).IsTrue();
        await Assert.That(first[1].Text).IsEqualTo("first");
        await Assert.That(first[2].Text).IsEqualTo("  1 one");
        await Assert.That(first[3].Text).IsEqualTo("* 2 two");
        await Assert.That(first[3].IsSelected).IsTrue();
        await Assert.That(first[4].Text).IsEqualTo("> 3 three");
        await Assert.That(first[4].IsCursor).IsTrue();
        await Assert.That(first[4].IsSelected).IsTrue();

        await Assert.That(state.Rows(BlockSide.Right, 14, 6)[0].IsActive).IsFalse();
    }

    [Test]
    public async Task Rows_ClampWindowAndTruncateToCellWidth()
    {
        ChangeLine[] lines =
        [
            Line(1, "zero"), Line(2, "one"), Line(3, "two"),
            Line(4, "three"), Line(5, "four"), Line(6, "five"),
        ];
        var state = new NavigableChangeBlocks();
        state.Replace([Block("a", "first", lines)]);
        state.MoveLine(4);

        IReadOnlyList<ChangeRow> rows = state.Rows(BlockSide.Left, 14, 5);
        await Assert.That(rows.Count).IsEqualTo(5);
        await Assert.That(rows[2].Text).IsEqualTo("  4 three");
        await Assert.That(rows[3].Text).IsEqualTo("> 5 four");

        IReadOnlyList<ChangeRow> narrow = state.Rows(BlockSide.Left, 6, 5);
        await Assert.That(narrow[3].Text).IsEqualTo("> 5 f…");

        state.Replace([Block("a", "first", Line(1, "漢漢漢漢"))]);
        IReadOnlyList<ChangeRow> wide = state.Rows(BlockSide.Left, 6, 4);
        await Assert.That(wide[2].Text).IsEqualTo("> 1 …");
    }

    [Test]
    public async Task Paint_PlacesBothSidesAndMarksCursorAndSelection()
    {
        var state = new NavigableChangeBlocks();
        state.Replace(
        [
            Block("a", "first", Line(1, "one"), Line(2, "two")),
            Block("b", "second", Line(3, "three")),
        ]);
        state.MoveLine(BlockSide.Left, 1);
        state.MoveBlock(BlockSide.Right, 1);

        // The test project pins the unqualified rendering names to the shared
        // UI namespace, so engine grid types are qualified through EngineCells
        // (as in the neighbouring TwoPaneStateTests).
        var buffer = new EngineCells.ScreenBuffer(21, 5);
        state.Paint(
            buffer,
            new EngineCells.Rect(0, 0, 21, 5),
            new EngineCells.CellStyle(attrs: EngineCells.StyleAttr.Bold));

        await Assert.That(buffer.Get(0, 0).Rune).IsEqualTo('L');
        await Assert.That(buffer.Get(0, 0).Flags).IsEqualTo((ushort)EngineCells.StyleAttr.Bold);
        await Assert.That(buffer.Get(11, 0).Rune).IsEqualTo('R');
        await Assert.That(buffer.Get(0, 3).Rune).IsEqualTo('>');
        await Assert.That(buffer.Get(0, 3).Flags).IsEqualTo((ushort)EngineCells.StyleAttr.Reverse);
        await Assert.That(buffer.Get(11, 2).Rune).IsEqualTo('>');
        await Assert.That(buffer.Get(13, 2).Rune).IsEqualTo('3');

        // A clip too narrow for two sides is a no-op, not a half-painted frame.
        var narrow = new EngineCells.ScreenBuffer(1, 5);
        state.Paint(narrow, new EngineCells.Rect(0, 0, 1, 5));
        await Assert.That(narrow.Get(0, 0).Rune).IsEqualTo(' ');
    }
}
