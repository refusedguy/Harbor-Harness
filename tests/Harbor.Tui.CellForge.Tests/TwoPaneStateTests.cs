using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #2698 P02: the domain-free two-pane explorer state. Covers the acceptance
/// facts from the catalog doc: deterministic rows for a clip, independent
/// cursors, focus changes without cursor movement, and a transfer reported to
/// the caller without mutating the state.
/// </summary>
public class TwoPaneStateTests
{
    private static PaneItem Item(string id, string label, string detail = "") => new(id, label, detail);

    [Test]
    public async Task EmptyState_HasNoSelectionOrTransfer()
    {
        var state = new TwoPaneState();

        await Assert.That(state.Active).IsEqualTo(PaneId.Source);
        await Assert.That(state.Items(PaneId.Source)).IsEmpty();
        await Assert.That(state.Items(PaneId.Target)).IsEmpty();
        await Assert.That(state.TryGetSelected(PaneId.Source, out _)).IsFalse();
        await Assert.That(state.Transfer().HasValue).IsFalse();

        IReadOnlyList<TwoPaneRow> rows = state.Rows(PaneId.Source, 10, 4);
        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows[0].Text).IsEqualTo("Source (0)");
        await Assert.That(rows[1].Text).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Replace_ClampCursor_LeavesOtherPaneAlone()
    {
        var state = new TwoPaneState();
        PaneItem[] source = [Item("a", "alpha"), Item("b", "beta"), Item("c", "gamma")];
        PaneItem[] target = [Item("t", "target")];

        await Assert.That(state.Replace(PaneId.Source, source)).IsFalse();
        await Assert.That(state.Replace(PaneId.Target, target)).IsFalse();
        await Assert.That(state.SetCursor(PaneId.Source, 2)).IsTrue();
        await Assert.That(state.SetCursor(PaneId.Target, 0)).IsFalse();

        // Shrinking only the source collection pulls only its cursor back.
        PaneItem[] shortened = [Item("a", "alpha")];
        await Assert.That(state.Replace(PaneId.Source, shortened)).IsTrue();
        await Assert.That(state.SourceCursor).IsEqualTo(0);
        await Assert.That(state.TargetCursor).IsEqualTo(0);
        await Assert.That(state.Items(PaneId.Source)).IsEquivalentTo(shortened);
        await Assert.That(state.Items(PaneId.Target)).IsEquivalentTo(target);
    }

    [Test]
    public async Task MoveCursor_TouchesActivePaneOnly()
    {
        var state = new TwoPaneState();
        state.Replace(PaneId.Source, [Item("a", "a"), Item("b", "b"), Item("c", "c")]);
        state.Replace(PaneId.Target, [Item("x", "x"), Item("y", "y")]);

        await Assert.That(state.MoveCursor(1)).IsTrue();
        await Assert.That(state.SourceCursor).IsEqualTo(1);
        await Assert.That(state.TargetCursor).IsEqualTo(0);

        state.SetActive(PaneId.Target);
        await Assert.That(state.MoveCursor(1)).IsTrue();
        await Assert.That(state.SourceCursor).IsEqualTo(1);
        await Assert.That(state.TargetCursor).IsEqualTo(1);

        // Bounds clamp instead of wrapping around; a clamp that lands on the
        // current row is a no-op, not a move.
        await Assert.That(state.MoveCursor(PaneId.Target, 10)).IsFalse();
        await Assert.That(state.TargetCursor).IsEqualTo(1);
        await Assert.That(state.MoveCursor(PaneId.Target, -10)).IsTrue();
        await Assert.That(state.TargetCursor).IsEqualTo(0);
    }

    [Test]
    public async Task SwitchActive_DoesNotMoveEitherCursor()
    {
        var state = new TwoPaneState();
        state.Replace(PaneId.Source, [Item("a", "a"), Item("b", "b")]);
        state.Replace(PaneId.Target, [Item("t", "t")]);
        state.SetCursor(PaneId.Source, 1);

        await Assert.That(state.SwitchActive()).IsTrue();
        await Assert.That(state.Active).IsEqualTo(PaneId.Target);
        await Assert.That(state.SourceCursor).IsEqualTo(1);
        await Assert.That(state.TargetCursor).IsEqualTo(0);

        await Assert.That(state.SetActive(PaneId.Target)).IsFalse();
        await Assert.That(state.SwitchActive()).IsTrue();
        await Assert.That(state.Active).IsEqualTo(PaneId.Source);
        await Assert.That(state.SourceCursor).IsEqualTo(1);
        await Assert.That(state.TargetCursor).IsEqualTo(0);
    }

    [Test]
    public async Task Transfer_ForwardAndReverse_ReportWithoutMutation()
    {
        var state = new TwoPaneState();
        state.Replace(PaneId.Source, [Item("a", "alpha", "preview a")]);
        state.Replace(PaneId.Target, [Item("t", "target", "preview t")]);

        TwoPaneTransfer? forward = state.Transfer();
        await Assert.That(forward.HasValue).IsTrue();
        TwoPaneTransfer forwardValue = forward!.Value;
        await Assert.That(forwardValue.From).IsEqualTo(PaneId.Source);
        await Assert.That(forwardValue.To).IsEqualTo(PaneId.Target);
        await Assert.That(forwardValue.Item.Id).IsEqualTo("a");

        TwoPaneTransfer? reverse = state.Transfer(reverse: true);
        await Assert.That(reverse.HasValue).IsTrue();
        TwoPaneTransfer reverseValue = reverse!.Value;
        await Assert.That(reverseValue.From).IsEqualTo(PaneId.Target);
        await Assert.That(reverseValue.To).IsEqualTo(PaneId.Source);
        await Assert.That(reverseValue.Item.Id).IsEqualTo("t");

        // Reporting is read-only: no pane change, no cursor change, no rebuild.
        await Assert.That(state.Active).IsEqualTo(PaneId.Source);
        await Assert.That(state.SourceCursor).IsEqualTo(0);
        await Assert.That(state.TargetCursor).IsEqualTo(0);
        await Assert.That(state.Items(PaneId.Source).Count).IsEqualTo(1);
        await Assert.That(state.Items(PaneId.Target).Count).IsEqualTo(1);
    }

