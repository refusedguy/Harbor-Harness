using System.Text;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>PRIM2a overlay z-stack: paint order, overlap clipping, top-down hit-test.</summary>
public sealed class OverlayStackTests
{
    private sealed class StubLayer(
        string id,
        Rect bounds,
        char glyph,
        bool opaque = true,
        bool hitTransparent = false,
        bool visible = true) : IOverlayLayer
    {
        public string Id { get; } = id;
        public Rect Bounds { get; set; } = bounds;
        public bool Visible { get; set; } = visible;
        public bool Opaque { get; set; } = opaque;
        public bool HitTransparent { get; set; } = hitTransparent;

        public List<Rect> Clips { get; } = [];

        public void Paint(ScreenBuffer buffer, Rect clip)
        {
            Clips.Add(clip);
            for (int y = clip.Y; y < clip.Bottom; y++)
            {
                for (int x = clip.X; x < clip.Right; x++)
                {
                    buffer.SetRune(x, y, new Rune(glyph), CellStyle.Plain);
                }
            }
        }
    }

    [Test]
    public async Task EmptyStack_PaintOver_LeavesBufferUntouched()
    {
        var stack = new OverlayStack();
        var buffer = new ScreenBuffer(10, 4);

        stack.PaintOver(buffer);

        await Assert.That(buffer.Get(0, 0).Rune).IsEqualTo((int)' ');
        await Assert.That(buffer.Get(9, 3).Rune).IsEqualTo((int)' ');
    }

    [Test]
    public async Task LayoutTree_PaintAll_EmptyOverlays_PaintsPanelsOnly()
    {
        var tree = new LayoutTree();
        tree.AddRoot(new BorderPanel("root", 4, 3, title: "t"));
        tree.Solve(20, 8);

        await Assert.That(tree.Overlays.IsEmpty).IsTrue();

        var buffer = new ScreenBuffer(20, 8);
        tree.PaintAll(buffer);

        await Assert.That(buffer.Get(0, 0).Rune).IsEqualTo((int)'┌');
    }

    [Test]
    public async Task PaintOrder_TopLayerWinsOverlap()
    {
        var stack = new OverlayStack();
        stack.Push(new StubLayer("lower", new Rect(0, 0, 10, 4), 'a'));
        stack.Push(new StubLayer("upper", new Rect(4, 1, 6, 2), 'b'));
        var buffer = new ScreenBuffer(10, 4);

        stack.PaintOver(buffer);

        await Assert.That(buffer.Get(0, 0).Rune).IsEqualTo((int)'a');
        await Assert.That(buffer.Get(5, 2).Rune).IsEqualTo((int)'b');
    }

    [Test]
    public async Task OpaqueLayerAbove_OccludesLower_ClipsLowerFragments()
    {
        var lower = new StubLayer("lower", new Rect(0, 0, 10, 4), 'a');
        var upper = new StubLayer("upper", new Rect(4, 1, 6, 2), 'b', opaque: true);
        var stack = new OverlayStack();
        stack.Push(lower);
        stack.Push(upper);
        var buffer = new ScreenBuffer(10, 4);

        stack.PaintOver(buffer);

        // The occluder must never appear in the lower layer's clips.
        var occluder = new Rect(4, 1, 6, 2);
        long painted = 0;
        foreach (var clip in lower.Clips)
        {
            var overlap = clip.Intersect(occluder);
            await Assert.That(overlap.Area).IsEqualTo(0);
            painted += clip.Area;
        }

        // Union of fragments == bounds minus the occluded intersection.
        long expected = new Rect(0, 0, 10, 4).Area - occluder.Area;
        await Assert.That(painted).IsEqualTo(expected);

        // The top layer itself paints its full bounds in one clip.
        await Assert.That(upper.Clips.Count).IsEqualTo(1);
        await Assert.That(upper.Clips[0]).IsEqualTo(occluder);
    }

    [Test]
    public async Task FullyCoveredByOpaqueSibling_SkipsDrawing()
    {
        // ENG6 #277 (TGui #5360 pattern): a fully-covered opaque overlapped
        // sibling skips drawing entirely — zero Paint calls, not one clipped call.
        var lower = new StubLayer("lower", new Rect(0, 0, 10, 4), 'a');
        var upper = new StubLayer("upper", new Rect(0, 0, 10, 4), 'b', opaque: true);
        var stack = new OverlayStack();
        stack.Push(lower);
        stack.Push(upper);
        var buffer = new ScreenBuffer(10, 4);

        stack.PaintOver(buffer);

        await Assert.That(lower.Clips.Count).IsEqualTo(0);
        await Assert.That(upper.Clips.Count).IsEqualTo(1);
        await Assert.That(buffer.Get(0, 0).Rune).IsEqualTo((int)'b');
        await Assert.That(buffer.Get(9, 3).Rune).IsEqualTo((int)'b');
    }

