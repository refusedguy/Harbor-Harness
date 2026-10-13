using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// N-ary Row/Column stacks on the layout tree (#1211): a leaf wraps into a
/// stack container (anchor kept first), solved by seating
/// <see cref="FlexLayout.Arrange"/> — the solver exists, ownership was
/// missing. Layout math only — no painting, no goldens touched.
/// </summary>
public class LayoutTreeRowColumnTests
{
    private static BorderPanel P(string id) => new(id, minWidth: 2, minHeight: 2);

    [Test]
    public async Task AddRow_ThreePanels_TilesWidthWithGap()
    {
        var tree = new LayoutTree();
        var a = P("a");
        var b = P("b");
        var c = P("c");
        tree.AddRoot(a);
        tree.AddRow("a", [b, c]);

        tree.Solve(30, 10);

        // Content 30-2 gaps = 28; three equal Min(2) shares absorb dust last.
        await Assert.That(a.Rect).IsEqualTo(new Rect(0, 0, 9, 10));
        await Assert.That(b.Rect).IsEqualTo(new Rect(10, 0, 9, 10));
        await Assert.That(c.Rect).IsEqualTo(new Rect(20, 0, 10, 10));
    }

    [Test]
    public async Task AddStack_ExplicitTracks_FixedPlusFill()
    {
        var tree = new LayoutTree();
        var a = P("a");
        var b = P("b");
        var c = P("c");
        tree.AddRoot(a);
        tree.AddStack("a", SplitDir.Horizontal, [b, c],
            [FlexTrack.Fixed(10), FlexTrack.Fill(1), FlexTrack.Fill(1)]);

        tree.Solve(30, 10);

        await Assert.That(a.Rect).IsEqualTo(new Rect(0, 0, 10, 10));
        await Assert.That(b.Rect).IsEqualTo(new Rect(11, 0, 9, 10));
        await Assert.That(c.Rect).IsEqualTo(new Rect(21, 0, 9, 10));
    }

    [Test]
    public async Task AddColumn_ThreePanels_StacksHeightWithGap()
    {
        var tree = new LayoutTree();
        var a = P("a");
        var b = P("b");
        var c = P("c");
        tree.AddRoot(a);
        tree.AddColumn("a", [b, c]);

        tree.Solve(20, 13);

        // Content 13-2 gaps = 11; shares [3,4,4], dust absorbed last.
        await Assert.That(a.Rect).IsEqualTo(new Rect(0, 0, 20, 3));
        await Assert.That(b.Rect).IsEqualTo(new Rect(0, 4, 20, 4));
        await Assert.That(c.Rect).IsEqualTo(new Rect(0, 9, 20, 4));
    }

    [Test]
    public async Task Stack_NestedBinarySplit_SolvesBoth()
    {
        var tree = new LayoutTree();
        var a = P("a");
        var b = P("b");
        var d = P("d");
        tree.AddRoot(a);
        tree.AddRow("a", [b]);
        tree.Split("b", SplitDir.Vertical, 0.5f, d, gap: 1);

        tree.Solve(30, 10);

        // Row: two equal shares of 28 → 14 each; b's column splits 9 usable
        // rows by ratio into 4 + 1 gap + 5.
        await Assert.That(a.Rect).IsEqualTo(new Rect(0, 0, 14, 10));
        await Assert.That(b.Rect).IsEqualTo(new Rect(15, 0, 14, 4));
        await Assert.That(d.Rect).IsEqualTo(new Rect(15, 5, 14, 5));
    }

    [Test]
    public async Task Remove_StackChildDownToOne_SplicesSurvivor()
    {
        var tree = new LayoutTree();
        var a = P("a");
        var b = P("b");
        var c = P("c");
        tree.AddRoot(a);
        tree.AddRow("a", [b, c]);
        tree.Remove("c");
        tree.Remove("b");

        tree.Solve(30, 10);

        await Assert.That(a.Rect).IsEqualTo(new Rect(0, 0, 30, 10));
        await Assert.That(tree.Panels.Count).IsEqualTo(1);
    }

    [Test]
    public async Task AddRow_UnknownPanel_Throws()
    {
        var tree = new LayoutTree();
        tree.AddRoot(P("a"));

        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
        {
            tree.AddRow("missing", [P("b")]);
            await Task.CompletedTask;
        });
    }

    [Test]
    public async Task AddStack_TrackCountMismatch_Throws()
    {
        var tree = new LayoutTree();
        tree.AddRoot(P("a"));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            tree.AddStack("a", SplitDir.Horizontal, [P("b")], [FlexTrack.Fill(1), FlexTrack.Fill(1)]);
            await Task.CompletedTask;
        });
    }
}
