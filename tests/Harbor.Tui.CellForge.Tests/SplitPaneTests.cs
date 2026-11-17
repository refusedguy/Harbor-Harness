using System.Text;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Issue #2328: resizable split panes: resize conservation and floors, focus
/// traversal through FocusRouter, and fullscreen through the OverlayLayer.
/// Engine-vocabulary only (EngineCells / EngineInput qualified); chat untouched.
/// </summary>
public sealed class SplitPaneTests
{
    private sealed class GlyphNode(char glyph) : EngineCells.INode
    {
        public EngineCells.Rect Arranged { get; private set; }

        public int Paints { get; private set; }

        public EngineCells.Size Measure(EngineCells.Constraints constraints) =>
            new(Math.Min(6, constraints.Width), Math.Min(3, constraints.Height));

        public void Arrange(EngineCells.Rect rect) => Arranged = rect;

        public void Paint(EngineCells.ScreenBuffer buffer, EngineCells.Rect clip)
        {
            Paints++;
            for (int y = clip.Y; y < clip.Bottom; y++)
            {
                for (int x = clip.X; x < clip.Right; x++)
                {
                    buffer.SetRune(x, y, new Rune(glyph), EngineCells.CellStyle.Plain);
                }
            }
        }
    }

    private static EngineCells.SplitPane Horizontal(params (string Id, int Weight, int Min)[] defs)
    {
        var pane = new EngineCells.SplitPane(EngineCells.SplitDir.Horizontal);
        foreach ((string id, int weight, int min) in defs)
        {
            _ = pane.Add(id, new GlyphNode(id[0]), weight, min);
        }

        return pane;
    }

    private static EngineCells.ScreenBuffer Paint(EngineCells.SplitPane pane, int cols = 10, int rows = 4)
    {
        var buffer = new EngineCells.ScreenBuffer(cols, rows);
        var full = new EngineCells.Rect(0, 0, cols, rows);
        pane.Arrange(full);
        pane.Paint(buffer, full);
        return buffer;
    }

    [Test]
    public async Task ResizeDivider_GrowsPane_ShrinksNeighbour_ConservingTotal()
    {
        var pane = Horizontal(("a", 3, 1), ("b", 3, 1));

        int applied = pane.ResizeDivider(0, 2);

        await Assert.That(applied).IsEqualTo(2);
        await Assert.That(pane.Items[0].Weight).IsEqualTo(5);
        await Assert.That(pane.Items[1].Weight).IsEqualTo(1);
        await Assert.That(pane.Items[0].Weight + pane.Items[1].Weight).IsEqualTo(6);
    }

    [Test]
    public async Task ResizeDivider_ClampsAtNeighbourFloor()
    {
        var pane = Horizontal(("a", 2, 1), ("b", 2, 1));

        _ = pane.ResizeDivider(0, 10);
        int applied = pane.ResizeDivider(0, 10);

        await Assert.That(pane.Items[1].Weight).IsEqualTo(1);
        await Assert.That(applied).IsEqualTo(0);
        await Assert.That(pane.Items[0].Weight).IsEqualTo(3);
    }

    [Test]
    public async Task ResizeById_LastPaneGrowsFromPreviousDivider()
    {
        var pane = Horizontal(("a", 3, 1), ("b", 3, 1), ("c", 3, 1));

        int applied = pane.Resize("c", 2);

        await Assert.That(applied).IsEqualTo(2);
        await Assert.That(pane.Items[0].Weight).IsEqualTo(3);
        await Assert.That(pane.Items[1].Weight).IsEqualTo(1);
        await Assert.That(pane.Items[2].Weight).IsEqualTo(5);
    }

    [Test]
    public async Task Resize_UnknownId_IsNoOp()
    {
        var pane = Horizontal(("a", 3, 1), ("b", 3, 1));

        await Assert.That(pane.Resize("missing", 2)).IsEqualTo(0);
        await Assert.That(pane.Items[0].Weight).IsEqualTo(3);
    }

    [Test]
    public async Task Focus_NextAndPrevious_WrapAndMoveFocusFlag()
    {
        var pane = Horizontal(("a", 1, 1), ("b", 1, 1), ("c", 1, 1));

        await Assert.That(pane.FocusNext()).IsTrue();
        await Assert.That(pane.Focused!.Id).IsEqualTo("a");
        await Assert.That(pane.FocusNext()).IsTrue();
        await Assert.That(pane.Focused!.Id).IsEqualTo("b");

        await Assert.That(pane.FocusPrevious()).IsTrue();
        await Assert.That(pane.Focused!.Id).IsEqualTo("a");

        await Assert.That(pane.Focus("c")).IsTrue();
        await Assert.That(pane.Focused!.Id).IsEqualTo("c");
        await Assert.That(pane.Items[2].IsFocused).IsTrue();
        await Assert.That(pane.Items[0].IsFocused).IsFalse();

        await Assert.That(pane.FocusNext()).IsTrue();
        await Assert.That(pane.Focused!.Id).IsEqualTo("a");
        await Assert.That(pane.Items[2].IsFocused).IsFalse();
    }

