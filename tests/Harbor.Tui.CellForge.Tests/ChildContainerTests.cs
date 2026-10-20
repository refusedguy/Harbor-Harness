using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// RC2 (#1200, epic #1195): heterogeneous Panel child container — rect math
/// mirrors the FlexLayout solver, paint fans out in order, nesting solves
/// through, removal and validation behave. Layout math plus paint smoke —
/// no viewport, no goldens touched.
/// </summary>
public sealed class ChildContainerTests
{
    private sealed class ProbePanel : Panel
    {
        private readonly List<string> _log;

        public ProbePanel(string id, List<string> log)
            : base(id, new EngineCells.Size(1, 1), 0)
        {
            _log = log;
        }

        public override void Paint(ScreenBuffer buffer) => _log.Add(Id);
    }

    private static ChildContainer Row(int gap = 0, FlexJustify justify = FlexJustify.Start) =>
        new("row", SplitDir.Horizontal, new EngineCells.Size(1, 1), gap: gap, justify: justify);

    private static ChildContainer Column(int gap = 0) =>
        new("col", SplitDir.Vertical, new EngineCells.Size(1, 1), gap: gap);

    [Test]
    public async Task Row_FixedFillFixed_AssignsChildRects()
    {
        var log = new List<string>();
        var container = Row(gap: 1);
        var a = new ProbePanel("a", log);
        var b = new ProbePanel("b", log);
        var c = new ProbePanel("c", log);
        container.Add(a, FlexTrack.Fixed(10));
        container.Add(b, FlexTrack.Fill(1));
        container.Add(c, FlexTrack.Fixed(6));

        container.Solve(new Rect(0, 0, 40, 10));

        await Assert.That(a.Rect).IsEqualTo(new Rect(0, 0, 10, 10));
        await Assert.That(b.Rect).IsEqualTo(new Rect(11, 0, 22, 10));
        await Assert.That(c.Rect).IsEqualTo(new Rect(34, 0, 6, 10));
    }

    [Test]
    public async Task Column_FixedFillFixed_StacksAlongY()
    {
        var log = new List<string>();
        var container = Column(gap: 1);
        var a = new ProbePanel("a", log);
        var b = new ProbePanel("b", log);
        var c = new ProbePanel("c", log);
        container.Add(a, FlexTrack.Fixed(10));
        container.Add(b, FlexTrack.Fill(1));
        container.Add(c, FlexTrack.Fixed(6));

        container.Solve(new Rect(0, 0, 10, 40));

        await Assert.That(a.Rect).IsEqualTo(new Rect(0, 0, 10, 10));
        await Assert.That(b.Rect).IsEqualTo(new Rect(0, 11, 10, 22));
        await Assert.That(c.Rect).IsEqualTo(new Rect(0, 34, 10, 6));
    }

    [Test]
    public async Task JustifyCenter_OffsetsPackedRun()
    {
        var log = new List<string>();
        var container = Row(gap: 2, justify: FlexJustify.Center);
        var a = new ProbePanel("a", log);
        var b = new ProbePanel("b", log);
        container.Add(a, FlexTrack.Fixed(6));
        container.Add(b, FlexTrack.Fixed(4));

        container.Solve(new Rect(0, 0, 20, 4));

        await Assert.That(a.Rect).IsEqualTo(new Rect(4, 0, 6, 4));
        await Assert.That(b.Rect).IsEqualTo(new Rect(12, 0, 4, 4));
    }

    [Test]
    public async Task Paint_PaintsChildrenInOrder()
    {
        var log = new List<string>();
        var container = Row();
        container.Add(new ProbePanel("a", log), FlexTrack.Fill(1));
        container.Add(new ProbePanel("b", log), FlexTrack.Fill(1));
        container.Add(new ProbePanel("c", log), FlexTrack.Fill(1));
        container.Solve(new Rect(0, 0, 30, 5));

        container.Paint(TestRender.Buffer(30, 5));

        await Assert.That(string.Join(",", log)).IsEqualTo("a,b,c");
    }

