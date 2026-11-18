using System.Text;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #2376: the Sheet primitive's contract — controlled open state, edge-anchored
/// slide geometry, a FocusRouter trap and Escape dismissal, all seated on the
/// existing OverlayStack. Goldens pin the painted panel art; the state tests
/// pin the behaviour the art cannot show.
/// </summary>
[NotInParallel("pty")]
public class SheetOverlayTests
{
    private static SheetOverlay NewSheet(
        SheetEdge edge = SheetEdge.Right,
        int extent = 12,
        double progress = 1.0)
    {
        var sheet = new SheetOverlay
        {
            Edge = edge,
            Extent = extent,
            Progress = progress,
            Title = "Details",
            Lines = new[] { "body" },
        };
        sheet.SetItems(new SheetFocusItem("files", "Files"), new SheetFocusItem("git", "Git"));
        sheet.Open();
        return sheet;
    }

    private static ChatScreen BuildScreen() => ScreenFixtures.BuildScreen(80, 24);

    // ------------------------------------------------------------- seating --

    [Test]
    public async Task Closed_SeatsNothing_AndIgnoresKeys()
    {
        var screen = BuildScreen();
        screen.SyncOverlays(new Rect(0, 0, 80, 24));

        await Assert.That(screen.Tree.Overlays.IsEmpty).IsTrue();
        await Assert.That(screen.Sheet.IsOpen).IsFalse();
        await Assert.That(screen.Sheet.HandleKey(KeyEvent.Simple(KeyCode.Escape))).IsFalse();
    }

    [Test]
    public async Task Open_SeatsOneModalOpaqueLayer_OnTheExistingStack()
    {
        var screen = BuildScreen();
        screen.Sheet.SetItems(new SheetFocusItem("a", "A"));
        screen.Sheet.Open();
        screen.SyncOverlays(new Rect(0, 0, 80, 24));

        await Assert.That(screen.Tree.Overlays.Count).IsEqualTo(1);
        IOverlayLayer layer = screen.Tree.Overlays.Layers[0];
        await Assert.That(layer.Id).IsEqualTo(SheetOverlayLayer.LayerId);
        await Assert.That(layer.Visible).IsTrue();
        await Assert.That(layer.Opaque).IsTrue();
        await Assert.That(layer.IsModal).IsTrue();
        await Assert.That(screen.Tree.Overlays.HasModalBarrier).IsTrue();
    }

    [Test]
    public async Task Escape_Closes_AndRemovesTheLayer()
    {
        var screen = BuildScreen();
        screen.Sheet.Open();
        screen.SyncOverlays(new Rect(0, 0, 80, 24));
        await Assert.That(screen.Tree.Overlays.Count).IsEqualTo(1);

        await Assert.That(screen.Sheet.HandleKey(KeyEvent.Simple(KeyCode.Escape))).IsTrue();
        await Assert.That(screen.Sheet.IsOpen).IsFalse();
        await Assert.That(screen.Sheet.Focus.Current).IsNull();

        screen.SyncOverlays(new Rect(0, 0, 80, 24));
        await Assert.That(screen.Tree.Overlays.IsEmpty).IsTrue();
    }

    // --------------------------------------------------------------- focus --

    [Test]
    public async Task Opening_SeedsFirstItem_AndTabWrapsInsideTheSheet()
    {
        var sheet = NewSheet();

        await Assert.That(sheet.Items[0].IsFocused).IsTrue();
        await Assert.That(sheet.Items[1].IsFocused).IsFalse();

        await Assert.That(sheet.HandleKey(KeyEvent.Simple(KeyCode.Tab))).IsTrue();
        await Assert.That(sheet.Items[0].IsFocused).IsFalse();
        await Assert.That(sheet.Items[1].IsFocused).IsTrue();

        // Wrapping is the trap: the router never leaves the sheet's own order.
        await Assert.That(sheet.HandleKey(KeyEvent.Simple(KeyCode.Tab))).IsTrue();
        await Assert.That(sheet.Items[0].IsFocused).IsTrue();

        await Assert.That(sheet.HandleKey(
            KeyEvent.Simple(KeyCode.Tab, KeyModifiers.Shift))).IsTrue();
        await Assert.That(sheet.Items[1].IsFocused).IsTrue();
    }

