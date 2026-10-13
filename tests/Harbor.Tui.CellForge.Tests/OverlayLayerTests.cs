using System.Text;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// TR3 (#1222, epic #1195): generalized overlay/z-layer primitive — z-order,
/// overlap clipping, hit-test, modal barrier, portal arrange, popup geometry.
/// Engine-vocabulary only (EngineCells-qualified); chat untouched.
/// </summary>
public sealed class OverlayLayerTests
{
    private sealed class GlyphNode(char glyph) : EngineCells.INode
    {
        public EngineCells.Rect Arranged { get; private set; }

        public List<EngineCells.Rect> Clips { get; } = [];

        public EngineCells.Size Measure(EngineCells.Constraints constraints) =>
            new(Math.Min(4, constraints.Width), Math.Min(2, constraints.Height));

        public void Arrange(EngineCells.Rect rect) => Arranged = rect;

        public void Paint(EngineCells.ScreenBuffer buffer, EngineCells.Rect clip)
        {
            Clips.Add(clip);
            for (int y = clip.Y; y < clip.Bottom; y++)
            {
                for (int x = clip.X; x < clip.Right; x++)
                {
                    buffer.SetRune(x, y, new Rune(glyph), EngineCells.CellStyle.Plain);
                }
            }
        }
    }

    /// <summary>
    /// #1204: sparse popover — paints a single dot, leaves the rest of its
    /// footprint untouched (like a dialog that draws border + a few rows).
    /// Proves the pipeline Clear blanks the footprint first.
    /// </summary>
    private sealed class SparseNode(char glyph) : EngineCells.INode
    {
        public EngineCells.Rect Arranged { get; private set; }

        public EngineCells.Size Measure(EngineCells.Constraints constraints) =>
            new(Math.Min(4, constraints.Width), Math.Min(2, constraints.Height));

        public void Arrange(EngineCells.Rect rect) => Arranged = rect;

        public void Paint(EngineCells.ScreenBuffer buffer, EngineCells.Rect clip)
        {
            buffer.SetRune(clip.X, clip.Y, new Rune(glyph), EngineCells.CellStyle.Plain);
        }
    }

    private static EngineCells.ZLayer Layer(
        string id,
        EngineCells.Rect bounds,
        char glyph,
        int z = 0,
        bool modal = false,
        bool opaque = true,
        bool hitTransparent = false,
        bool visible = true) =>
        new(id, new GlyphNode(glyph), bounds, z, modal)
        {
            Opaque = opaque,
            HitTransparent = hitTransparent,
            Visible = visible,
        };

    private static EngineCells.ScreenBuffer Painted(EngineCells.OverlayLayer overlays, int cols = 10, int rows = 4)
    {
        var buffer = new EngineCells.ScreenBuffer(cols, rows);
        overlays.Arrange(new EngineCells.Rect(0, 0, cols, rows));
        overlays.Paint(buffer, new EngineCells.Rect(0, 0, cols, rows));
        return buffer;
    }

    [Test]
    public async Task Empty_Paint_LeavesBufferUntouched()
    {
        var overlays = new EngineCells.OverlayLayer();

        await Assert.That(overlays.IsEmpty).IsTrue();
        var buffer = Painted(overlays);

        await Assert.That(buffer.Get(0, 0).Rune).IsEqualTo((int)' ');
        await Assert.That(buffer.Get(9, 3).Rune).IsEqualTo((int)' ');
    }

    [Test]
    public async Task Push_SameId_ReplacesAndReseats()
    {
        var overlays = new EngineCells.OverlayLayer();
        overlays.Push(Layer("a", new EngineCells.Rect(0, 0, 4, 2), 'a', z: 0));
        overlays.Push(Layer("b", new EngineCells.Rect(0, 0, 4, 2), 'b', z: 5));
        overlays.Push(Layer("a", new EngineCells.Rect(0, 0, 4, 2), 'A', z: 9));

        await Assert.That(overlays.Count).IsEqualTo(2);
        await Assert.That(overlays.Layers[0].Id).IsEqualTo("b");
        await Assert.That(overlays.Layers[1].Id).IsEqualTo("a");
        await Assert.That(overlays.Get("a")).IsNotNull();
    }

    [Test]
    public async Task ZOrder_HigherPaintsLast_RegardlessOfPushOrder()
    {
        var overlays = new EngineCells.OverlayLayer();
        overlays.Push(Layer("top", new EngineCells.Rect(4, 1, 6, 2), 'b', z: 10));
        overlays.Push(Layer("base", new EngineCells.Rect(0, 0, 10, 4), 'a', z: 0));
        var buffer = Painted(overlays);

        await Assert.That(buffer.Get(0, 0).Rune).IsEqualTo((int)'a');
        await Assert.That(buffer.Get(5, 2).Rune).IsEqualTo((int)'b');
    }

