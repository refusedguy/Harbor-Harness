using System.Text;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>PRIM12 (#308) fullscreen diff viewer: parse, hunk nav, split/unified + wrap toggles, z-stack seating.</summary>
public sealed class DiffViewerOverlayTests
{
    private const string Sample = """
        --- a/src/app.cs
        +++ b/src/app.cs
        @@ -10,3 +10,4 @@ Render()
         context-a
        -oldA
        +newB
         context-b
        @@ -20,2 +21,3 @@ Second()
         keep
        +added2
        """;

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

    private static DiffViewerOverlay Shown(string diff = Sample, string? file = "src/app.cs")
    {
        var overlay = new DiffViewerOverlay();
        overlay.Show(file, diff);
        return overlay;
    }

    private static string PaintArt(DiffViewerOverlay overlay, int cols = 80, int rows = 24)
    {
        var buffer = new ScreenBuffer(cols, rows);
        overlay.Paint(buffer, new Rect(0, 0, cols, rows));
        return GridDump.Art(buffer);
    }

    [Test]
    public async Task Hidden_Paint_LeavesBufferUntouched()
    {
        var overlay = new DiffViewerOverlay();
        var buffer = new ScreenBuffer(80, 24);

        overlay.Paint(buffer, new Rect(0, 0, 80, 24));

        await Assert.That(GridDump.Art(buffer)).DoesNotContain("Diff");
        await Assert.That(buffer.Get(40, 12).Rune).IsEqualTo((int)' ');
    }

    [Test]
    public async Task Show_ParsesHunksAndPreamble()
    {
        var overlay = Shown();

        await Assert.That(overlay.Visible).IsTrue();
        await Assert.That(overlay.FilePath).IsEqualTo("src/app.cs");
        await Assert.That(overlay.Hunks.Count).IsEqualTo(2);
        await Assert.That(overlay.Preamble.Count).IsEqualTo(2);
        await Assert.That(overlay.Hunks[0].Header).Contains("@@ -10,3 +10,4 @@");
        await Assert.That(overlay.Hunks[0].Lines.Count).IsEqualTo(4);
        await Assert.That(overlay.Hunks[1].Lines.Count).IsEqualTo(2);
        await Assert.That(overlay.SelectedHunk).IsEqualTo(0);
        await Assert.That(overlay.Mode).IsEqualTo(DiffViewerMode.Unified);
    }

    [Test]
    public async Task Show_PaintsHeaderHunksAndFooter()
    {
        string art = PaintArt(Shown());

        await Assert.That(art).Contains("Diff");
        await Assert.That(art).Contains("src/app.cs");
        await Assert.That(art).Contains("hunk 1/2");
        await Assert.That(art).Contains("[unified]");
        await Assert.That(art).Contains("@@ -10,3 +10,4 @@");
        await Assert.That(art).Contains("- oldA");
        await Assert.That(art).Contains("+ newB");
        await Assert.That(art).Contains("n/p hunk");
        await Assert.That(art).Contains("╭");
    }

    [Test]
    public async Task PlainContent_WithoutHunks_BecomesSingleHunk()
    {
        var overlay = Shown("-gone\n+fresh\nsame");

        await Assert.That(overlay.Hunks.Count).IsEqualTo(1);

        string art = PaintArt(overlay);
        await Assert.That(art).Contains("- gone");
        await Assert.That(art).Contains("+ fresh");
    }

    [Test]
    public async Task HunkNav_NextPrevFirstLast_Clamps()
    {
        var overlay = Shown();

        overlay.NextHunk();
        await Assert.That(overlay.SelectedHunk).IsEqualTo(1);
        await Assert.That(PaintArt(overlay)).Contains("hunk 2/2");

        overlay.NextHunk();
        await Assert.That(overlay.SelectedHunk).IsEqualTo(1);

        overlay.PreviousHunk();
        await Assert.That(overlay.SelectedHunk).IsEqualTo(0);

        overlay.PreviousHunk();
        await Assert.That(overlay.SelectedHunk).IsEqualTo(0);

        overlay.LastHunk();
        await Assert.That(overlay.SelectedHunk).IsEqualTo(1);

        overlay.FirstHunk();
        await Assert.That(overlay.SelectedHunk).IsEqualTo(0);
    }

    [Test]
    public async Task ToggleMode_Split_PairsSidesWithGutter()
    {
        var overlay = Shown();

        overlay.ToggleMode();
        await Assert.That(overlay.Mode).IsEqualTo(DiffViewerMode.Split);

        string split = PaintArt(overlay);
        await Assert.That(split).Contains("[split]");
        await Assert.That(split).Contains("│");

        // Removed (left) and added (right) share one paired row in split mode.
        bool paired = false;
        foreach (string line in split.Split('\n'))
        {
            if (line.Contains("oldA") && line.Contains("newB"))
            {
                paired = true;
            }
        }

        await Assert.That(paired).IsTrue();
        await Assert.That(PaintArt(Shown())).DoesNotContain("│oldA");

        overlay.ToggleMode();
        await Assert.That(overlay.Mode).IsEqualTo(DiffViewerMode.Unified);
    }

    [Test]
    public async Task ToggleWrap_LongLine_TruncatesVsFlows()
    {
        string tail = new string('T', 12);
        string longLine = "+" + new string('x', 60) + tail;
        var overlay = Shown("@@ -1 +1 @@\n" + longLine);

        string nowrap = PaintArt(overlay, cols: 40, rows: 12);
        await Assert.That(nowrap).Contains("…");
        await Assert.That(nowrap).DoesNotContain(tail);

        overlay.ToggleWrap();
        string wrapped = PaintArt(overlay, cols: 40, rows: 12);
        await Assert.That(wrapped).Contains(tail);
    }

    [Test]
    public async Task TinyViewport_PaintStaysNoop()
    {
        var overlay = Shown();
        var buffer = new ScreenBuffer(10, 4);

        overlay.Paint(buffer, new Rect(0, 0, 10, 4));

        await Assert.That(GridDump.Art(buffer)).DoesNotContain("Diff");
    }

    [Test]
    public async Task HandleKey_NavTogglesAndDismiss()
    {
        var overlay = Shown();

        await Assert.That(overlay.HandleKey(new ConsoleKeyInfo('x', ConsoleKey.X, false, false, false))).IsFalse();

        await Assert.That(overlay.HandleKey(new ConsoleKeyInfo('n', ConsoleKey.N, false, false, false))).IsTrue();
        await Assert.That(overlay.SelectedHunk).IsEqualTo(1);

        await Assert.That(overlay.HandleKey(new ConsoleKeyInfo('p', ConsoleKey.P, false, false, false))).IsTrue();
        await Assert.That(overlay.SelectedHunk).IsEqualTo(0);

        await Assert.That(overlay.HandleKey(new ConsoleKeyInfo('s', ConsoleKey.S, false, false, false))).IsTrue();
        await Assert.That(overlay.Mode).IsEqualTo(DiffViewerMode.Split);

        await Assert.That(overlay.HandleKey(new ConsoleKeyInfo('w', ConsoleKey.W, false, false, false))).IsTrue();
        await Assert.That(overlay.Wrap).IsTrue();

        await Assert.That(overlay.HandleKey(new ConsoleKeyInfo('q', ConsoleKey.Q, false, false, false))).IsTrue();
        await Assert.That(overlay.Visible).IsFalse();

        await Assert.That(overlay.HandleKey(new ConsoleKeyInfo('n', ConsoleKey.N, false, false, false))).IsFalse();
    }

    [Test]
    public async Task HandleKey_EscAndArrows()
    {
        var overlay = Shown();

        await Assert.That(overlay.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false))).IsTrue();
        await Assert.That(overlay.SelectedHunk).IsEqualTo(1);

        await Assert.That(overlay.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false))).IsTrue();
        await Assert.That(overlay.SelectedHunk).IsEqualTo(0);

        await Assert.That(overlay.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false))).IsTrue();
        await Assert.That(overlay.Visible).IsFalse();
    }

    [Test]
    public async Task HandleKey_KeyEvent_RoutesNavAndDismiss()
    {
        var overlay = Shown();

        await Assert.That(overlay.HandleKey(KeyEvent.Char(new Rune('n')))).IsTrue();
        await Assert.That(overlay.SelectedHunk).IsEqualTo(1);

        await Assert.That(overlay.HandleKey(KeyEvent.Char(new Rune('s')))).IsTrue();
        await Assert.That(overlay.Mode).IsEqualTo(DiffViewerMode.Split);

        await Assert.That(overlay.HandleKey(KeyEvent.Char(new Rune('w')))).IsTrue();
        await Assert.That(overlay.Wrap).IsTrue();

        await Assert.That(overlay.HandleKey(KeyEvent.Simple(KeyCode.Escape))).IsTrue();
        await Assert.That(overlay.Visible).IsFalse();
    }

    [Test]
    public async Task Layer_OnStack_PaintsOverLower_HitTestWins_ModalBarrier()
    {
        var overlay = Shown();
        var layer = new DiffViewerOverlayLayer(overlay);
        layer.Sync(new Rect(0, 0, 80, 24));

        await Assert.That(layer.Id).IsEqualTo(DiffViewerOverlayLayer.LayerId);
        await Assert.That(layer.Opaque).IsTrue();
        await Assert.That(layer.HitTransparent).IsFalse();
        await Assert.That(layer.IsModal).IsTrue();
        await Assert.That(layer.Visible).IsTrue();

        var stack = new OverlayStack();
        stack.Push(new StubLayer("base", new Rect(0, 0, 80, 24), 'a'));
        stack.Push(layer);
        var buffer = new ScreenBuffer(80, 24);

        stack.PaintOver(buffer);

        await Assert.That(GridDump.Art(buffer)).Contains("src/app.cs");
        await Assert.That(stack.HitTest(0, 0)!.Id).IsEqualTo("diff");
        await Assert.That(stack.TopModal!.Id).IsEqualTo("diff");
        await Assert.That(stack.HasModalBarrier).IsTrue();

        await Assert.That(stack.RouteKey(KeyEvent.Char(new Rune('n')))).IsTrue();
        await Assert.That(overlay.SelectedHunk).IsEqualTo(1);

        overlay.Hide();
        await Assert.That(layer.Visible).IsFalse();
        await Assert.That(stack.HitTest(0, 0)!.Id).IsEqualTo("base");
        await Assert.That(stack.HasModalBarrier).IsFalse();
    }

    [Test]
    public async Task Layer_Hidden_StaysNoopOnStack()
    {
        var layer = new DiffViewerOverlayLayer(new DiffViewerOverlay());
        layer.Sync(new Rect(0, 0, 80, 24));

        await Assert.That(layer.Visible).IsFalse();

        var stack = new OverlayStack();
        stack.Push(new StubLayer("base", new Rect(0, 0, 80, 24), 'a'));
        stack.Push(layer);
        var buffer = new ScreenBuffer(80, 24);

        stack.PaintOver(buffer);

        await Assert.That(GridDump.Art(buffer)).DoesNotContain("Diff");
        await Assert.That(buffer.Get(40, 12).Rune).IsEqualTo((int)'a');
    }

    [Test]
    public async Task Seating_SyncOverlays_OrdersDialogDiffToast()
    {
        var screen = BuildScreen();
        screen.Dialog.ShowAlert("Hi", "hello");
        screen.DiffViewer.Show("src/app.cs", Sample);
        screen.Toasts.Show("saved");
        screen.SyncOverlays(new Rect(0, 0, 48, 14));

        await Assert.That(screen.Tree.Overlays.Count).IsEqualTo(3);
        await Assert.That(screen.Tree.Overlays.Layers[0].Id).IsEqualTo(DialogOverlayLayer.LayerId);
        await Assert.That(screen.Tree.Overlays.Layers[1].Id).IsEqualTo(DiffViewerOverlayLayer.LayerId);
        await Assert.That(screen.Tree.Overlays.Layers[2].Id).IsEqualTo(ToastOverlayLayer.LayerId);
    }

    [Test]
    public async Task Seating_HiddenDiff_KeepsStackEmpty_PaintAllUntouched()
    {
        var plain = BuildScreen();
        string expected = PaintAll(plain);

        var seated = BuildScreen();
        seated.SyncOverlays(new Rect(0, 0, 48, 14));

        await Assert.That(seated.Tree.Overlays.IsEmpty).IsTrue();
        await Assert.That(PaintAll(seated)).IsEqualTo(expected);
    }

    private static ChatScreen BuildScreen()
    {
        var screen = ChatScreen.Build(
            new ComposerController(),
            new StatusViewModel { Model = "m" },
            includeSidebar: false);
        screen.Tree.Solve(48, 14);
        return screen;
    }

    private static string PaintAll(ChatScreen screen)
    {
        var buffer = new ScreenBuffer(48, 14);
        screen.Tree.PaintAll(buffer);
        return GridDump.Art(buffer);
    }
}