    [Test]
    public async Task HandleKey_RoutesFocusFullscreenAndResize()
    {
        var pane = Horizontal(("a", 3, 1), ("b", 3, 1));
        _ = pane.FocusNext();
        await Assert.That(pane.Focused!.Id).IsEqualTo("a");

        await Assert.That(pane.HandleKey(EngineInput.KeyEvent.Simple(EngineInput.KeyCode.Tab))).IsTrue();
        await Assert.That(pane.Focused!.Id).IsEqualTo("b");
        await Assert.That(pane.HandleKey(EngineInput.KeyEvent.Simple(
            EngineInput.KeyCode.Tab, EngineInput.KeyModifiers.Shift))).IsTrue();
        await Assert.That(pane.Focused!.Id).IsEqualTo("a");

        await Assert.That(pane.HandleKey(EngineInput.KeyEvent.Char(new Rune('=')))).IsTrue();
        await Assert.That(pane.Items[0].Weight).IsEqualTo(4);

        await Assert.That(pane.HandleKey(EngineInput.KeyEvent.Char(new Rune('f')))).IsTrue();
        await Assert.That(pane.IsFullscreen).IsTrue();
        await Assert.That(pane.FullscreenItem!.Id).IsEqualTo("a");

        await Assert.That(pane.HandleKey(EngineInput.KeyEvent.Char(new Rune('f')))).IsTrue();
        await Assert.That(pane.IsFullscreen).IsFalse();
    }

    [Test]
    public async Task HandleKey_UnboundKey_FallsThrough()
    {
        var pane = Horizontal(("a", 1, 1), ("b", 1, 1));

        await Assert.That(pane.HandleKey(EngineInput.KeyEvent.Char(new Rune('z')))).IsFalse();
    }

    [Test]
    public async Task Fullscreen_Toggle_PaintsFocusedPaneAcrossViewport()
    {
        var pane = Horizontal(("a", 1, 1), ("b", 1, 1));
        var split = Paint(pane);
        await Assert.That(split.Get(0, 0).Rune).IsEqualTo((int)'a');
        await Assert.That(split.Get(9, 3).Rune).IsEqualTo((int)'b');

        _ = pane.Focus("a");
        await Assert.That(pane.ToggleFullscreen()).IsTrue();
        var full = Paint(pane);
        for (int x = 0; x < 10; x++)
        {
            await Assert.That(full.Get(x, 0).Rune).IsEqualTo((int)'a');
            await Assert.That(full.Get(x, 3).Rune).IsEqualTo((int)'a');
        }

        await Assert.That(pane.ExitFullscreen()).IsFalse();
        var restored = Paint(pane);
        await Assert.That(restored.Get(0, 0).Rune).IsEqualTo((int)'a');
        await Assert.That(restored.Get(9, 3).Rune).IsEqualTo((int)'b');
    }

    [Test]
    public async Task Fullscreen_WithoutFocus_DoesNothing()
    {
        var pane = Horizontal(("a", 1, 1), ("b", 1, 1));

        await Assert.That(pane.ToggleFullscreen()).IsFalse();
        await Assert.That(pane.IsFullscreen).IsFalse();
        await Assert.That(pane.FullscreenLayer.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Arrange_SplitsByWeightRatio()
    {
        var pane = Horizontal(("a", 3, 1), ("b", 1, 1));
        pane.Arrange(new EngineCells.Rect(0, 0, 80, 24));

        await Assert.That(((GlyphNode)pane.Items[0].Content).Arranged.Width).IsEqualTo(60);
        await Assert.That(((GlyphNode)pane.Items[1].Content).Arranged.Width).IsEqualTo(20);
    }

    [Test]
    public async Task Arrange_RespectsMinimumWeights()
    {
        var pane = Horizontal(("a", 50, 10), ("b", 1, 10));
        pane.Arrange(new EngineCells.Rect(0, 0, 20, 4));

        await Assert.That(((GlyphNode)pane.Items[0].Content).Arranged.Width).IsGreaterThanOrEqualTo(10);
        await Assert.That(((GlyphNode)pane.Items[1].Content).Arranged.Width).IsGreaterThanOrEqualTo(10);
    }

    [Test]
    public async Task Measure_ClampsToConstraints()
    {
        var pane = Horizontal(("a", 1, 1), ("b", 1, 1));

        EngineCells.Size want = pane.Measure(new EngineCells.Constraints(80, 24));

        await Assert.That(want.Width).IsLessThanOrEqualTo(80);
        await Assert.That(want.Height).IsLessThanOrEqualTo(24);
    }

    [Test]
    public async Task Remove_ClearsFullscreenAndFocus()
    {
        var pane = Horizontal(("a", 1, 1), ("b", 1, 1));
        _ = pane.Focus("a");
        _ = pane.ToggleFullscreen();

        await Assert.That(pane.Remove("a")).IsTrue();
        await Assert.That(pane.IsFullscreen).IsFalse();
        await Assert.That(pane.Items.Count).IsEqualTo(1);
        await Assert.That(pane.FocusRouter.Order.Count).IsEqualTo(1);
        await Assert.That(pane.Remove("missing")).IsFalse();
    }
}