    [Test]
    public async Task JointlyCoveredByTwoOpaqueSiblings_SkipsDrawing()
    {
        // ENG6 #277: coverage by the UNION of opaque siblings above also culls —
        // neither half-cover alone covers the lower layer, together they do.
        var lower = new StubLayer("lower", new Rect(0, 0, 10, 4), 'a');
        var left = new StubLayer("left", new Rect(0, 0, 5, 4), 'l', opaque: true);
        var right = new StubLayer("right", new Rect(5, 0, 5, 4), 'r', opaque: true);
        var stack = new OverlayStack();
        stack.Push(lower);
        stack.Push(left);
        stack.Push(right);
        var buffer = new ScreenBuffer(10, 4);

        stack.PaintOver(buffer);

        await Assert.That(lower.Clips.Count).IsEqualTo(0);
        await Assert.That(buffer.Get(0, 0).Rune).IsEqualTo((int)'l');
        await Assert.That(buffer.Get(9, 3).Rune).IsEqualTo((int)'r');
    }

    [Test]
    public async Task FullyCoveredByTransparentSibling_StillPaints()
    {
        // ENG6 #277: a non-opaque full cover must NOT cull — the lower layer
        // still paints its full bounds underneath the veil.
        var lower = new StubLayer("lower", new Rect(0, 0, 10, 4), 'a');
        var veil = new StubLayer("veil", new Rect(0, 0, 10, 4), 'b', opaque: false);
        var stack = new OverlayStack();
        stack.Push(lower);
        stack.Push(veil);
        var buffer = new ScreenBuffer(10, 4);

        stack.PaintOver(buffer);

        await Assert.That(lower.Clips.Count).IsEqualTo(1);
        await Assert.That(lower.Clips[0]).IsEqualTo(new Rect(0, 0, 10, 4));
    }

    [Test]
    public async Task TransparentLayerAbove_DoesNotClipLower()
    {
        var lower = new StubLayer("lower", new Rect(0, 0, 10, 4), 'a');
        var veil = new StubLayer("veil", new Rect(4, 1, 6, 2), 'b', opaque: false);
        var stack = new OverlayStack();
        stack.Push(lower);
        stack.Push(veil);
        var buffer = new ScreenBuffer(10, 4);

        stack.PaintOver(buffer);

        await Assert.That(lower.Clips.Count).IsEqualTo(1);
        await Assert.That(lower.Clips[0]).IsEqualTo(new Rect(0, 0, 10, 4));
    }

    [Test]
    public async Task HitTest_TopDown_SkipsTransparentAndHidden()
    {
        var stack = new OverlayStack();
        stack.Push(new StubLayer("bottom", new Rect(0, 0, 10, 4), 'a'));
        stack.Push(new StubLayer("toast", new Rect(4, 1, 6, 2), 'b', hitTransparent: true));
        stack.Push(new StubLayer("hidden", new Rect(4, 1, 6, 2), 'c', visible: false));

        // Hit-transparent + hidden layers fall through to the bottom layer.
        await Assert.That(stack.HitTest(5, 2)!.Id).IsEqualTo("bottom");
        await Assert.That(stack.HitTest(0, 0)!.Id).IsEqualTo("bottom");
        await Assert.That(stack.HitTest(10, 4)).IsNull();
    }

    [Test]
    public async Task HitTest_TopmostOpaque_Wins()
    {
        var stack = new OverlayStack();
        stack.Push(new StubLayer("bottom", new Rect(0, 0, 10, 4), 'a'));
        stack.Push(new StubLayer("dialog", new Rect(4, 1, 6, 2), 'b'));

        await Assert.That(stack.HitTest(5, 2)!.Id).IsEqualTo("dialog");
        await Assert.That(stack.HitTest(0, 0)!.Id).IsEqualTo("bottom");
    }

    [Test]
    public async Task Push_SameId_ReplacesAndMovesToTop()
    {
        var stack = new OverlayStack();
        stack.Push(new StubLayer("a", new Rect(0, 0, 2, 2), 'a'));
        stack.Push(new StubLayer("b", new Rect(0, 0, 2, 2), 'b'));
        stack.Push(new StubLayer("a", new Rect(5, 0, 2, 2), 'A'));

        await Assert.That(stack.Count).IsEqualTo(2);
        await Assert.That(stack.Layers[1].Id).IsEqualTo("a");
        await Assert.That(stack.HitTest(5, 0)!.Bounds).IsEqualTo(new Rect(5, 0, 2, 2));

        await Assert.That(stack.Remove("missing")).IsFalse();
        await Assert.That(stack.Remove("b")).IsTrue();
        await Assert.That(stack.Get("b")).IsNull();
        await Assert.That(stack.Get("a")).IsNotNull();

        stack.Clear();
        await Assert.That(stack.IsEmpty).IsTrue();
    }
}
