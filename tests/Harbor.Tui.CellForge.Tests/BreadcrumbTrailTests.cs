using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #2324: the generalized breadcrumb primitive. Covers the push/back/forward
/// history semantics (browser-style branch pruning) and all three render forms.
/// </summary>
public class BreadcrumbTrailTests
{
    [Test]
    public async Task EmptyTrail_HasNoCrumb()
    {
        var trail = new BreadcrumbTrail<string>(s => s);

        await Assert.That(trail.IsEmpty).IsTrue();
        await Assert.That(trail.Depth).IsEqualTo(0);
        await Assert.That(trail.Position).IsEqualTo(-1);
        await Assert.That(trail.Current).IsNull();
        await Assert.That(trail.CanBack).IsFalse();
        await Assert.That(trail.CanForward).IsFalse();
        await Assert.That(trail.Render()).IsEqualTo(string.Empty);
        await Assert.That(trail.ActivePath()).IsEmpty();
        await Assert.That(trail.Segments()).IsEmpty();
        await Assert.That(trail.Back()).IsFalse();
        await Assert.That(trail.Forward()).IsFalse();
    }

    [Test]
    public async Task Push_BuildsActivePathAndRenders()
    {
        var trail = new BreadcrumbTrail<string>(s => s);

        await Assert.That(trail.Push("root")).IsEqualTo(1);
        await Assert.That(trail.Push("src")).IsEqualTo(2);
        await Assert.That(trail.Push("Harbor")).IsEqualTo(3);

        await Assert.That(trail.IsEmpty).IsFalse();
        await Assert.That(trail.Depth).IsEqualTo(3);
        await Assert.That(trail.Position).IsEqualTo(2);
        await Assert.That(trail.HistoryCount).IsEqualTo(3);
        await Assert.That(trail.Current).IsEqualTo("Harbor");
        await Assert.That(trail.Render()).IsEqualTo("root / src / Harbor");
        await Assert.That(trail.Render(" > ")).IsEqualTo("root > src > Harbor");
        await Assert.That(trail.ActivePath()).IsEquivalentTo(new[] { "root", "src", "Harbor" });
    }

    [Test]
    public async Task BackAndForward_MoveCurrentAndShrinkRenderedPath()
    {
        var trail = new BreadcrumbTrail<string>(s => s);
        trail.Push("root");
        trail.Push("src");
        trail.Push("Harbor");

        await Assert.That(trail.CanBack).IsTrue();
        await Assert.That(trail.CanForward).IsFalse();

        await Assert.That(trail.Back()).IsTrue();
        await Assert.That(trail.Current).IsEqualTo("src");
        await Assert.That(trail.Render()).IsEqualTo("root / src");
        await Assert.That(trail.CanForward).IsTrue();

        await Assert.That(trail.Back()).IsTrue();
        await Assert.That(trail.Current).IsEqualTo("root");
        await Assert.That(trail.Render()).IsEqualTo("root");
        await Assert.That(trail.CanBack).IsFalse();

        await Assert.That(trail.Forward()).IsTrue();
        await Assert.That(trail.Current).IsEqualTo("src");
        await Assert.That(trail.Forward()).IsTrue();
        await Assert.That(trail.Current).IsEqualTo("Harbor");
        await Assert.That(trail.Render()).IsEqualTo("root / src / Harbor");
        await Assert.That(trail.CanForward).IsFalse();
    }

    [Test]
    public async Task Boundaries_AreNoOps()
    {
        var trail = new BreadcrumbTrail<string>(s => s);
        trail.Push("root");

        await Assert.That(trail.Back()).IsFalse();
        await Assert.That(trail.Current).IsEqualTo("root");
        await Assert.That(trail.Forward()).IsFalse();
        await Assert.That(trail.Current).IsEqualTo("root");
    }

    [Test]
    public async Task PushAfterBack_PrunesForwardTail()
    {
        var trail = new BreadcrumbTrail<string>(s => s);
        trail.Push("a");
        trail.Push("b");
        trail.Push("c");
        trail.Back(); // current = b, c is forward tail
        trail.Back(); // current = a, b and c forward

        await Assert.That(trail.CanForward).IsTrue();
        await Assert.That(trail.HistoryCount).IsEqualTo(3);

        await Assert.That(trail.Push("x")).IsEqualTo(2);
        await Assert.That(trail.HistoryCount).IsEqualTo(2);
        await Assert.That(trail.CanForward).IsFalse();
        await Assert.That(trail.Render()).IsEqualTo("a / x");
        await Assert.That(trail.Forward()).IsFalse();
    }

