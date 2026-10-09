using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// ENG2 #273 moat: the <see cref="DirtyRect"/> invalidation cascade
/// (Terminal.Gui NeedsDraw pattern) — viewport-local dirty rects with Union,
/// narrow cascade into children, separate self/children paint gates, and
/// narrow clears. A spinner tick must never degrade into full-screen
/// invalidation.
/// </summary>
public class DirtyRectTests
{
    [Test]
    public async Task Invalidate_ClipsToViewport_AndUnions()
    {
        var root = new DirtyRect(80, 24);

        root.Invalidate(new EngineCells.Rect(-5, -5, 10, 10));
        var first = root.Dirty;
        await Assert.That(first.HasValue).IsTrue();
        await Assert.That(first!.Value).IsEqualTo(new EngineCells.Rect(0, 0, 5, 5));

        root.Invalidate(new EngineCells.Rect(2, 2, 10, 10));
        var merged = root.Dirty;
        await Assert.That(merged.HasValue).IsTrue();
        await Assert.That(merged!.Value).IsEqualTo(new EngineCells.Rect(0, 0, 12, 12));
        await Assert.That(root.DirtyArea).IsEqualTo(144);
    }

    [Test]
    public async Task Invalidate_EmptyOrOffscreen_IsNoOp()
    {
        var root = new DirtyRect(80, 24);

        root.Invalidate(new EngineCells.Rect(10, 10, 0, 5));
        root.Invalidate(new EngineCells.Rect(200, 200, 5, 5));
        root.Invalidate(new EngineCells.Rect(-10, -10, 5, 5));

        await Assert.That(root.NeedsAnyDraw).IsFalse();
        await Assert.That(root.Dirty.HasValue).IsFalse();
        await Assert.That(root.DirtyArea).IsEqualTo(0);

        var collapsed = new DirtyRect(0, 0);
        collapsed.Invalidate(new EngineCells.Rect(0, 0, 80, 24));
        collapsed.InvalidateAll();
        await Assert.That(collapsed.NeedsAnyDraw).IsFalse();
        await Assert.That(collapsed.FullInvalidations).IsEqualTo(0);
    }

    [Test]
    public async Task ChildInvalidate_BubblesFlag_WithoutSelfDirty()
    {
        var root = new DirtyRect(80, 24);
        var child = new DirtyRect(40, 10);
        root.AddChild(child, new EngineCells.Rect(5, 5, 40, 10));

        child.Invalidate(new EngineCells.Rect(0, 0, 4, 2));

        await Assert.That(child.NeedsDraw).IsTrue();
        await Assert.That(root.NeedsDraw).IsFalse();
        await Assert.That(root.ChildNeedsDraw).IsTrue();
        await Assert.That(root.NeedsAnyDraw).IsTrue();
    }

    [Test]
    public async Task ParentInvalidate_NarrowCascades_TranslatedIntoChild()
    {
        var root = new DirtyRect(80, 24);
        var child = new DirtyRect(40, 10);
        var sibling = new DirtyRect(10, 10);
        root.AddChild(child, new EngineCells.Rect(5, 5, 40, 10));
        root.AddChild(sibling, new EngineCells.Rect(50, 10, 10, 10));

        root.Invalidate(new EngineCells.Rect(7, 6, 4, 3));

        var childDirty = child.Dirty;
        await Assert.That(childDirty.HasValue).IsTrue();
        await Assert.That(childDirty!.Value).IsEqualTo(new EngineCells.Rect(2, 1, 4, 3));
        await Assert.That(sibling.NeedsAnyDraw).IsFalse();

        // Region outside every child frame: self dirty, children untouched.
        root.ClearAll();
        root.Invalidate(new EngineCells.Rect(0, 0, 80, 1));
        await Assert.That(root.NeedsDraw).IsTrue();
        await Assert.That(child.NeedsAnyDraw).IsFalse();
        await Assert.That(sibling.NeedsAnyDraw).IsFalse();
    }

    [Test]
    public async Task PaintGates_SelfVsChildrenDrawSeparately()
    {
        var root = new DirtyRect(80, 24);
        var child = new DirtyRect(40, 10);
        root.AddChild(child, new EngineCells.Rect(5, 5, 40, 10));

        // Child-only damage: the self paint is skipped, the child paints,
        // and the acknowledge clears the ancestor flag.
        child.Invalidate(new EngineCells.Rect(0, 0, 4, 2));
        int selfPaints = 0;
        int childPaints = 0;
        if (root.NeedsDraw)
        {
            selfPaints++;
        }

        if (root.ChildNeedsDraw && child.NeedsAnyDraw)
        {
            childPaints++;
            child.ClearAll();
        }

        await Assert.That(selfPaints).IsEqualTo(0);
        await Assert.That(childPaints).IsEqualTo(1);
        await Assert.That(root.ChildNeedsDraw).IsFalse();
        await Assert.That(root.NeedsAnyDraw).IsFalse();

        // Self-only damage outside the child frame: the child paint is skipped.
        root.Invalidate(new EngineCells.Rect(0, 0, 80, 1));
        selfPaints = 0;
        childPaints = 0;
        if (root.NeedsDraw)
        {
            selfPaints++;
            root.ClearDrawn(root.Dirty!.Value);
        }

        if (root.ChildNeedsDraw)
        {
            childPaints++;
        }

        await Assert.That(selfPaints).IsEqualTo(1);
        await Assert.That(childPaints).IsEqualTo(0);
        await Assert.That(root.NeedsAnyDraw).IsFalse();
    }