    [Test]
    public async Task OpaqueAbove_OccludesLowerFragments()
    {
        var lower = Layer("lower", new EngineCells.Rect(0, 0, 10, 4), 'a');
        var overlays = new EngineCells.OverlayLayer();
        overlays.Push(lower);
        overlays.Push(Layer("upper", new EngineCells.Rect(4, 1, 6, 2), 'b', z: 1, opaque: true));
        Painted(overlays);

        var occluder = new EngineCells.Rect(4, 1, 6, 2);
        var node = (GlyphNode)lower.Content;
        foreach (var clip in node.Clips)
        {
            await Assert.That(clip.Intersect(occluder).Area).IsEqualTo(0);
        }

        await Assert.That(node.Clips.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task NonOpaqueAbove_LetsLowerPaintThrough()
    {
        var lower = Layer("lower", new EngineCells.Rect(0, 0, 10, 4), 'a');
        var overlays = new EngineCells.OverlayLayer();
        overlays.Push(lower);
        overlays.Push(Layer("veil", new EngineCells.Rect(4, 1, 6, 2), 'v', z: 1, opaque: false));
        var buffer = Painted(overlays);

        // The veil paints last, but the lower entry painted underneath it first.
        var node = (GlyphNode)lower.Content;
        long painted = 0;
        foreach (var clip in node.Clips)
        {
            painted += clip.Area;
        }

        await Assert.That(painted).IsEqualTo(40);
        await Assert.That(buffer.Get(5, 2).Rune).IsEqualTo((int)'v');
    }

    [Test]
    public async Task ModalBarrier_ActiveWhileVisible()
    {
        var overlays = new EngineCells.OverlayLayer();
        var dialog = Layer("dialog", new EngineCells.Rect(4, 1, 6, 2), 'd', z: 5, modal: true);
        overlays.Push(Layer("base", new EngineCells.Rect(0, 0, 10, 4), 'a'));
        overlays.Push(dialog);

        await Assert.That(overlays.TopModal!.Id).IsEqualTo("dialog");
        await Assert.That(overlays.HasModalBarrier).IsTrue();

        dialog.Visible = false;
        await Assert.That(overlays.TopModal).IsNull();
        await Assert.That(overlays.HasModalBarrier).IsFalse();

        dialog.Visible = true;
        await Assert.That(overlays.HasModalBarrier).IsTrue();
        overlays.Remove("dialog");
        await Assert.That(overlays.HasModalBarrier).IsFalse();
    }

    [Test]
    public async Task RouteKey_TopDown_StopsAtModal()
    {
        var bottom = Layer("bottom", new EngineCells.Rect(0, 0, 10, 4), 'a');
        var dialog = Layer("dialog", new EngineCells.Rect(4, 1, 6, 2), 'd', z: 5, modal: true);
        var overlays = new EngineCells.OverlayLayer();
        overlays.Push(bottom);
        overlays.Push(dialog);

        var seen = new List<string>();
        bool consumed = overlays.RouteKey(l =>
        {
            seen.Add(l.Id);
            return false;
        });

        await Assert.That(consumed).IsFalse();
        await Assert.That(seen).IsEquivalentTo(new[] { "dialog" });
        await Assert.That(overlays.HasModalBarrier).IsTrue();
    }

    [Test]
    public async Task RouteKey_FirstConsumerWins_SkipsHidden()
    {
        var overlays = new EngineCells.OverlayLayer();
        overlays.Push(Layer("lower", new EngineCells.Rect(0, 0, 10, 4), 'a'));
        overlays.Push(Layer("hidden", new EngineCells.Rect(0, 0, 10, 4), 'h', z: 5, visible: false));
        overlays.Push(Layer("upper", new EngineCells.Rect(0, 0, 10, 4), 'b', z: 9));

        var seen = new List<string>();
        bool consumed = overlays.RouteKey(l =>
        {
            seen.Add(l.Id);
            return l.Id == "lower";
        });

        await Assert.That(consumed).IsTrue();
        await Assert.That(seen).IsEquivalentTo(new[] { "upper", "lower" });
    }

    [Test]
    public async Task HitTest_TopmostWins_TransparentFallsThrough()
    {
        var overlays = new EngineCells.OverlayLayer();
        overlays.Push(Layer("base", new EngineCells.Rect(0, 0, 10, 4), 'a'));
        overlays.Push(Layer("toast", new EngineCells.Rect(4, 1, 6, 2), 't', z: 5, hitTransparent: true));
        overlays.Push(Layer("hidden", new EngineCells.Rect(0, 0, 10, 4), 'h', z: 9, visible: false));

        await Assert.That(overlays.HitTest(5, 2)!.Id).IsEqualTo("base");
        await Assert.That(overlays.HitTest(0, 0)!.Id).IsEqualTo("base");
        await Assert.That(overlays.HitTest(50, 50)).IsNull();
    }

    [Test]
    public async Task Arrange_SeatsContentAtBounds_PortalSemantics()
    {
        var overlays = new EngineCells.OverlayLayer();
        var toast = Layer("toast", new EngineCells.Rect(6, 2, 4, 2), 't', z: 3);
        overlays.Push(Layer("base", new EngineCells.Rect(0, 0, 10, 4), 'a'));
        overlays.Push(toast);
        overlays.Arrange(new EngineCells.Rect(0, 0, 10, 4));

        await Assert.That(toast.Content.Arranged).IsEqualTo(new EngineCells.Rect(6, 2, 4, 2));
    }

    [Test]
    public async Task CenteredRect_CentersAndClamps()
    {
        var screen = new EngineCells.Rect(0, 0, 80, 24);

        await Assert.That(EngineCells.OverlayGeometry.CenteredRect(screen, 20, 10))
            .IsEqualTo(new EngineCells.Rect(30, 7, 20, 10));
        await Assert.That(EngineCells.OverlayGeometry.CenteredRect(screen, 200, 100)).IsEqualTo(screen);
        await Assert.That(EngineCells.OverlayGeometry.CenteredRect(screen, 0, 10).Area).IsEqualTo(0);
    }

    [Test]
    public async Task Clear_BlanksFootprint_LeavesOutside()
    {
        var overlays = new EngineCells.OverlayLayer();
        overlays.Push(Layer("base", new EngineCells.Rect(0, 0, 10, 4), 'a'));
        var buffer = Painted(overlays);
        await Assert.That(buffer.Get(5, 2).Rune).IsEqualTo((int)'a');

        EngineCells.OverlayGeometry.Clear(buffer, new EngineCells.Rect(4, 1, 6, 2));

        await Assert.That(buffer.Get(5, 2).Rune).IsEqualTo((int)' ');
        await Assert.That(buffer.Get(0, 0).Rune).IsEqualTo((int)'a');
    }

    [Test]
    public async Task Opaque_SparseContent_ClearsFootprintBeforePaint()
    {
        // Production order: base layout already painted into the buffer,
        // then the overlay floats over it. Pre-fill with 'a' so a missing
        // Clear leaks visibly.
        var buffer = new EngineCells.ScreenBuffer(10, 4);
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 10; x++)
            {
                buffer.SetRune(x, y, new Rune('a'), EngineCells.CellStyle.Plain);
            }
        }

        var overlays = new EngineCells.OverlayLayer();
        overlays.Push(new EngineCells.ZLayer(
            "popup",
            new SparseNode('b'),
            new EngineCells.Rect(4, 1, 6, 2),
            z: 1));
        overlays.Arrange(new EngineCells.Rect(0, 0, 10, 4));
        overlays.Paint(buffer, new EngineCells.Rect(0, 0, 10, 4));

        // Painted dot wins; the rest of the opaque footprint is blanked,
        // not leaked from the base layer.
        await Assert.That(buffer.Get(4, 1).Rune).IsEqualTo((int)'b');
        await Assert.That(buffer.Get(5, 1).Rune).IsEqualTo((int)' ');
        await Assert.That(buffer.Get(5, 2).Rune).IsEqualTo((int)' ');
        await Assert.That(buffer.Get(0, 0).Rune).IsEqualTo((int)'a');
    }

    [Test]
    public async Task Transparent_SparseContent_PreservesBase()
    {
        var buffer = new EngineCells.ScreenBuffer(10, 4);
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 10; x++)
            {
                buffer.SetRune(x, y, new Rune('a'), EngineCells.CellStyle.Plain);
            }
        }

        var overlays = new EngineCells.OverlayLayer();
        overlays.Push(new EngineCells.ZLayer(
            "toast",
            new SparseNode('t'),
            new EngineCells.Rect(4, 1, 6, 2),
            z: 1)
        {
            Opaque = false,
            HitTransparent = true,
        });
        overlays.Arrange(new EngineCells.Rect(0, 0, 10, 4));
        overlays.Paint(buffer, new EngineCells.Rect(0, 0, 10, 4));

        await Assert.That(buffer.Get(4, 1).Rune).IsEqualTo((int)'t');
        await Assert.That(buffer.Get(5, 1).Rune).IsEqualTo((int)'a');
    }
}
