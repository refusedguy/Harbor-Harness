using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// PRIM2c seating: Dialog/Toast ride <see cref="LayoutTree.Overlays"/> instead
/// of a manual post-<c>PaintAll</c> blit. Hidden layers stay off the stack
/// (quiet frames byte-identical); visible layers paint exactly what the manual
/// <c>Paint</c> path produces, with toast above the opaque dialog.
/// </summary>
public sealed class DialogToastSeatingTests
{
    private const int Cols = 48;
    private const int Rows = 14;

    private static readonly Rect Viewport = new(0, 0, Cols, Rows);

    private static ChatScreen BuildScreen()
    {
        var screen = ChatScreen.Build(
            new ComposerController(),
            new StatusViewModel { Model = "m" },
            includeSidebar: false);
        screen.Tree.Solve(Cols, Rows);
        return screen;
    }

    private static string PaintAll(ChatScreen screen)
    {
        var buffer = new ScreenBuffer(Cols, Rows);
        screen.Tree.PaintAll(buffer);
        return GridDump.Art(buffer);
    }

    [Test]
    public async Task HiddenOverlays_Sync_KeepsStackEmpty_PaintAllUntouched()
    {
        var plain = BuildScreen();
        string expected = PaintAll(plain);

        var seated = BuildScreen();
        seated.SyncOverlays(Viewport);

        await Assert.That(seated.Tree.Overlays.IsEmpty).IsTrue();
        await Assert.That(PaintAll(seated)).IsEqualTo(expected);
    }

    [Test]
    public async Task VisibleDialog_StackPaint_EqualsManualBlit()
    {
        var screen = BuildScreen();
        screen.Dialog.ShowAlert("Hi", "helloworld");
        screen.SyncOverlays(Viewport);

        await Assert.That(screen.Tree.Overlays.Count).IsEqualTo(1);
        await Assert.That(screen.Tree.Overlays.Layers[0].Id).IsEqualTo(DialogOverlayLayer.LayerId);

        string stacked = PaintAll(screen);

        var manual = new ScreenBuffer(Cols, Rows);
        foreach (var panel in screen.Tree.Panels)
        {
            panel.Paint(manual);
        }
        screen.Dialog.Paint(manual, Viewport);

        await Assert.That(stacked).IsEqualTo(GridDump.Art(manual));
        await Assert.That(stacked.Contains("helloworld")).IsTrue();
    }

    [Test]
    public async Task ShownToasts_StackPaint_EqualsManualStripBlit()
    {
        var screen = BuildScreen();
        screen.Toasts.Show("first toast");
        screen.Toasts.Show("second toast");
        screen.SyncOverlays(Viewport);

        await Assert.That(screen.Tree.Overlays.Count).IsEqualTo(1);
        await Assert.That(screen.Tree.Overlays.Layers[0].Id).IsEqualTo(ToastOverlayLayer.LayerId);

        // First pass only drains the pending queue (same as the manual path).
        _ = PaintAll(screen);
        string stacked = PaintAll(screen);

        var strip = screen.Toasts.ComputeBounds(Viewport);
        var manual = new ScreenBuffer(Cols, Rows);
        foreach (var panel in screen.Tree.Panels)
        {
            panel.Paint(manual);
        }
        screen.Toasts.Paint(manual, strip);
        screen.Toasts.Paint(manual, strip);

        await Assert.That(strip.Width).IsEqualTo(31);
        await Assert.That(stacked).IsEqualTo(GridDump.Art(manual));
        await Assert.That(stacked.Contains("first toast")).IsTrue();
        await Assert.That(stacked.Contains("second toast")).IsTrue();
    }

    [Test]
    public async Task DialogOpaque_ToastTransparent_ZOrderAndHitTest()
    {
        const int smallCols = 40;
        const int smallRows = 10;
        var viewport = new Rect(0, 0, smallCols, smallRows);
        var screen = ChatScreen.Build(
            new ComposerController(),
            new StatusViewModel { Model = "m" },
            includeSidebar: false);
        screen.Tree.Solve(smallCols, smallRows);
        screen.Dialog.ShowAlert("Hi", "hello world");
        screen.Toasts.Show("note");
        screen.SyncOverlays(viewport);

        // Bottom-to-top: opaque dialog below, transparent toast on top.
        await Assert.That(screen.Tree.Overlays.Count).IsEqualTo(2);
        await Assert.That(screen.Tree.Overlays.Layers[0].Id).IsEqualTo(DialogOverlayLayer.LayerId);
        await Assert.That(screen.Tree.Overlays.Layers[1].Id).IsEqualTo(ToastOverlayLayer.LayerId);

        var dialog = screen.Tree.Overlays.Get(DialogOverlayLayer.LayerId)!;
        var toast = screen.Tree.Overlays.Get(ToastOverlayLayer.LayerId)!;
        await Assert.That(dialog.Opaque).IsTrue();
        await Assert.That(dialog.HitTransparent).IsFalse();
        await Assert.That(toast.Opaque).IsFalse();
        await Assert.That(toast.HitTransparent).IsTrue();

        // Toast strip overlaps the centered dialog box here: the transparent
        // toast falls through to the dialog beneath it.
        var strip = screen.Toasts.ComputeBounds(viewport);
        await Assert.That(dialog.Bounds.Contains(strip.X + 1, strip.Y)).IsTrue();
        await Assert.That(screen.Tree.Overlays.HitTest(strip.X + 1, strip.Y)!.Id)
            .IsEqualTo(DialogOverlayLayer.LayerId);

        // Dialog center (below the strip) is captured by the dialog.
        int cx = dialog.Bounds.X + dialog.Bounds.Width / 2;
        int cy = dialog.Bounds.Y + dialog.Bounds.Height / 2;
        await Assert.That(screen.Tree.Overlays.HitTest(cx, cy)!.Id)
            .IsEqualTo(DialogOverlayLayer.LayerId);
    }

    [Test]
    public async Task DismissAndClear_Sync_EmptiesStack()
    {
        var screen = BuildScreen();
        screen.Dialog.ShowAlert("Hi", "hello world");
        screen.Toasts.Show("note");
        screen.SyncOverlays(Viewport);
        await Assert.That(screen.Tree.Overlays.Count).IsEqualTo(2);

        screen.Dialog.Dismiss();
        screen.Toasts.Clear();
        screen.SyncOverlays(Viewport);

        await Assert.That(screen.Tree.Overlays.IsEmpty).IsTrue();
    }
}
