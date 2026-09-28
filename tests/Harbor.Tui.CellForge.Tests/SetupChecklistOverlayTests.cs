using System.Text;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Projection;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Setup-guide checklist overlay (KILLER_FEATURES §2.7 Feature 9, issue #383):
/// checklist rows with <c>✓</c>/<c>○</c> markers, the <c>done/total</c> gauge,
/// in-place progress on a model refresh, modal key handling and z-stack seating.
/// </summary>
[NotInParallel("pty")]
public sealed class SetupChecklistOverlayTests
{
    private static SetupChecklistModel Partial(int done)
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var ids = SetupTaskIds.All;
        for (int i = 0; i < ids.Count; i++)
        {
            map[ids[i]] = i < done;
        }

        return SetupChecklistModel.Empty.WithCompletion(map);
    }

    private static string Art(SetupChecklistOverlay overlay, int cols = 80, int rows = 24)
    {
        var buffer = new ScreenBuffer(cols, rows);
        overlay.Paint(buffer, new Rect(0, 0, cols, rows));
        return GridDump.Art(buffer);
    }

    [Test]
    public async Task Hidden_Paint_LeavesBufferUntouched()
    {
        var overlay = new SetupChecklistOverlay();

        string art = Art(overlay);

        await Assert.That(overlay.Visible).IsFalse();
        await Assert.That(art).DoesNotContain("Setup guide");
        await Assert.That(art.Replace("\n", string.Empty).Trim()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Show_PaintsEveryTaskWithMarkerAndLabel()
    {
        var overlay = new SetupChecklistOverlay();
        overlay.Show(Partial(3));

        string art = Art(overlay);

        await Assert.That(art).Contains("Setup guide");
        await Assert.That(art).Contains("✓ Config file created");
        await Assert.That(art).Contains("✓ Provider key stored");
        await Assert.That(art).Contains("✓ Provider reachable");
        await Assert.That(art).Contains("○ Workspace open");
        await Assert.That(art).Contains("○ First prompt sent");
        await Assert.That(art).Contains("Esc to close");
        await Assert.That(art).Contains("╭");
    }

    [Test]
    public async Task Show_PaintsProgressCaptionAndGaugeBar()
    {
        var overlay = new SetupChecklistOverlay();
        overlay.Show(Partial(3));

        string art = Art(overlay);

        await Assert.That(art).Contains("3/5");
        // GaugeBar filled + track cells in the same row as the caption.
        int gaugeRow = art.Split('\n').ToList().FindIndex(line => line.Contains("3/5"));
        await Assert.That(gaugeRow).IsGreaterThanOrEqualTo(0);
        string row = art.Split('\n')[gaugeRow];
        await Assert.That(row).Contains(GaugeBar.Filled.ToString());
        await Assert.That(row).Contains(GaugeBar.Track.ToString());
    }

    [Test]
    public async Task ModelRefresh_RepaintsProgressInPlace()
    {
        var overlay = new SetupChecklistOverlay();
        overlay.Show(Partial(3));
        string before = Art(overlay);

        overlay.SetModel(Partial(5));
        string after = Art(overlay);

        await Assert.That(before).Contains("3/5");
        await Assert.That(after).Contains("5/5");
        await Assert.That(after).DoesNotContain("○ First prompt sent");
        await Assert.That(after).Contains("✓ First prompt sent");
    }

    [Test]
    public async Task SetModel_Null_ResetsToEmpty()
    {
        var overlay = new SetupChecklistOverlay();
        overlay.Show(Partial(5));

        overlay.SetModel(null);

        await Assert.That(overlay.Model.CompletedCount).IsEqualTo(0);
        await Assert.That(overlay.Model.ProgressText).IsEqualTo("0/5");
    }

    [Test]
    public async Task ComputeBox_CentersInsideViewport()
    {
        var overlay = new SetupChecklistOverlay();
        overlay.Show(Partial(2));

        Rect box = overlay.ComputeBox(new Rect(0, 0, 80, 24));

        await Assert.That(box.Width).IsGreaterThanOrEqualTo(SetupChecklistOverlay.MinWidth);
        await Assert.That(box.Height).IsLessThanOrEqualTo(24);
        await Assert.That(box.X).IsGreaterThanOrEqualTo(0);
        await Assert.That(box.Right).IsLessThanOrEqualTo(80);
        await Assert.That(box.Bottom).IsLessThanOrEqualTo(24);
    }

    [Test]
    public async Task ComputeBox_ClampsToTinyViewport()
    {
        var overlay = new SetupChecklistOverlay();
        overlay.Show(Partial(2));

        Rect box = overlay.ComputeBox(new Rect(0, 0, 20, 6));

        await Assert.That(box.Width).IsLessThanOrEqualTo(20);
        await Assert.That(box.Height).IsLessThanOrEqualTo(6);
    }

    [Test]
    public async Task HandleKey_Hidden_PassesThrough()
    {
        var overlay = new SetupChecklistOverlay();

        bool consumed = overlay.HandleKey(KeyEvent.Simple(KeyCode.Escape));

        await Assert.That(consumed).IsFalse();
        await Assert.That(overlay.Visible).IsFalse();
    }

    [Test]
    public async Task HandleKey_DismissKeys_CloseTheModal()
    {
        foreach (KeyEvent dismiss in new[]
                 {
                     KeyEvent.Simple(KeyCode.Escape),
                     KeyEvent.Char(new Rune('q')),
                     KeyEvent.Char(new Rune('?')),
                 })
        {
            var overlay = new SetupChecklistOverlay();
            overlay.Show(Partial(1));

            bool consumed = overlay.HandleKey(dismiss);

            await Assert.That(consumed).IsTrue();
            await Assert.That(overlay.Visible).IsFalse();
        }
    }

    [Test]
    public async Task HandleKey_Enter_ClosesButFallsThrough()
    {
        var overlay = new SetupChecklistOverlay();
        overlay.Show(Partial(1));

        bool consumed = overlay.HandleKey(KeyEvent.Simple(KeyCode.Enter));

        await Assert.That(consumed).IsFalse();
        await Assert.That(overlay.Visible).IsFalse();
    }

    [Test]
    public async Task HandleKey_TypedKeys_ReachTheComposerBehindTheGuide()
    {
        var overlay = new SetupChecklistOverlay();
        overlay.Show(Partial(1));

        bool consumed = overlay.HandleKey(KeyEvent.Char(new Rune('a')));

        await Assert.That(consumed).IsFalse();
        await Assert.That(overlay.Visible).IsTrue();
    }

    [Test]
    public async Task Layer_Hidden_PaintsNothingThroughTheStack()
    {
        var overlay = new SetupChecklistOverlay();
        var layer = new SetupChecklistOverlayLayer(overlay);
        layer.Sync(new Rect(0, 0, 80, 24));
        var stack = new OverlayStack();
        stack.Push(layer);

        var buffer = new ScreenBuffer(80, 24);
        stack.PaintOver(buffer);

        await Assert.That(layer.Visible).IsFalse();
        await Assert.That(stack.HasModalBarrier).IsFalse();
        await Assert.That(GridDump.Art(buffer).Replace("\n", string.Empty).Trim()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Layer_Visible_IsOpaqueButLeavesTheComposerReachable()
    {
        var overlay = new SetupChecklistOverlay();
        overlay.Show(Partial(3));
        var layer = new SetupChecklistOverlayLayer(overlay);
        layer.Sync(new Rect(0, 0, 80, 24));
        var stack = new OverlayStack();
        stack.Push(layer);

        var buffer = new ScreenBuffer(80, 24);
        stack.PaintOver(buffer);

        await Assert.That(layer.Visible).IsTrue();
        await Assert.That(layer.Opaque).IsTrue();

        // Read-only guide: it occludes the panels under its box but raises no
        // modal barrier, so keys keep flowing to the composer.
        await Assert.That(layer.IsModal).IsFalse();
        await Assert.That(stack.HasModalBarrier).IsFalse();
        await Assert.That(GridDump.Art(buffer)).Contains("Setup guide");
    }

    [Test]
    public async Task Layer_OnKey_ForwardsTheDismissKeys()
    {
        var overlay = new SetupChecklistOverlay();
        overlay.Show(Partial(1));
        var layer = new SetupChecklistOverlayLayer(overlay);

        bool consumed = layer.OnKey(KeyEvent.Simple(KeyCode.Escape));

        await Assert.That(consumed).IsTrue();
        await Assert.That(overlay.Visible).IsFalse();
    }
}
