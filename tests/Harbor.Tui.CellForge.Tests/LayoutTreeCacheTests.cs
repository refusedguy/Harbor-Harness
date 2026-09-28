using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// ENG3 #274: <see cref="LayoutTree"/> caches solved results keyed by
/// (layout, area) in a bounded LRU — identical frames must not re-solve,
/// and recently used areas must survive interleaved resizes.
/// </summary>
public class LayoutTreeCacheTests
{
    private sealed class StubPanel(string id, int minW, int minH, int priority = 0) : Panel(id, new Size(minW, minH), priority)
    {
        public override void Paint(ScreenBuffer buffer) { }
    }

    private static LayoutTree BuildTree()
    {
        var tree = new LayoutTree();
        tree.AddRoot(new StubPanel("a", 1, 1));
        tree.Split("a", SplitDir.Horizontal, 0.5f, new StubPanel("b", 1, 1));
        return tree;
    }

    [Test]
    public async Task IdenticalFrames_SolveOnce()
    {
        var tree = BuildTree();
        tree.Solve(80, 24);
        var first = tree.Panels.ToDictionary(p => p.Id, p => p.Rect);

        for (int i = 0; i < 5; i++)
        {
            tree.Solve(80, 24);
        }

        await Assert.That(tree.FullSolveCount).IsEqualTo(1);
        await Assert.That(tree.CacheHitCount).IsEqualTo(5);
        foreach (var panel in tree.Panels)
        {
            await Assert.That(panel.Rect).IsEqualTo(first[panel.Id]);
        }
    }

    [Test]
    public async Task AlternatingAreas_BothStayCached()
    {
        var tree = BuildTree();
        tree.Solve(80, 24);
        var narrow = tree.Panels.ToDictionary(p => p.Id, p => p.Rect);
        tree.Solve(100, 30);
        var wide = tree.Panels.ToDictionary(p => p.Id, p => p.Rect);

        tree.Solve(80, 24);
        tree.Solve(100, 30);

        await Assert.That(tree.FullSolveCount).IsEqualTo(2);
        await Assert.That(tree.CacheHitCount).IsEqualTo(2);
        foreach (var panel in tree.Panels)
        {
            await Assert.That(panel.Rect).IsEqualTo(wide[panel.Id]);
        }

        tree.Solve(80, 24);
        foreach (var panel in tree.Panels)
        {
            await Assert.That(panel.Rect).IsEqualTo(narrow[panel.Id]);
        }
    }

    [Test]
    public async Task Cache_EvictsOldest_BoundedAt500()
    {
        var tree = BuildTree();
        for (int w = 1; w <= LayoutTree.MaxCachedLayouts + 10; w++)
        {
            tree.Solve(w, 24);
        }

        await Assert.That(tree.CacheCount).IsEqualTo(LayoutTree.MaxCachedLayouts);
        int solves = tree.FullSolveCount;

        tree.Solve(1, 24); // oldest entry evicted → miss
        await Assert.That(tree.FullSolveCount).IsEqualTo(solves + 1);
        await Assert.That(tree.CacheCount).IsEqualTo(LayoutTree.MaxCachedLayouts);
    }

    [Test]
    public async Task Mutation_KeysNewVersion()
    {
        var tree = BuildTree();
        tree.Solve(80, 24);
        tree.Split("a", SplitDir.Vertical, 0.5f, new StubPanel("c", 1, 1));
        tree.Solve(80, 24);

        await Assert.That(tree.FullSolveCount).IsEqualTo(2);
        await Assert.That(tree.CacheHitCount).IsEqualTo(0);
    }
}
