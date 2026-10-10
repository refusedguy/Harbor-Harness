using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Screenshot-markup overlay (KILLER_FEATURES §2.7 Feature 14, issue #400
/// slice 1/2): wireframe paint per primitive at a known rect, resize
/// re-projection, key/mouse routing into store messages, and layer seating.
/// The overlay keeps no session — every test drives it through
/// <c>Sync</c> snapshots.
/// </summary>
public class MarkupOverlayTests
{
    private static MarkupOverlayState OpenState() =>
        MarkupOverlayState.Open("/shots/broken.png", "broken.png", 800, 600, scrollOffset: 0);

    private static MarkupOverlay Shown(MarkupOverlayState snapshot)
    {
        var overlay = new MarkupOverlay();
        overlay.Sync(snapshot);
        return overlay;
    }

    private static string PaintArt(MarkupOverlay overlay, int cols = 80, int rows = 24)
    {
        var buffer = new ScreenBuffer(cols, rows);
        overlay.Paint(buffer, new Rect(0, 0, cols, rows));
        return GridDump.Art(buffer);
    }

    [Test]
    public async Task Hidden_Paint_LeavesBufferUntouched()
    {
        var overlay = new MarkupOverlay();
        var buffer = new ScreenBuffer(80, 24);
        overlay.Paint(buffer, new Rect(0, 0, 80, 24));

        await Assert.That(GridDump.Art(buffer)).DoesNotContain("broken.png");
        await Assert.That(overlay.Visible).IsFalse();
        await Assert.That(overlay.HandleKey(KeyEvent.Simple(KeyCode.Escape)) is null).IsTrue();
    }

    [Test]
    public async Task Open_PaintsFrame_Header_AndHints()
    {
        var overlay = Shown(OpenState());

        string art = PaintArt(overlay);

        await Assert.That(overlay.Visible).IsTrue();
        await Assert.That(art).Contains("broken.png");
        await Assert.That(art).Contains("800×600");
        await Assert.That(art).Contains("0 ann");
        await Assert.That(art).Contains("esc close");
        await Assert.That(art.Contains('┌')).IsTrue();
    }

    [Test]
    public async Task Rectangle_PaintsCorners_AtKnownCells()
    {
        // 800×600 in an 80×24 box fits to image (14,2,51,19); (0.2,0.2) → (24,6).
        var state = OpenState() with
        {
            Model = OpenState().Model.Add(
                MarkupKind.Rectangle, NormalizedPoint.Create(0.2, 0.2), NormalizedPoint.Create(0.4, 0.4)),
        };
        var overlay = Shown(state);
        var buffer = new ScreenBuffer(80, 24);
        overlay.Paint(buffer, new Rect(0, 0, 80, 24));

        await Assert.That(buffer.Get(24, 6).Rune).IsEqualTo((int)'┌');
        await Assert.That(GridDump.Art(buffer)).Contains("1 ann");
    }

    [Test]
    public async Task Arrow_PaintsShaft_AndHead_AtKnownCells()
    {
        var state = OpenState() with
        {
            Model = OpenState().Model.Add(
                MarkupKind.Arrow, NormalizedPoint.Create(0.1, 0.5), NormalizedPoint.Create(0.5, 0.5)),
        };
        var overlay = Shown(state);
        var buffer = new ScreenBuffer(80, 24);
        overlay.Paint(buffer, new Rect(0, 0, 80, 24));

        // Head at (39,11), horizontal shaft '─' just before it.
        await Assert.That(buffer.Get(39, 11).Rune).IsEqualTo((int)'▶');
        await Assert.That(buffer.Get(38, 11).Rune).IsEqualTo((int)'─');
    }

    [Test]
    public async Task Text_PaintsString_AtAnchor()
    {
        var state = OpenState() with
        {
            Model = OpenState().Model.Add(
                MarkupKind.Text, NormalizedPoint.Create(0.3, 0.3), NormalizedPoint.Create(0.3, 0.3), text: "hi"),
        };
        var overlay = Shown(state);

        string art = PaintArt(overlay);

        await Assert.That(art).Contains("hi");
    }

    [Test]
    public async Task Resize_Reprojects_SameModel_AtLargerViewport()
    {
        var state = OpenState() with
        {
            Model = OpenState().Model.Add(
                MarkupKind.Rectangle, NormalizedPoint.Create(0.2, 0.2), NormalizedPoint.Create(0.4, 0.4)),
        };

        var small = Shown(state);
        var large = Shown(state);
        var smallBox = small.ComputeImageRect(small.ComputeBox(new Rect(0, 0, 80, 24)));
        var largeBox = large.ComputeImageRect(large.ComputeBox(new Rect(0, 0, 160, 48)));

        // Same model, doubled terminal: the fitted box grows on both axes…
        await Assert.That(largeBox.Width).IsGreaterThan(smallBox.Width);
        await Assert.That(largeBox.Height).IsGreaterThan(smallBox.Height);

        // …and the annotation corner scales with it.
        var smallCorner = MarkupAnnotationModel.ProjectToCells(NormalizedPoint.Create(0.2, 0.2), smallBox);
        var largeCorner = MarkupAnnotationModel.ProjectToCells(NormalizedPoint.Create(0.2, 0.2), largeBox);
        await Assert.That(largeCorner.Col).IsGreaterThan(smallCorner.Col);
        await Assert.That(largeCorner.Row).IsGreaterThan(smallCorner.Row);

        await Assert.That(PaintArt(small)).Contains("┌");
        await Assert.That(PaintArt(large, 160, 48)).Contains("┌");
    }

    [Test]
    public async Task Keys_RouteToStoreMessages()
    {
        var state = OpenState() with
        {
            Model = OpenState().Model.Add(
                MarkupKind.Arrow, NormalizedPoint.Create(0.1, 0.1), NormalizedPoint.Create(0.2, 0.2)),
        };
        var overlay = Shown(state);

        await Assert.That(overlay.HandleKey(KeyEvent.Simple(KeyCode.Escape)) is ChatAppMsg.CloseMarkup).IsTrue();
        await Assert.That(overlay.HandleKey(KeyEvent.Simple(KeyCode.Enter)) is ChatAppMsg.MarkupPlace).IsTrue();
        await Assert.That(overlay.HandleKey(KeyEvent.Char(new Rune('u'))) is ChatAppMsg.MarkupUndo).IsTrue();
        await Assert.That(overlay.HandleKey(KeyEvent.Char(new Rune('r'), KeyModifiers.Ctrl)) is ChatAppMsg.MarkupRedo).IsTrue();
        await Assert.That(overlay.HandleKey(KeyEvent.Simple(KeyCode.Delete)) is ChatAppMsg.MarkupDeleteSelected).IsTrue();
        await Assert.That(overlay.HandleKey(KeyEvent.Simple(KeyCode.Tab)) is ChatAppMsg.MarkupSelectNext).IsTrue();
        await Assert.That(overlay.HandleKey(KeyEvent.Char(new Rune('2'))) is ChatAppMsg.MarkupSelectTool).IsTrue();

        // Selection present: arrows nudge it; shift+arrows resize it.
        await Assert.That(overlay.HandleKey(KeyEvent.Simple(KeyCode.Right)) is ChatAppMsg.MarkupNudge).IsTrue();
        await Assert.That(overlay.HandleKey(KeyEvent.Simple(KeyCode.Right, KeyModifiers.Shift)) is ChatAppMsg.MarkupResize).IsTrue();

        // No selection: arrows move the cursor instead.
        var empty = Shown(OpenState());
        await Assert.That(empty.HandleKey(KeyEvent.Simple(KeyCode.Right)) is ChatAppMsg.MarkupMoveCursor).IsTrue();

        // Release events are never gestures; plain typing with a shape tool is swallowed.
        var release = new KeyEvent(KeyCode.Char, new Rune('x'), KeyModifiers.None, KeyEventType.Release, false);
        await Assert.That(overlay.HandleKey(release) is null).IsTrue();
        await Assert.That(overlay.HandleKey(KeyEvent.Char(new Rune('x'))) is null).IsTrue();
    }

    [Test]
    public async Task TextTool_Types_AndTrims_PendingText()
    {
        var state = OpenState() with { ActiveTool = MarkupKind.Text, PendingText = "a" };
        var overlay = Shown(state);

        var typed = overlay.HandleKey(KeyEvent.Char(new Rune('b')));
        await Assert.That(typed is ChatAppMsg.MarkupSetPendingText set && set.Text == "ab").IsTrue();

        var trimmed = overlay.HandleKey(KeyEvent.Simple(KeyCode.Backspace));
        await Assert.That(trimmed is ChatAppMsg.MarkupSetPendingText clear && clear.Text == string.Empty).IsTrue();

        // 'q' feeds the buffer while the text tool is armed…
        await Assert.That(overlay.HandleKey(KeyEvent.Char(new Rune('q'))) is ChatAppMsg.MarkupSetPendingText).IsTrue();
    }

    [Test]
    public async Task ShapeTool_Q_Dismisses()
    {
        var overlay = Shown(OpenState());

        await Assert.That(overlay.HandleKey(KeyEvent.Char(new Rune('q'))) is ChatAppMsg.CloseMarkup).IsTrue();
    }

    [Test]
    public async Task Mouse_PressDragRelease_MapsThroughImageBox()
    {
        var overlay = Shown(OpenState());
        var viewport = new Rect(0, 0, 80, 24);
        var image = overlay.ComputeImageRect(overlay.ComputeBox(viewport));

        // Image centre cell → (0.5, 0.5) in unit space.
        int cx = image.X + (image.Width / 2);
        int cy = image.Y + (image.Height / 2);
        var press = overlay.HandleMouse(new MouseEvent(MouseEventType.Press, MouseButton.Left, cx, cy, KeyModifiers.None), viewport);
        await Assert.That(press is ChatAppMsg.MarkupPressAt).IsTrue();

        var drag = overlay.HandleMouse(new MouseEvent(MouseEventType.Drag, MouseButton.Left, cx + 4, cy + 2, KeyModifiers.None), viewport);
        await Assert.That(drag is ChatAppMsg.MarkupDragTo).IsTrue();

        var release = overlay.HandleMouse(new MouseEvent(MouseEventType.Release, MouseButton.Left, cx + 4, cy + 2, KeyModifiers.None), viewport);
        await Assert.That(release is ChatAppMsg.MarkupReleaseAt).IsTrue();

        var click = overlay.HandleMouse(new MouseEvent(MouseEventType.Click, MouseButton.Left, cx, cy, KeyModifiers.None), viewport);
        await Assert.That(click is ChatAppMsg.MarkupSelectAt select && select.X == 0.5 && select.Y == 0.5).IsTrue();
    }

    [Test]
    public async Task Mouse_OutsideImageBox_AndWheel_AreSwallowed()
    {
        var overlay = Shown(OpenState());
        var viewport = new Rect(0, 0, 80, 24);

        await Assert.That(overlay.HandleMouse(
            new MouseEvent(MouseEventType.Press, MouseButton.Left, 0, 0, KeyModifiers.None), viewport) is null).IsTrue();
        await Assert.That(overlay.HandleMouse(
            new MouseEvent(MouseEventType.WheelUp, MouseButton.None, 40, 12, KeyModifiers.None), viewport) is null).IsTrue();
        await Assert.That(overlay.HandleMouse(
            new MouseEvent(MouseEventType.Press, MouseButton.Right, 40, 12, KeyModifiers.None), viewport) is null).IsTrue();
    }

    [Test]
    public async Task Layer_Seats_OnStack_OnlyWhileOpen()
    {
        var overlay = new MarkupOverlay();
        var layer = new MarkupOverlayLayer(overlay);

        await Assert.That(layer.Id).IsEqualTo(MarkupOverlayLayer.LayerId);
        await Assert.That(layer.Visible).IsFalse();
        await Assert.That(layer.Opaque).IsTrue();
        await Assert.That(layer.IsModal).IsTrue();

        var hidden = new ScreenBuffer(80, 24);
        layer.Paint(hidden, new Rect(0, 0, 80, 24));
        await Assert.That(GridDump.Art(hidden)).DoesNotContain("broken.png");

        overlay.Sync(OpenState());
        layer.Sync(new Rect(0, 0, 80, 24));
        await Assert.That(layer.Visible).IsTrue();
        await Assert.That(layer.Bounds).IsEqualTo(new Rect(0, 0, 80, 24));

        var buffer = new ScreenBuffer(80, 24);
        layer.Paint(buffer, new Rect(0, 0, 80, 24));
        await Assert.That(GridDump.Art(buffer)).Contains("broken.png");
    }
}
