using System.Text;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>ENG4 (#275) modal barrier + key routing + Clear-popup pattern.</summary>
public sealed class OverlayModalTests
{
    private sealed class StubLayer(
        string id,
        Rect bounds,
        char glyph,
        bool opaque = true,
        bool hitTransparent = false,
        bool visible = true,
        bool modal = false,
        KeyCode? consumes = null) : IOverlayLayer
    {
        public string Id { get; } = id;
        public Rect Bounds { get; set; } = bounds;
        public bool Visible { get; set; } = visible;
        public bool Opaque { get; set; } = opaque;
        public bool HitTransparent { get; set; } = hitTransparent;
        public bool IsModal { get; set; } = modal;

        public List<KeyEvent> SeenKeys { get; } = [];

        public bool OnKey(in KeyEvent key)
        {
            SeenKeys.Add(key);
            return consumes.HasValue && key.Key == consumes.Value;
        }

        public void Paint(ScreenBuffer buffer, Rect clip)
        {
            for (int y = clip.Y; y < clip.Bottom; y++)
            {
                for (int x = clip.X; x < clip.Right; x++)
                {
                    buffer.SetRune(x, y, new Rune(glyph), CellStyle.Plain);
                }
            }
        }
    }

    private static readonly KeyEvent Esc = KeyEvent.Simple(KeyCode.Escape);
    private static readonly KeyEvent Enter = KeyEvent.Simple(KeyCode.Enter);

    [Test]
    public async Task IsModal_DefaultsFalse_NoBarrier()
    {
        var stack = new OverlayStack();
        stack.Push(new StubLayer("toast", new Rect(0, 0, 10, 4), 't'));

        await Assert.That(stack.TopModal).IsNull();
        await Assert.That(stack.HasModalBarrier).IsFalse();
    }

    [Test]
    public async Task ModalBarrier_ActiveWhileVisible()
    {
        var stack = new OverlayStack();
        var dialog = new StubLayer("dialog", new Rect(4, 1, 6, 2), 'd', modal: true);
        stack.Push(new StubLayer("base", new Rect(0, 0, 10, 4), 'a'));
        stack.Push(dialog);

        await Assert.That(stack.TopModal!.Id).IsEqualTo("dialog");
        await Assert.That(stack.HasModalBarrier).IsTrue();

        dialog.Visible = false;
        await Assert.That(stack.TopModal).IsNull();
        await Assert.That(stack.HasModalBarrier).IsFalse();

        dialog.Visible = true;
        await Assert.That(stack.HasModalBarrier).IsTrue();
        stack.Remove("dialog");
        await Assert.That(stack.HasModalBarrier).IsFalse();
    }

    [Test]
    public async Task RouteKey_TopDown_FirstConsumerWins()
    {
        var lower = new StubLayer("lower", new Rect(0, 0, 10, 4), 'a', consumes: KeyCode.Enter);
        var upper = new StubLayer("upper", new Rect(0, 0, 10, 4), 'b', consumes: KeyCode.Enter);
        var stack = new OverlayStack();
        stack.Push(lower);
        stack.Push(upper);

        await Assert.That(stack.RouteKey(in Enter)).IsTrue();
        await Assert.That(lower.SeenKeys.Count).IsEqualTo(0);
        await Assert.That(upper.SeenKeys.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RouteKey_StopsAtModal_PanelsStarve()
    {
        var bottom = new StubLayer("bottom", new Rect(0, 0, 10, 4), 'a', consumes: KeyCode.Escape);
        var dialog = new StubLayer("dialog", new Rect(4, 1, 6, 2), 'd', modal: true);
        var stack = new OverlayStack();
        stack.Push(bottom);
        stack.Push(dialog);

        // Modal ignores Esc: unhandled, but the barrier holds — the host must
        // swallow the key so panels beneath starve.
        await Assert.That(stack.RouteKey(in Esc)).IsFalse();
        await Assert.That(bottom.SeenKeys.Count).IsEqualTo(0);
        await Assert.That(stack.HasModalBarrier).IsTrue();
    }

    [Test]
    public async Task RouteKey_ModalConsumer_HandlesKey()
    {
        var stack = new OverlayStack();
        stack.Push(new StubLayer("bottom", new Rect(0, 0, 10, 4), 'a', consumes: KeyCode.Escape));
        stack.Push(new StubLayer("dialog", new Rect(4, 1, 6, 2), 'd', modal: true, consumes: KeyCode.Escape));

        await Assert.That(stack.RouteKey(in Esc)).IsTrue();
    }

    [Test]
    public async Task RouteKey_SkipsHidden_AndEmpty()
    {
        var stack = new OverlayStack();
        await Assert.That(stack.RouteKey(in Esc)).IsFalse();

        var hidden = new StubLayer("hidden", new Rect(0, 0, 10, 4), 'h', visible: false, consumes: KeyCode.Escape);
        var bottom = new StubLayer("bottom", new Rect(0, 0, 10, 4), 'a');
        stack.Push(bottom);
        stack.Push(hidden);

        await Assert.That(stack.RouteKey(in Esc)).IsFalse();
        await Assert.That(hidden.SeenKeys.Count).IsEqualTo(0);
        await Assert.That(bottom.SeenKeys.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CenteredRect_CentersAndClamps()
    {
        var screen = new Rect(0, 0, 80, 24);

        await Assert.That(OverlayPopup.CenteredRect(screen, 20, 10)).IsEqualTo(new Rect(30, 7, 20, 10));
        await Assert.That(OverlayPopup.CenteredRect(screen, 200, 100)).IsEqualTo(screen);
        await Assert.That(OverlayPopup.CenteredRect(screen, 0, 10).Area).IsEqualTo(0);
        await Assert.That(OverlayPopup.CenteredRect(screen, 20, -1).Area).IsEqualTo(0);
    }

    [Test]
    public async Task Clear_BlanksFootprint_LeavesOutside()
    {
        var stack = new OverlayStack();
        stack.Push(new StubLayer("base", new Rect(0, 0, 10, 4), 'a'));
        var buffer = new ScreenBuffer(10, 4);
        stack.PaintOver(buffer);
        await Assert.That(buffer.Get(5, 2).Rune).IsEqualTo((int)'a');

        var popup = new Rect(4, 1, 6, 2);
        OverlayPopup.Clear(buffer, popup);

        await Assert.That(buffer.Get(5, 2).Rune).IsEqualTo((int)' ');
        await Assert.That(buffer.Get(0, 0).Rune).IsEqualTo((int)'a');
    }
}