    [Test]
    public async Task Paint_ZeroSizeChildren_AreSkipped()
    {
        var log = new List<string>();
        var container = Row();
        var a = new ProbePanel("a", log);
        var b = new ProbePanel("b", log);
        container.Add(a, FlexTrack.Fixed(10));
        container.Add(b, FlexTrack.Fixed(10));

        container.Solve(new Rect(0, 0, 0, 10));
        container.Paint(TestRender.Buffer(1, 10));

        await Assert.That(a.Rect).IsEqualTo(new Rect(0, 0, 0, 10));
        await Assert.That(b.Rect).IsEqualTo(new Rect(0, 0, 0, 10));
        await Assert.That(log.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Nested_ContainerInsideContainer_SolvesThroughPaint()
    {
        var log = new List<string>();
        var outer = Column();
        var top = new ProbePanel("top", log);
        var inner = new ChildContainer("inner", SplitDir.Horizontal, new EngineCells.Size(1, 1));
        var left = new ProbePanel("left", log);
        var right = new ProbePanel("right", log);
        inner.Add(left, FlexTrack.Fixed(10));
        inner.Add(right, FlexTrack.Fill(1));
        outer.Add(top, FlexTrack.Fixed(10));
        outer.Add(inner, FlexTrack.Fill(1));

        // No explicit inner.Solve: the outer solve seats the inner rect, and
        // the inner arrange happens lazily on paint (same as tree-seating).
        outer.Solve(new Rect(0, 0, 30, 20));
        outer.Paint(TestRender.Buffer(30, 20));

        await Assert.That(top.Rect).IsEqualTo(new Rect(0, 0, 30, 10));
        await Assert.That(inner.Rect).IsEqualTo(new Rect(0, 10, 30, 10));
        await Assert.That(left.Rect).IsEqualTo(new Rect(0, 10, 10, 10));
        await Assert.That(right.Rect).IsEqualTo(new Rect(10, 10, 20, 10));
        await Assert.That(string.Join(",", log)).IsEqualTo("top,left,right");
    }

    [Test]
    public async Task Remove_DropsChildAndTrack_ResolvesRemainder()
    {
        var log = new List<string>();
        var container = Row(gap: 1);
        var a = new ProbePanel("a", log);
        var b = new ProbePanel("b", log);
        var c = new ProbePanel("c", log);
        container.Add(a, FlexTrack.Fixed(10));
        container.Add(b, FlexTrack.Fill(1));
        container.Add(c, FlexTrack.Fixed(6));
        container.Solve(new Rect(0, 0, 40, 10));

        await Assert.That(container.Remove(b)).IsTrue();
        await Assert.That(container.Remove(b)).IsFalse();
        await Assert.That(container.Count).IsEqualTo(2);

        container.Solve(new Rect(0, 0, 40, 10));

        await Assert.That(a.Rect).IsEqualTo(new Rect(0, 0, 10, 10));
        await Assert.That(c.Rect).IsEqualTo(new Rect(11, 0, 6, 10));
    }

    [Test]
    public async Task Paint_TreeSeatedRect_ResolvesWithoutExplicitSolve()
    {
        // LayoutTree seats a panel by assigning Rect directly (no Solve call).
        // Paint must notice the move and arrange — this is the no-ChatScreen-change path.
        var log = new List<string>();
        var container = Row();
        var a = new ProbePanel("a", log);
        var b = new ProbePanel("b", log);
        container.Add(a, FlexTrack.Fill(1));
        container.Add(b, FlexTrack.Fill(1));
        container.Rect = new Rect(5, 2, 20, 6);

        container.Paint(TestRender.Buffer(30, 12));

        await Assert.That(a.Rect).IsEqualTo(new Rect(5, 2, 10, 6));
        await Assert.That(b.Rect).IsEqualTo(new Rect(15, 2, 10, 6));
    }

    [Test]
    public async Task Add_NullChild_ThrowsArgumentNull()
    {
        var container = Row();

        await Assert.That(() => container.Add(null!, FlexTrack.Fill(1))).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Ctor_NegativeGap_ThrowsArgumentOutOfRange()
    {
        await Assert.That(() => new ChildContainer(
            "x",
            SplitDir.Horizontal,
            new EngineCells.Size(1, 1),
            gap: -1)).Throws<ArgumentOutOfRangeException>();
    }
}
