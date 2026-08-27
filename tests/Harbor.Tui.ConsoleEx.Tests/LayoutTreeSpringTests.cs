using Harbor.Tui.ConsoleEx.Rendering;

namespace Harbor.Tui.ConsoleEx.Tests;

/// <summary>
/// Spring-driven split-ratio animation on the layout tree (HDS v1
/// panel-resize motion): rects track the spring each frame, the geometry
/// cache is bypassed mid-flight, and the settled layout matches a direct
/// ratio solve.
/// </summary>
public class LayoutTreeSpringTests
{
    private static (LayoutTree Tree, Panel A, Panel B) BuildSplit(float ratio)
    {
        var tree = new LayoutTree();
        var a = new BorderPanel("a", minWidth: 2, minHeight: 2);
        var b = new BorderPanel("b", minWidth: 2, minHeight: 2);
        tree.AddRoot(a);
        tree.Split("a", SplitDir.Vertical, ratio, b, gap: 1);
        return (tree, a, b);
    }

    [Test]
    public async Task AnimateRatio_RectsTrackSpring_AndSettleOnTarget()
    {
        (var tree, var a, var b) = BuildSplit(0.5f);

        tree.AnimateRatio("a", 0.8f);
        int lastHeight = a.Rect.Height;
        bool grew = false;
        for (int frame = 0; frame < 200; frame++)
        {
            tree.Solve(40, 20);
            grew |= a.Rect.Height > lastHeight;
            lastHeight = a.Rect.Height;
        }

        // Settled: A gets round((20-1) * 0.8) = 15 rows; B the rest.
        await Assert.That(a.Rect.Height).IsEqualTo(15);
        await Assert.That(b.Rect.Height).IsEqualTo(4);
        await Assert.That(grew).IsTrue(); // heights animated, not a jump
    }

    [Test]
    public async Task Solve_BypassesCache_WhileSpringIsInFlight()
    {
        (var tree, var a, var b) = BuildSplit(0.5f);
        tree.Solve(40, 20);
        int settledHeight = a.Rect.Height;

        tree.AnimateRatio("a", 0.8f);
        tree.Solve(40, 20);
        int midFlight = a.Rect.Height;

        await Assert.That(midFlight).IsNotEqualTo(settledHeight); // same viewport, moving rects
    }

    [Test]
    public async Task AnimateRatio_UnknownPanel_Throws()
    {
        (var tree, _, _) = BuildSplit(0.5f);

        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
        {
            tree.AnimateRatio("missing", 0.7f);
            await Task.CompletedTask;
        });
    }

    [Test]
    public async Task AnimateRatio_SettledLayout_MatchesDirectRatioSolve()
    {
        (var springTree, var springA, _) = BuildSplit(0.5f);
        springTree.AnimateRatio("a", 0.8f);
        for (int frame = 0; frame < 200; frame++)
        {
            springTree.Solve(40, 20);
        }

        (var directTree, var directA, _) = BuildSplit(0.8f);
        directTree.Solve(40, 20);

        await Assert.That(springA.Rect).IsEqualTo(directA.Rect);
    }

    [Test]
    public async Task Remove_CancelsPendingSpring()
    {
        (var tree, var a, var b) = BuildSplit(0.5f);
        tree.AnimateRatio("a", 0.8f);
        tree.Remove("b"); // b promoted into the root leaf slot

        for (int frame = 0; frame < 40; frame++)
        {
            tree.Solve(40, 20);
        }

        await Assert.That(a.Rect.Height).IsEqualTo(20); // full viewport, no split left
    }
}
