using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

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

    // ── Min-width springs (sidebar-style show/hide, P1.6) ─────────────────

    /// <summary>Sidebar scenario: B pinned at its 42-style min on a wide row;
    /// A owns the rest. Gap 1, so usable = width − 1.</summary>
    private static (LayoutTree Tree, Panel A, Panel B) BuildSidebar(int viewportWidth)
    {
        var tree = new LayoutTree();
        var timeline = new BorderPanel("timeline", minWidth: 2, minHeight: 1, priority: 10);
        var sidebar = new BorderPanel("sidebar", minWidth: 6, minHeight: 1, priority: 5);
        tree.AddRoot(timeline);
        tree.Split("timeline", SplitDir.Horizontal, (viewportWidth - 1 - 6) / (float)(viewportWidth - 1), sidebar, gap: 1);
        tree.Solve(viewportWidth, 10);
        return (tree, timeline, sidebar);
    }

    [Test]
    public async Task AnimateMinWidth_Hide_GlidesToZero_NotBinaryJump()
    {
        (var tree, var timeline, var sidebar) = BuildSidebar(40);
        int shownWidth = sidebar.Rect.Width;
        await Assert.That(shownWidth).IsEqualTo(6);

        tree.AnimateRatio("timeline", 1.0f);
        tree.AnimateMinWidth("sidebar", 0);

        int sawShrinking = 0;
        int last = shownWidth;
        for (int frame = 0; frame < 200; frame++)
        {
            tree.Solve(40, 10);
            sawShrinking += sidebar.Rect.Width < last ? 1 : 0;
            last = sidebar.Rect.Width;
        }

        await Assert.That(sidebar.Rect.Width).IsEqualTo(0);
        await Assert.That(timeline.Rect.Width).IsEqualTo(39); // full usable extent
        await Assert.That(sawShrinking).IsGreaterThan(1); // several frames of motion, not a snap
    }

    [Test]
    public async Task AnimateMinWidth_Show_GrowsBackToBaseMinimum()
    {
        (var tree, _, var sidebar) = BuildSidebar(40);
        tree.AnimateRatio("timeline", 1.0f);
        tree.AnimateMinWidth("sidebar", 0);
        for (int frame = 0; frame < 200; frame++)
        {
            tree.Solve(40, 10);
        }

        tree.AnimateMinWidth("sidebar", 6);
        tree.AnimateRatio("timeline", (40 - 1 - 6) / 39f);
        int sawGrowing = 0;
        int last = sidebar.Rect.Width;
        for (int frame = 0; frame < 200; frame++)
        {
            tree.Solve(40, 10);
            sawGrowing += sidebar.Rect.Width > last ? 1 : 0;
            last = sidebar.Rect.Width;
        }

        await Assert.That(sidebar.Rect.Width).IsEqualTo(6);
        await Assert.That(sawGrowing).IsGreaterThan(1);
    }

    [Test]
    public async Task AnimateMinWidth_SettledZero_SmallViewportStillSolvesNoCollapse()
    {
        (var tree, var timeline, var sidebar) = BuildSidebar(40);
        tree.AnimateRatio("timeline", 1.0f);
        tree.AnimateMinWidth("sidebar", 0);
        for (int frame = 0; frame < 200; frame++)
        {
            tree.Solve(40, 10);
        }

        // Base minimums (2 + 6 + 1 = 9) fit; spring min (6 → 0) must not
        // binary-collapse the sidebar — it just stays fully hidden.
        tree.Solve(8, 10);
        await Assert.That(sidebar.Rect.Width).IsEqualTo(0);
        await Assert.That(timeline.Rect.Width).IsEqualTo(7); // usable = 8 − 1 gap
    }

    [Test]
    public async Task AnimateMinWidth_UnknownPanel_Throws()
    {
        (var tree, _, _) = BuildSidebar(40);

        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
        {
            tree.AnimateMinWidth("missing", 0);
            await Task.CompletedTask;
        });
    }

    [Test]
    public async Task IsAnimating_TracksSpringFlight()
    {
        (var tree, _, _) = BuildSidebar(40);
        await Assert.That(tree.IsAnimating).IsFalse();

        tree.AnimateMinWidth("sidebar", 0);
        await Assert.That(tree.IsAnimating).IsTrue();

        for (int frame = 0; frame < 200 && tree.IsAnimating; frame++)
        {
            tree.Solve(40, 10);
        }

        await Assert.That(tree.IsAnimating).IsFalse();
    }
}