    [Test]
    public async Task EveryOtherKey_IsSwallowedWhileOpen()
    {
        var sheet = NewSheet();

        await Assert.That(sheet.HandleKey(KeyEvent.Char(new Rune('a')))).IsTrue();
        await Assert.That(sheet.IsOpen).IsTrue();

        // A second Escape is a no-op once the sheet is closed.
        await Assert.That(sheet.HandleKey(KeyEvent.Simple(KeyCode.Escape))).IsTrue();
        await Assert.That(sheet.HandleKey(KeyEvent.Simple(KeyCode.Escape))).IsFalse();
    }

    // ------------------------------------------------------------ geometry --

    [Test]
    public async Task Progress_TranslatesTheBoxTowardsItsAnchorEdge()
    {
        var viewport = new Rect(0, 0, 20, 8);

        Rect closed = SheetPainter.ComputeBox(viewport, SheetEdge.Right, 12, 0.0);
        Rect half = SheetPainter.ComputeBox(viewport, SheetEdge.Right, 12, 0.5);
        Rect open = SheetPainter.ComputeBox(viewport, SheetEdge.Right, 12, 1.0);

        // At 0 the box starts at the right edge (nothing visible), at 1 it is flush.
        await Assert.That(closed.X).IsEqualTo(20);
        await Assert.That(half.X).IsEqualTo(14);
        await Assert.That(open.X).IsEqualTo(8);
        await Assert.That(open.Width).IsEqualTo(12);
        await Assert.That(open.Height).IsEqualTo(8);
    }

    [Test]
    public async Task AllFourEdges_AnchorTheirBox()
    {
        var viewport = new Rect(0, 0, 20, 12);

        Rect left = SheetPainter.ComputeBox(viewport, SheetEdge.Left, 8, 1.0);
        Rect right = SheetPainter.ComputeBox(viewport, SheetEdge.Right, 8, 1.0);
        Rect top = SheetPainter.ComputeBox(viewport, SheetEdge.Top, 8, 1.0);
        Rect bottom = SheetPainter.ComputeBox(viewport, SheetEdge.Bottom, 8, 1.0);

        await Assert.That(left.X).IsEqualTo(0);
        await Assert.That(right.X).IsEqualTo(12);
        await Assert.That(top.Y).IsEqualTo(0);
        await Assert.That(bottom.Y).IsEqualTo(4);

        await Assert.That(left.Width).IsEqualTo(8);
        await Assert.That(right.Width).IsEqualTo(8);
        await Assert.That(top.Height).IsEqualTo(8);
        await Assert.That(bottom.Height).IsEqualTo(8);
    }

    [Test]
    public async Task TinyViewport_HidesTheSheet()
    {
        Rect box = SheetPainter.ComputeBox(new Rect(0, 0, 6, 6), SheetEdge.Left, 12, 1.0);

        await Assert.That(box.Width).IsZero();
        await Assert.That(box.Height).IsZero();
    }

    // -------------------------------------------------------------- goldens --

    private static async Task VerifyGolden(string name, SheetOverlay sheet, int cols = 20, int rows = 8)
    {
        var buffer = TestRender.Buffer(cols, rows);
        var layer = new SheetOverlayLayer(sheet);
        layer.Sync(new Rect(0, 0, cols, rows));
        layer.Paint(buffer, new Rect(0, 0, cols, rows));

        string document = ScreenFixtures.GoldenDocument(name, buffer);
        string expected = Golden.VerifyText(Golden.FixtureDir("sheet"), name, document);
        await Assert.That(expected).IsEqualTo(document);
    }

    [Test]
    public async Task Golden_RightOpen()
    {
        await VerifyGolden("right-open", NewSheet(SheetEdge.Right));
    }

    [Test]
    public async Task Golden_LeftOpen()
    {
        await VerifyGolden("left-open", NewSheet(SheetEdge.Left));
    }

    [Test]
    public async Task Golden_TopOpen()
    {
        await VerifyGolden("top-open", NewSheet(SheetEdge.Top, extent: 8), rows: 12);
    }

    [Test]
    public async Task Golden_BottomOpen()
    {
        await VerifyGolden("bottom-open", NewSheet(SheetEdge.Bottom, extent: 8), rows: 12);
    }

    [Test]
    public async Task Golden_RightPartial()
    {
        await VerifyGolden("right-partial", NewSheet(SheetEdge.Right, progress: 0.5));
    }
}