    [Test]
    public async Task Rows_AreDeterministicAndPreviewFollowsCursor()
    {
        var state = new TwoPaneState();
        state.Replace(PaneId.Source, [Item("a", "alpha", "first"), Item("b", "beta", "second")]);
        state.Replace(PaneId.Target, [Item("t", "target", "detail")]);

        IReadOnlyList<TwoPaneRow> first = state.Rows(PaneId.Source, 12, 4);
        IReadOnlyList<TwoPaneRow> second = state.Rows(PaneId.Source, 12, 4);
        await Assert.That(first.Count).IsEqualTo(4);
        for (int i = 0; i < first.Count; i++)
        {
            await Assert.That(second[i].Text).IsEqualTo(first[i].Text);
            await Assert.That(second[i].IsCursor).IsEqualTo(first[i].IsCursor);
            await Assert.That(second[i].IsActive).IsEqualTo(first[i].IsActive);
        }

        await Assert.That(first[0].Text).IsEqualTo("Source (2)");
        await Assert.That(first[0].IsActive).IsTrue();
        await Assert.That(first[1].Text).IsEqualTo("> alpha");
        await Assert.That(first[1].IsCursor).IsTrue();
        await Assert.That(first[2].Text).IsEqualTo("  beta");
        await Assert.That(first[3].Text).IsEqualTo("first");

        state.MoveCursor(PaneId.Source, 1);
        IReadOnlyList<TwoPaneRow> moved = state.Rows(PaneId.Source, 12, 4);
        await Assert.That(moved[1].Text).IsEqualTo("  alpha");
        await Assert.That(moved[2].Text).IsEqualTo("> beta");
        await Assert.That(moved[3].Text).IsEqualTo("second");

        state.SetActive(PaneId.Target);
        IReadOnlyList<TwoPaneRow> targetRows = state.Rows(PaneId.Target, 12, 4);
        await Assert.That(targetRows[0].Text).IsEqualTo("Target (1)");
        await Assert.That(targetRows[0].IsActive).IsTrue();
        await Assert.That(state.Rows(PaneId.Source, 12, 4)[0].IsActive).IsFalse();
    }

    [Test]
    public async Task Rows_ClampWindowAndTruncateToCellWidth()
    {
        var state = new TwoPaneState();
        state.Replace(
            PaneId.Source,
            [Item("0", "zero"), Item("1", "one"), Item("2", "two"), Item("3", "three"), Item("4", "four"), Item("5", "five")]);
        state.SetCursor(PaneId.Source, 4);

        IReadOnlyList<TwoPaneRow> rows = state.Rows(PaneId.Source, 12, 4);
        await Assert.That(rows.Count).IsEqualTo(4);
        await Assert.That(rows[1].Text).IsEqualTo("  three");
        await Assert.That(rows[2].Text).IsEqualTo("> four");

        IReadOnlyList<TwoPaneRow> narrow = state.Rows(PaneId.Source, 5, 4);
        await Assert.That(narrow[2].Text).IsEqualTo("> fo…");

        state.Replace(PaneId.Source, [Item("wide", "漢漢漢漢", "preview")]);
        IReadOnlyList<TwoPaneRow> wide = state.Rows(PaneId.Source, 5, 3);
        await Assert.That(wide[1].Text).IsEqualTo("> 漢…");
    }

    [Test]
    public async Task Paint_PlacesBothPanesAndMarksCursor()
    {
        var state = new TwoPaneState();
        state.Replace(PaneId.Source, [Item("a", "alpha", "first preview")]);
        state.Replace(PaneId.Target, [Item("b", "beta", "second preview")]);

        // The test project pins the unqualified rendering names to the shared
        // UI namespace, so the engine types are qualified here (as in the
        // neighbouring BreadcrumbTrailTests).
        var buffer = new EngineCells.ScreenBuffer(21, 4);
        state.Paint(
            buffer,
            new EngineCells.Rect(0, 0, 21, 4),
            new EngineCells.CellStyle(attrs: EngineCells.StyleAttr.Bold));

        await Assert.That(buffer.Get(0, 0).Rune).IsEqualTo('S');
        await Assert.That(buffer.Get(0, 0).Flags).IsEqualTo((ushort)EngineCells.StyleAttr.Bold);
        await Assert.That(buffer.Get(11, 0).Rune).IsEqualTo('T');
        await Assert.That(buffer.Get(0, 1).Rune).IsEqualTo('>');
        await Assert.That(buffer.Get(0, 1).Flags).IsEqualTo((ushort)EngineCells.StyleAttr.Reverse);
        await Assert.That(buffer.Get(13, 1).Rune).IsEqualTo('b');

        // A clip too narrow for two panes is a no-op, not a half-painted frame.
        var narrow = new EngineCells.ScreenBuffer(1, 4);
        state.Paint(narrow, new EngineCells.Rect(0, 0, 1, 4));
        await Assert.That(narrow.Get(0, 0).Rune).IsEqualTo(' ');
    }
}
