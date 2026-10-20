using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #1201 compose declarative screen builder: a leaf (id, track, leaf-factory)
/// list plus splits assembled through <see cref="LayoutTree"/>, and the
/// generalized <see cref="ScreenBuilder.AttachLeaf"/> reservation behind
/// <c>ChatScreenPanelDock.AttachBottom</c>/<c>AttachSide</c>.
/// Layout math only — no goldens touched.
/// </summary>
public class ScreenBuilderTests
{
    private sealed class StubPanel(string id, int minW, int minH, int priority = 0) : Panel(id, new Size(minW, minH), priority)
    {
        public override void Paint(ScreenBuffer buffer) { }
    }

    private static async Task AssertSameRects(LayoutTree built, LayoutTree manual)
    {
        var b = built.Panels.ToDictionary(p => p.Id, p => p.Rect);
        var m = manual.Panels.ToDictionary(p => p.Id, p => p.Rect);
        await Assert.That(b.Count).IsEqualTo(m.Count);
        foreach (var (id, rect) in m)
        {
            await Assert.That(b.ContainsKey(id)).IsTrue();
            await Assert.That(b[id].X).IsEqualTo(rect.X);
            await Assert.That(b[id].Y).IsEqualTo(rect.Y);
            await Assert.That(b[id].Width).IsEqualTo(rect.Width);
            await Assert.That(b[id].Height).IsEqualTo(rect.Height);
        }
    }

    [Test]
    public async Task Build_SingleLeaf_SolvesAndPaintsTitle()
    {
        var tree = ScreenBuilder.Build([
            new ScreenLeafSpec { Id = "a", Track = FlexTrack.Fill(), Create = () => new BorderPanel("a", 4, 1, title: "hello builder") },
        ]);
        await Assert.That(tree.Panels.Count).IsEqualTo(1);
        var buffer = new ScreenBuffer(30, 6);
        tree.Solve(30, 6);
        tree.PaintAll(buffer);
        await Assert.That(ScreenFixtures.Art(buffer).Contains("hello builder")).IsTrue();
    }

    [Test]
    public async Task Build_TwoLeaves_VerticalSplit_MatchesManualTree()
    {
        var built = ScreenBuilder.Build(
            [
                new ScreenLeafSpec { Id = "top", Track = FlexTrack.Fill(), Create = () => new StubPanel("top", 4, 1) },
                new ScreenLeafSpec { Id = "bottom", Track = FlexTrack.Fixed(3), Create = () => new StubPanel("bottom", 4, 3) },
            ],
            [
                new ScreenSplitSpec { TargetId = "top", Direction = SplitDir.Vertical, Ratio = 0.5f, NewPanelId = "bottom", Gap = 0 },
            ]);

        var manual = new LayoutTree();
        manual.AddRoot(new StubPanel("top", 4, 1));
        manual.Split("top", SplitDir.Vertical, 0.5f, new StubPanel("bottom", 4, 3), gap: 0);

        built.Solve(30, 10);
        manual.Solve(30, 10);
        await AssertSameRects(built, manual);
    }

    [Test]
    public async Task Build_DuplicateLeafId_Throws()
    {
        await Assert.That(() => ScreenBuilder.Build([
            new ScreenLeafSpec { Id = "a", Create = () => new StubPanel("a", 1, 1) },
            new ScreenLeafSpec { Id = "a", Create = () => new StubPanel("a", 1, 1) },
        ])).Throws<ArgumentException>();
    }

    [Test]
    public async Task Build_SplitSeatsUnknownLeaf_Throws()
    {
        await Assert.That(() => ScreenBuilder.Build(
            [new ScreenLeafSpec { Id = "a", Create = () => new StubPanel("a", 1, 1) }],
            [new ScreenSplitSpec { TargetId = "a", NewPanelId = "ghost" }])).Throws<ArgumentException>();
    }

    [Test]
    public async Task Build_SplitTargetsUnknownAnchor_Throws()
    {
        await Assert.That(() => ScreenBuilder.Build(
            [
                new ScreenLeafSpec { Id = "a", Create = () => new StubPanel("a", 1, 1) },
                new ScreenLeafSpec { Id = "b", Create = () => new StubPanel("b", 1, 1) },
            ],
            [new ScreenSplitSpec { TargetId = "ghost", NewPanelId = "b" }])).Throws<KeyNotFoundException>();
    }

    [Test]
    public async Task Build_NaN_Ratio_DegradesToEvenSplit()
    {
        var tree = ScreenBuilder.Build(
            [
                new ScreenLeafSpec { Id = "a", Create = () => new StubPanel("a", 1, 1) },
                new ScreenLeafSpec { Id = "b", Create = () => new StubPanel("b", 1, 1) },
            ],
            [new ScreenSplitSpec { TargetId = "a", Ratio = float.NaN, NewPanelId = "b" }]);
        tree.Solve(30, 10);
        await Assert.That(tree.Panels.Count).IsEqualTo(2);
    }

    [Test]
    public async Task AttachLeaf_Vertical_ReservesExactRows()
    {
        var tree = new LayoutTree();
        tree.AddRoot(new StubPanel("timeline", 4, 1, priority: 10));
        tree.Solve(40, 24);

        ScreenBuilder.AttachLeaf(tree, "timeline", SplitDir.Vertical, new StubPanel("dock", 4, 3), sizeCells: 6, gap: 0);
        tree.Solve(40, 24);

        var byId = tree.Panels.ToDictionary(p => p.Id, p => p.Rect);
        await Assert.That(byId["dock"].Height).IsEqualTo(6);
        await Assert.That(byId["timeline"].Height).IsEqualTo(18);
    }

    [Test]
    public async Task AttachLeaf_Horizontal_ReservesExactColumns()
    {
        var tree = new LayoutTree();
        tree.AddRoot(new StubPanel("timeline", 4, 1, priority: 10));
        tree.Solve(80, 10);

        ScreenBuilder.AttachLeaf(tree, "timeline", SplitDir.Horizontal, new StubPanel("side", 20, 1), sizeCells: 20, gap: 1);
        tree.Solve(80, 10);

        var byId = tree.Panels.ToDictionary(p => p.Id, p => p.Rect);
        await Assert.That(byId["side"].Width).IsEqualTo(20);
        await Assert.That(byId["timeline"].Width).IsEqualTo(59);
    }

    [Test]
    public async Task AttachLeaf_Oversize_DegradesToSliverInsteadOfCollapsing()
    {
        var tree = new LayoutTree();
        tree.AddRoot(new StubPanel("timeline", 4, 1, priority: 10));
        tree.Solve(40, 10);

        ScreenBuilder.AttachLeaf(tree, "timeline", SplitDir.Vertical, new StubPanel("dock", 4, 1), sizeCells: 500, gap: 0);
        tree.Solve(40, 10);

        var byId = tree.Panels.ToDictionary(p => p.Id, p => p.Rect);
        await Assert.That(byId["timeline"].Height).IsGreaterThan(0);
        await Assert.That(byId["dock"].Height).IsGreaterThan(0);
    }

    [Test]
    public async Task AttachLeaf_UnknownAnchor_Throws()
    {
        var tree = new LayoutTree();
        tree.AddRoot(new StubPanel("a", 1, 1));
        await Assert.That(() => ScreenBuilder.AttachLeaf(tree, "ghost", SplitDir.Vertical, new StubPanel("b", 1, 1), sizeCells: 3))
            .Throws<KeyNotFoundException>();
    }
}
