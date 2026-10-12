using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// SC2 #1210 (epic #1195): Spectre <c>Layout</c> composition seam on the binary
/// <see cref="LayoutTree"/> — fixed Size wins over ratio, MinimumSize floor
/// holds, IsVisible excludes from the split, and named access
/// (<c>tree[id]</c> / GetPanel / Update) recomposes by name.
/// </summary>
public sealed class LayoutTreeNamedFixedTests
{
    private sealed class StubPanel(string id, int minW, int minH, int priority = 0)
        : Panel(id, new Size(minW, minH), priority)
    {
        public override void Paint(ScreenBuffer buffer)
        {
        }
    }

    private static LayoutTree BuildSplit()
    {
        var tree = new LayoutTree();
        tree.AddRoot(new StubPanel("a", 1, 1));
        tree.Split("a", SplitDir.Horizontal, 0.5f, new StubPanel("b", 1, 1), gap: 1);
        return tree;
    }

    [Test]
    public async Task FixedSize_WinsOverRatio()
    {
        var tree = BuildSplit();
        tree.SetFixedSize("a", 10);
        tree.Solve(40, 10);

        await Assert.That(tree["a"].Rect).IsEqualTo(new Rect(0, 0, 10, 10));
        await Assert.That(tree["b"].Rect).IsEqualTo(new Rect(11, 0, 29, 10));
    }

    [Test]
    public async Task FixedSize_ClampedToMinimumFloor()
    {
        var tree = new LayoutTree();
        tree.AddRoot(new StubPanel("a", 5, 1));
        tree.Split("a", SplitDir.Horizontal, 0.5f, new StubPanel("b", 1, 1), gap: 1);
        tree.SetFixedSize("a", 2);
        tree.Solve(40, 10);

        // Asked 2, floor is 5: MinimumSize wins over Size.
        await Assert.That(tree["a"].Rect.Width).IsEqualTo(5);
        await Assert.That(tree["b"].Rect.Width).IsEqualTo(34);
    }

    [Test]
    public async Task FixedSize_Null_ClearsBackToRatio()
    {
        var tree = BuildSplit();
        tree.SetFixedSize("a", 10);
        tree.SetFixedSize("a", null);
        tree.Solve(40, 10);

        // 0.5 * (40 - 1) = 19.5 -> round 20; B takes the remainder 19.
        await Assert.That(tree["a"].Rect.Width).IsEqualTo(20);
        await Assert.That(tree["b"].Rect.Width).IsEqualTo(19);
    }

    [Test]
    public async Task HiddenPanel_GivesSiblingFullExtent()
    {
        var tree = BuildSplit();
        tree.SetVisible("b", false);
        tree.Solve(40, 10);

        await Assert.That(tree["a"].Rect).IsEqualTo(new Rect(0, 0, 40, 10));
        await Assert.That(tree["b"].Rect).IsEqualTo(default(Rect));
    }

    [Test]
    public async Task HiddenRoot_OwnsNoCells()
    {
        var tree = new LayoutTree();
        tree.AddRoot(new StubPanel("solo", 1, 1));
        tree.SetVisible("solo", false);
        tree.Solve(40, 10);

        await Assert.That(tree["solo"].Rect).IsEqualTo(default(Rect));
    }

    [Test]
    public async Task SetVisible_TogglesBackToRatioLayout()
    {
        var tree = BuildSplit();
        tree.SetVisible("b", false);
        tree.Solve(40, 10);
        tree.SetVisible("b", true);
        tree.Solve(40, 10);

        await Assert.That(tree["a"].Rect.Width).IsEqualTo(20);
        await Assert.That(tree["b"].Rect.Width).IsEqualTo(19);
    }

    [Test]
    public async Task Indexer_And_TryGetPanel_NamedAccess()
    {
        var tree = BuildSplit();

        await Assert.That(ReferenceEquals(tree["a"], tree.GetPanel("a"))).IsTrue();
        await Assert.That(tree.TryGetPanel("b", out var b)).IsTrue();
        await Assert.That(b!.Id).IsEqualTo("b");
        await Assert.That(tree.TryGetPanel("missing", out _)).IsFalse();
        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
        {
            _ = tree["missing"];
            await Task.CompletedTask;
        });
    }

    [Test]
    public async Task Update_ReplacesLeaf_KeepsShape()
    {
        var tree = BuildSplit();
        tree.Solve(40, 10);
        var before = tree["b"].Rect;

        var replacement = new StubPanel("b", 1, 1);
        tree.Update("b", replacement);
        tree.Solve(40, 10);

        await Assert.That(ReferenceEquals(tree["b"], replacement)).IsTrue();
        await Assert.That(tree["b"].Rect).IsEqualTo(before);
        await Assert.That(tree.Panels.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Update_IdMismatch_And_Missing_Throw()
    {
        var tree = BuildSplit();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            tree.Update("b", new StubPanel("other", 1, 1));
            await Task.CompletedTask;
        });
        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
        {
            tree.Update("missing", new StubPanel("missing", 1, 1));
            await Task.CompletedTask;
        });
    }

    [Test]
    public async Task Solve_WidthsSumToUsable_NoDust()
    {
        var tree = BuildSplit();
        tree.Solve(41, 10);

        int total = tree["a"].Rect.Width + tree["b"].Rect.Width + 1;
        await Assert.That(total).IsEqualTo(41);
    }
}