    [Test]
    public async Task ClearDrawn_PartialKeepsRemainder_FullClears()
    {
        var root = new DirtyRect(80, 24);
        root.Invalidate(new EngineCells.Rect(0, 0, 10, 10));

        // Partial cover keeps the remainder dirty — damage is never dropped.
        root.ClearDrawn(new EngineCells.Rect(0, 0, 10, 5));
        await Assert.That(root.NeedsDraw).IsTrue();
        await Assert.That(root.Dirty!.Value).IsEqualTo(new EngineCells.Rect(0, 0, 10, 10));

        root.ClearDrawn(new EngineCells.Rect(0, 0, 10, 10));
        await Assert.That(root.NeedsAnyDraw).IsFalse();
    }

    [Test]
    public async Task ClearDrawn_CascadesNarrow_SiblingKeepsDirty()
    {
        var root = new DirtyRect(80, 24);
        var child = new DirtyRect(40, 10);
        var sibling = new DirtyRect(10, 10);
        root.AddChild(child, new EngineCells.Rect(5, 5, 40, 10));
        root.AddChild(sibling, new EngineCells.Rect(50, 0, 10, 10));

        root.Invalidate(new EngineCells.Rect(0, 0, 80, 24));
        root.ClearDrawn(new EngineCells.Rect(5, 5, 40, 10));

        await Assert.That(child.NeedsAnyDraw).IsFalse();
        await Assert.That(sibling.NeedsAnyDraw).IsTrue();
        await Assert.That(root.NeedsDraw).IsTrue();
        await Assert.That(root.ChildNeedsDraw).IsTrue();
    }

    [Test]
    public async Task NarrowTicks_NeverExpandToFullscreen()
    {
        var root = new DirtyRect(100, 40);

        for (int i = 0; i < 60; i++)
        {
            root.Invalidate(new EngineCells.Rect(90 + (i & 1), 39, 2, 1));
        }

        // Union of the alternating 2-cell ticks: 3 cells, not the 4000-cell screen.
        var dirty = root.Dirty;
        await Assert.That(dirty.HasValue).IsTrue();
        await Assert.That(dirty!.Value).IsEqualTo(new EngineCells.Rect(90, 39, 3, 1));
        await Assert.That(root.DirtyArea).IsEqualTo(3);
        await Assert.That(root.FullInvalidations).IsEqualTo(0);

        root.ClearDrawn(new EngineCells.Rect(88, 39, 12, 1));
        await Assert.That(root.NeedsAnyDraw).IsFalse();
        await Assert.That(root.FullInvalidations).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidateAll_MarksWholeViewport_ClearAllResets()
    {
        var root = new DirtyRect(80, 24);
        var child = new DirtyRect(40, 10);
        root.AddChild(child, new EngineCells.Rect(5, 5, 40, 10));

        root.InvalidateAll();

        await Assert.That(root.Dirty!.Value).IsEqualTo(new EngineCells.Rect(0, 0, 80, 24));
        await Assert.That(root.FullInvalidations).IsEqualTo(1);
        await Assert.That(child.Dirty!.Value).IsEqualTo(new EngineCells.Rect(0, 0, 40, 10));
        await Assert.That(child.FullInvalidations).IsEqualTo(0);

        root.ClearAll();
        await Assert.That(root.NeedsAnyDraw).IsFalse();
        await Assert.That(child.NeedsAnyDraw).IsFalse();
    }

    [Test]
    public async Task RemoveChild_DetachesSubtreeFlags()
    {
        var root = new DirtyRect(80, 24);
        var child = new DirtyRect(40, 10);
        root.AddChild(child, new EngineCells.Rect(5, 5, 40, 10));
        child.Invalidate(new EngineCells.Rect(0, 0, 4, 2));
        await Assert.That(root.ChildNeedsDraw).IsTrue();

        await Assert.That(root.RemoveChild(child)).IsTrue();
        await Assert.That(root.ChildNeedsDraw).IsFalse();
        await Assert.That(root.ChildCount).IsEqualTo(0);
        await Assert.That(child.NeedsDraw).IsTrue();
        await Assert.That(root.RemoveChild(child)).IsFalse();
    }

    [Test]
    public async Task AddChild_RejectsDoubleParent()
    {
        var first = new DirtyRect(80, 24);
        var second = new DirtyRect(80, 24);
        var child = new DirtyRect(10, 10);
        first.AddChild(child, new EngineCells.Rect(0, 0, 10, 10));

        bool threw = false;
        try
        {
            second.AddChild(child, new EngineCells.Rect(0, 0, 10, 10));
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }
}
