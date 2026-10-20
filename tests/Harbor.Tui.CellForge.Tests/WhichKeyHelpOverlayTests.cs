using System.Text;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>PRIM11 (#306) which-key help overlay: hidden no-op, hotkey rows, context section, z-stack seating.</summary>
public sealed class WhichKeyHelpOverlayTests
{
    private sealed class StubLayer(string id, Rect bounds, char glyph) : IOverlayLayer
    {
        public string Id { get; } = id;
        public Rect Bounds { get; } = bounds;
        public bool Visible => true;
        public bool Opaque => true;
        public bool HitTransparent => false;

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

    [Test]
    public async Task Hidden_Paint_LeavesBufferUntouched()
    {
        var overlay = new WhichKeyHelpOverlay();
        var buffer = TestRender.Buffer(80, 24);

        overlay.Paint(buffer, new Rect(0, 0, 80, 24));

        await Assert.That(TestRender.Art(buffer)).DoesNotContain("Which-key");
        await Assert.That(buffer.Get(40, 12).Rune).IsEqualTo((int)' ');
    }

    [Test]
    public async Task Show_PaintsTitleAndHotkeyRows()
    {
        var overlay = new WhichKeyHelpOverlay();
        overlay.Show();
        var buffer = TestRender.Buffer(80, 24);

        overlay.Paint(buffer, new Rect(0, 0, 80, 24));

        string art = TestRender.Art(buffer);
        await Assert.That(art).Contains("Which-key");
        await Assert.That(art).Contains("Hotkeys");
        await Assert.That(art).Contains("Alt+1..9");
        await Assert.That(art).Contains("Ctrl+L");
        await Assert.That(art).Contains("Press ? to close.");
        await Assert.That(art).Contains("╭");
    }

    [Test]
    public async Task NoContext_HidesContextSection()
    {
        var overlay = new WhichKeyHelpOverlay();
        overlay.Show();
        var buffer = TestRender.Buffer(80, 24);

        overlay.Paint(buffer, new Rect(0, 0, 80, 24));

        await Assert.That(TestRender.Art(buffer)).DoesNotContain("Context");
    }

    [Test]
    public async Task WithContext_ShowsFocusAndOverlay()
    {
        var overlay = new WhichKeyHelpOverlay();
        overlay.Show(new WhichKeyContext("timeline", "palette"));
        var buffer = TestRender.Buffer(80, 24);

        overlay.Paint(buffer, new Rect(0, 0, 80, 24));

        string art = TestRender.Art(buffer);
        await Assert.That(art).Contains("Context");
        await Assert.That(art).Contains("timeline");
        await Assert.That(art).Contains("palette");
    }

    [Test]
    public async Task ComputeBox_CenteredAndClamped()
    {
        var overlay = new WhichKeyHelpOverlay();
        overlay.Show();
        var viewport = new Rect(0, 0, 80, 24);

        var box = overlay.ComputeBox(viewport);

        await Assert.That(box.Width).IsGreaterThanOrEqualTo(WhichKeyHelpOverlay.MinWidth);
        await Assert.That(box.Width).IsLessThanOrEqualTo(WhichKeyHelpOverlay.MaxWidth);
        await Assert.That(box.Height).IsGreaterThanOrEqualTo(WhichKeyHelpOverlay.MinHeight);
        await Assert.That(box.X).IsEqualTo((viewport.Width - box.Width) / 2);
        await Assert.That(box.Y).IsEqualTo((viewport.Height - box.Height) / 2);
        await Assert.That(box.Right).IsLessThanOrEqualTo(viewport.Right);
        await Assert.That(box.Bottom).IsLessThanOrEqualTo(viewport.Bottom);
    }

    [Test]
    public async Task TinyViewport_PaintStaysNoop()
    {
        var overlay = new WhichKeyHelpOverlay();
        overlay.Show();
        var buffer = TestRender.Buffer(10, 4);

        overlay.Paint(buffer, new Rect(0, 0, 10, 4));

        await Assert.That(TestRender.Art(buffer)).DoesNotContain("Which-key");
    }

    [Test]
    public async Task HandleKey_EscAndQuestion_Dismiss()
    {
        var overlay = new WhichKeyHelpOverlay();
        overlay.Show();

        await Assert.That(overlay.HandleKey(new ConsoleKeyInfo('a', ConsoleKey.A, false, false, false))).IsFalse();
        await Assert.That(overlay.Visible).IsTrue();

        await Assert.That(overlay.HandleKey(new ConsoleKeyInfo('?', ConsoleKey.Oem2, true, false, false))).IsTrue();
        await Assert.That(overlay.Visible).IsFalse();

        overlay.Show();
        await Assert.That(overlay.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false))).IsTrue();
        await Assert.That(overlay.Visible).IsFalse();

        await Assert.That(overlay.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false))).IsFalse();
    }

    [Test]
    public async Task Layer_OnStack_PaintsOverLower_HitTestWins()
    {
        var overlay = new WhichKeyHelpOverlay();
        overlay.Show();
        var layer = new WhichKeyHelpOverlayLayer(overlay);
        layer.Sync(new Rect(0, 0, 80, 24));

        await Assert.That(layer.Id).IsEqualTo(WhichKeyHelpOverlayLayer.LayerId);
        await Assert.That(layer.Opaque).IsTrue();
        await Assert.That(layer.HitTransparent).IsFalse();
        await Assert.That(layer.Visible).IsTrue();

        var stack = new OverlayStack();
        stack.Push(new StubLayer("base", new Rect(0, 0, 80, 24), 'a'));
        stack.Push(layer);
        var buffer = TestRender.Buffer(80, 24);

        stack.PaintOver(buffer);

        await Assert.That(TestRender.Art(buffer)).Contains("Which-key");
        await Assert.That(stack.HitTest(layer.Bounds.X + 1, layer.Bounds.Y + 1)!.Id).IsEqualTo("whichkey");
        await Assert.That(stack.HitTest(0, 0)!.Id).IsEqualTo("base");

        overlay.Hide();
        await Assert.That(layer.Visible).IsFalse();
        await Assert.That(stack.HitTest(layer.Bounds.X + 1, layer.Bounds.Y + 1)!.Id).IsEqualTo("base");
    }

    [Test]
    public async Task Layer_Hidden_StaysNoopOnStack()
    {
        var layer = new WhichKeyHelpOverlayLayer(new WhichKeyHelpOverlay());
        layer.Sync(new Rect(0, 0, 80, 24));

        await Assert.That(layer.Visible).IsFalse();

        var stack = new OverlayStack();
        stack.Push(new StubLayer("base", new Rect(0, 0, 80, 24), 'a'));
        stack.Push(layer);
        var buffer = TestRender.Buffer(80, 24);

        stack.PaintOver(buffer);

        await Assert.That(TestRender.Art(buffer)).DoesNotContain("Which-key");
        await Assert.That(buffer.Get(40, 12).Rune).IsEqualTo((int)'a');
    }

    [Test]
    public async Task NoLeaderBindings_HidesLeaderSection()
    {
        var overlay = new WhichKeyHelpOverlay();
        overlay.Show();
        var buffer = TestRender.Buffer(80, 24);

        overlay.Paint(buffer, new Rect(0, 0, 80, 24));

        await Assert.That(TestRender.Art(buffer)).DoesNotContain("Leader");
        await Assert.That(TestRender.Art(buffer)).DoesNotContain("pending ctrl+x");
    }

    [Test]
    public async Task LeaderBindings_RenderGroupedRows()
    {
        var overlay = new WhichKeyHelpOverlay();
        overlay.SetLeaderBindings([
            new LeaderEntry("session", 's', "save session"),
            new LeaderEntry("session", 'l', "list sessions"),
            new LeaderEntry("panel", 'p', "toggle panel"),
        ]);
        overlay.Show();
        var buffer = TestRender.Buffer(80, 32);

        overlay.Paint(buffer, new Rect(0, 0, 80, 32));

        string art = TestRender.Art(buffer);
        await Assert.That(art).Contains("Leader");
        await Assert.That(art).Contains("session");
        await Assert.That(art).Contains("panel");
        await Assert.That(art).Contains("save session");
        await Assert.That(art).Contains("ctrl+x s");
        await Assert.That(art).Contains("Press ? to close.");
    }

    [Test]
    public async Task LeaderBindings_Null_ClearsRows()
    {
        var overlay = new WhichKeyHelpOverlay();
        overlay.SetLeaderBindings([new LeaderEntry("session", 's', "save session")]);
        overlay.SetLeaderBindings(null);
        overlay.Show();
        var buffer = TestRender.Buffer(80, 24);

        overlay.Paint(buffer, new Rect(0, 0, 80, 24));

        await Assert.That(TestRender.Art(buffer)).DoesNotContain("Leader");
        await Assert.That(overlay.LeaderBindings.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PendingChord_ShowsPreview_AndClears()
    {
        var overlay = new WhichKeyHelpOverlay();
        overlay.Show();
        overlay.SetPending('G');

        await Assert.That(overlay.PendingChord.HasValue).IsTrue();
        await Assert.That(overlay.PendingChord.Value).IsEqualTo('g');

        var buffer = TestRender.Buffer(80, 24);
        overlay.Paint(buffer, new Rect(0, 0, 80, 24));
        await Assert.That(TestRender.Art(buffer)).Contains("pending ctrl+x g");

        overlay.SetPending(null);
        await Assert.That(overlay.PendingChord.HasValue).IsFalse();

        var cleared = TestRender.Buffer(80, 24);
        overlay.Paint(cleared, new Rect(0, 0, 80, 24));
        await Assert.That(TestRender.Art(cleared)).DoesNotContain("pending ctrl+x");
    }
}