    [Test]
    public async Task Segments_ResolveLabelsAndMarkOnlyCurrent()
    {
        var trail = new BreadcrumbTrail<int>(n => $"node{n}");
        trail.Push(1);
        trail.Push(2);
        trail.Push(3);
        trail.Back();

        var segments = trail.Segments();
        await Assert.That(segments.Count).IsEqualTo(3);
        await Assert.That(segments[0].Label).IsEqualTo("node1");
        await Assert.That(segments[0].IsCurrent).IsFalse();
        await Assert.That(segments[1].Label).IsEqualTo("node2");
        await Assert.That(segments[1].IsCurrent).IsTrue();
        await Assert.That(segments[2].Label).IsEqualTo("node3");
        await Assert.That(segments[2].IsCurrent).IsFalse();
    }

    [Test]
    public async Task LabelOf_IsAppliedToEveryRenderForm()
    {
        var trail = new BreadcrumbTrail<int>(n => $"#{n}");
        trail.Push(1);
        trail.Push(2);

        await Assert.That(trail.Render()).IsEqualTo("#1 / #2");
        await Assert.That(trail.Segments()[1].Label).IsEqualTo("#2");

        var buffer = new EngineCells.ScreenBuffer(16, 1);
        int width = trail.Paint(buffer, 0, 0);
        await Assert.That(width).IsEqualTo(7);
        await Assert.That(buffer.Get(0, 0).Rune).IsEqualTo('#');
    }

    [Test]
    public async Task MeasureWidth_MatchesUnicodeWidthOfRender()
    {
        var trail = new BreadcrumbTrail<string>(s => s);
        trail.Push("a");
        trail.Push("\u6F22"); // CJK: two cells

        string text = trail.Render();
        await Assert.That(text).IsEqualTo("a / \u6F22");
        await Assert.That(trail.MeasureWidth()).IsEqualTo(EngineCells.UnicodeWidth.Width(text.AsSpan()));
        await Assert.That(trail.MeasureWidth()).IsEqualTo(6);
    }

    [Test]
    public async Task Paint_StylesAncestorsDimAndCurrentBold()
    {
        var trail = new BreadcrumbTrail<string>(s => s);
        trail.Push("root");
        trail.Push("src");
        trail.Push("Harbor");

        var buffer = new EngineCells.ScreenBuffer(24, 1);
        int width = trail.Paint(buffer, 0, 0);

        await Assert.That(width).IsEqualTo(19);
        // "root" dim, separators plain, "src" dim, last separator plain, "Harbor" bold.
        await Assert.That(buffer.Get(0, 0).Rune).IsEqualTo('r');
        await Assert.That(buffer.Get(0, 0).Flags).IsEqualTo((ushort)EngineCells.StyleAttr.Dim);
        await Assert.That(buffer.Get(4, 0).Rune).IsEqualTo(' ');
        await Assert.That(buffer.Get(4, 0).Flags).IsEqualTo((ushort)EngineCells.StyleAttr.None);
        await Assert.That(buffer.Get(7, 0).Flags).IsEqualTo((ushort)EngineCells.StyleAttr.Dim);
        await Assert.That(buffer.Get(13, 0).Rune).IsEqualTo('H');
        await Assert.That(buffer.Get(13, 0).Flags).IsEqualTo((ushort)EngineCells.StyleAttr.Bold);
    }

    [Test]
    public async Task Paint_EscapesBracketInLabel()
    {
        var trail = new BreadcrumbTrail<string>(s => s);
        trail.Push("[bold]literal");

        await Assert.That(trail.Render()).IsEqualTo("[bold]literal");

        var buffer = new EngineCells.ScreenBuffer(16, 1);
        int width = trail.Paint(buffer, 0, 0);
        await Assert.That(width).IsEqualTo(13);
        await Assert.That(buffer.Get(0, 0).Rune).IsEqualTo('[');
        await Assert.That(buffer.Get(0, 0).Flags).IsEqualTo((ushort)EngineCells.StyleAttr.Bold);
    }
}
