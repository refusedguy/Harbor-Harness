using System.Text;
using Harbor.Tui.CellForge.Capabilities;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Inline-image render pipeline (KILLER_FEATURES §2.7 Feature 12, issue #387):
/// the engine's <see cref="InlineImageLayer" /> must emit kitty APC / OSC 1337
/// through the frame's own byte stream — never a side-channel write — and the
/// payloads must stay invisible to the cell diff. The fullscreen zoom viewer's
/// open/zoom/close round trip is pinned here too.
/// </summary>
public class InlineImageRenderTests
{
    private static ScreenBuffer PaintBlock(ImageBlock block, InlineImageLayer layer, int cols, int rows)
    {
        var buffer = new ScreenBuffer(cols, rows);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, cols, rows), 0, inlineImages: layer));
        return buffer;
    }

    [Test]
    public async Task Layer_DisablesItself_UntilTheProbeSpeaks()
    {
        var layer = new InlineImageLayer();
        await Assert.That(layer.Enabled).IsFalse();

        layer.TryEncode("a.png", "image/png", ImageBlockTests.PngHeader(8, 8), 4, 2, out byte[]? off);
        await Assert.That(off).IsNull();

        layer.Kind = InlineImageKind.KittyApc;
        await Assert.That(layer.Enabled).IsTrue();
        await Assert.That(layer.TryEncode("a.png", "image/png", ImageBlockTests.PngHeader(8, 8), 4, 2, out byte[]? on)).IsTrue();
        await Assert.That(on).IsNotNull();
    }

    [Test]
    public async Task Kitty_Encode_StampsCellBox_AndSuppressesCursorMove()
    {
        var layer = new InlineImageLayer { Kind = InlineImageKind.KittyApc };
        bool ok = layer.TryEncode("shot.png", "image/png", ImageBlockTests.PngHeader(64, 32), 20, 8, out byte[]? payload);

        await Assert.That(ok).IsTrue();
        string seq = Encoding.UTF8.GetString(payload!);
        await Assert.That(seq).StartsWith("\u001B_Gf=100,a=T,C=1,c=20,r=8,");
        await Assert.That(seq).EndsWith("\u001B\\");
    }

    [Test]
    public async Task Osc1337_Encode_StampsCellBox_InCellUnits()
    {
        var layer = new InlineImageLayer { Kind = InlineImageKind.Osc1337 };
        byte[] jpeg = JpegProbeTests.Jpeg(800, 600);
        bool ok = layer.TryEncode("photo.jpg", "image/jpeg", jpeg, 30, 12, out byte[]? payload);

        await Assert.That(ok).IsTrue();
        string seq = Encoding.UTF8.GetString(payload!);
        await Assert.That(seq).Contains("name=photo.jpg");
        await Assert.That(seq).Contains("width=30;height=12;");
        await Assert.That(seq).EndsWith("\u0007");
    }

    [Test]
    public async Task Kitty_RefusesNonPng_SixCellsOfRectAreNotEnoughToLie()
    {
        var layer = new InlineImageLayer { Kind = InlineImageKind.KittyApc };
        await Assert.That(layer.TryEncode("p.jpg", "image/jpeg", JpegProbeTests.Jpeg(8, 8), 4, 2, out byte[]? jpeg)).IsFalse();
        await Assert.That(jpeg).IsNull();

        // Degenerate geometry never encodes — an empty rect is the caller's
        // signal to keep the text card.
        await Assert.That(layer.TryEncode("a.png", "image/png", ImageBlockTests.PngHeader(8, 8), 0, 4, out _)).IsFalse();
    }

    [Test]
    public async Task Place_ClipsToTheFrame_AndDropsWhatIsFullyOutside()
    {
        var layer = new InlineImageLayer
        {
            Kind = InlineImageKind.KittyApc,
            FrameBounds = new Rect(0, 0, 20, 10),
        };

        layer.Place(new Rect(15, 2, 30, 4), new byte[] { 1, 2, 3 });  // overhangs right
        layer.Place(new Rect(-40, 2, 10, 4), new byte[] { 4, 5, 6 });  // fully outside
        layer.Place(new Rect(4, 6, 6, 2), new byte[] { 7, 8 });       // inside

        await Assert.That(layer.Count).IsEqualTo(2);
        var first = layer.PlacementAt(0);
        await Assert.That(first.Cells.Right).IsEqualTo(20);
        await Assert.That(layer.PlacementAt(1).Cells.X).IsEqualTo(4);
    }

    [Test]
    public async Task Place_CapsTheFrame_AndCountsWhatItDropped()
    {
        var layer = new InlineImageLayer { Kind = InlineImageKind.KittyApc, FrameBounds = new Rect(0, 0, 200, 200) };
        for (int i = 0; i < InlineImageLayer.MaxPlacements + 5; i++)
        {
            layer.Place(new Rect(0, i, 4, 1), new byte[] { 1 });
        }

        await Assert.That(layer.Count).IsEqualTo(InlineImageLayer.MaxPlacements);
        await Assert.That(layer.DroppedCount).IsEqualTo(5);
    }

    [Test]
    public async Task BeginFrame_DropsThePreviousFramesPlacements()
    {
        var layer = new InlineImageLayer { Kind = InlineImageKind.KittyApc, FrameBounds = new Rect(0, 0, 20, 10) };
        layer.Place(new Rect(0, 0, 4, 2), new byte[] { 1 });
        await Assert.That(layer.Count).IsEqualTo(1);

        layer.BeginFrame();
        await Assert.That(layer.Count).IsEqualTo(0);

        // An empty frame emits nothing at all.
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend);
        writer.BeginFrame();
        layer.Emit(writer);
        writer.EndFrame();
        await Assert.That(backend.TotalBytes).IsEqualTo(0);
    }

    /// <summary>
    /// The acceptance point for "no raw write bypassing the engine": the escape
    /// payload must arrive in the SAME backend write as the cell diff, so a
    /// synchronized-update frame still contains one contiguous byte span.
    /// </summary>
    [Test]
    public async Task Session_EmitsPayloadsInsideTheFramesOwnWrite()
    {
        var backend = new RecordingBackend();
        var session = new ScreenSession(new AnsiWriter(backend, syncUpdates: true), 40, 20);
        session.Images.Kind = InlineImageKind.KittyApc;

        var timeline = new VirtualizedChatTimeline { InlineImages = session.Images };
        timeline.Append(new ImageBlock("shot.png", "image/png", 2048, ImageBlockTests.PngHeader(640, 480), graphicsAvailable: true));

        session.BeginFrame();
        _ = timeline.PrepareFrame(40, 20);
        timeline.Paint(session.PaintBuffer, new Rect(0, 0, 40, 20));
        await session.FlushFrameAsync();

        await Assert.That(backend.Writes.Count).IsEqualTo(1);
        string frame = Encoding.UTF8.GetString(backend.Writes[0]);
        await Assert.That(frame).Contains("\u001B_Gf=100,a=T,C=1,");
        // Synchronized-output wrapper is closed only once, after the payload.
        await Assert.That(frame.IndexOf("\u001B_Gf=100", StringComparison.Ordinal))
            .IsLessThan(frame.LastIndexOf("\u001B[?2026l", StringComparison.Ordinal));
    }

    /// <summary>
    /// Dirty-rect/occlusion accounting: the payload writes NO cells, so the
    /// second frame with identical content is empty — the diff sees nothing and
    /// the row hashes are unchanged. If an image ever leaked into the cell
    /// buffer, this test would fail on the byte count.
    /// </summary>
    [Test]
    public async Task PayloadBytes_NeverEnterTheCellGrid()
    {
        var backend = new RecordingBackend();
        var session = new ScreenSession(new AnsiWriter(backend), 40, 20);
        session.Images.Kind = InlineImageKind.KittyApc;

        var timeline = new VirtualizedChatTimeline { InlineImages = session.Images };
        timeline.Append(new ImageBlock("shot.png", "image/png", 2048, ImageBlockTests.PngHeader(640, 480), graphicsAvailable: true));

        session.BeginFrame();
        _ = timeline.PrepareFrame(40, 20);
        timeline.Paint(session.PaintBuffer, new Rect(0, 0, 40, 20));
        await session.FlushFrameAsync();

        // One full-text frame, zero image bytes in the cell state.
        await Assert.That(GridDump.Cells(session.PaintBuffer)).DoesNotContain("\u001B");
        await Assert.That(session.Engine.FrontMatches(session.Back)).IsTrue();

        backend.ResetForTests();
        session.BeginFrame();
        _ = timeline.PrepareFrame(40, 20);
        timeline.Paint(session.PaintBuffer, new Rect(0, 0, 40, 20));
        await session.FlushFrameAsync();

        // Identical cells ⇒ the diff emits nothing, and the placement is
        // unchanged ⇒ the base64 payload is not re-transmitted either. A
        // steady-state frame with a picture on screen costs zero bytes.
        await Assert.That(backend.TotalBytes).IsEqualTo(0);
    }

    [Test]
    public async Task Emit_SkipsUnchangedPlacements_ButResendsOnMove()
    {
        var backend = new RecordingBackend();
        var layer = new InlineImageLayer { Kind = InlineImageKind.KittyApc, FrameBounds = new Rect(0, 0, 40, 20) };
        byte[] payload = [0x41, 0x42];

        // Frame 1: a fresh placement is sent.
        var writer = new AnsiWriter(backend);
        writer.BeginFrame();
        layer.Place(new Rect(2, 0, 10, 4), payload);
        layer.Emit(writer);
        writer.EndFrame();
        long first = backend.TotalBytes;
        await Assert.That(first).IsGreaterThan(0);

        // Frame 2: identical placement, identical payload instance → silence.
        backend.ResetForTests();
        writer.BeginFrame();
        layer.Place(new Rect(2, 0, 10, 4), payload);
        layer.Emit(writer);
        writer.EndFrame();
        await Assert.That(backend.TotalBytes).IsEqualTo(0);

        // Frame 3: the block scrolled, so the rect moved → resend.
        backend.ResetForTests();
        writer.BeginFrame();
        layer.Place(new Rect(2, 3, 10, 4), payload);
        layer.Emit(writer);
        writer.EndFrame();
        await Assert.That(backend.TotalBytes).IsGreaterThan(0);
    }

    [Test]
    public async Task Emit_ResendsEverythingAfterResizeOrInvalidation()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend);
        var layer = new InlineImageLayer { Kind = InlineImageKind.KittyApc, FrameBounds = new Rect(0, 0, 40, 20) };
        byte[] payload = [0x41];

        writer.BeginFrame();
        layer.Place(new Rect(2, 0, 10, 4), payload);
        layer.Emit(writer);
        writer.EndFrame();

        // Same placement, but the frame geometry changed → the terminal may have
        // reflowed, so we cannot assume the bitmap is still there.
        layer.FrameBounds = new Rect(0, 0, 60, 20);
        backend.ResetForTests();
        writer.BeginFrame();
        layer.Place(new Rect(2, 0, 10, 4), payload);
        layer.Emit(writer);
        writer.EndFrame();
        await Assert.That(backend.TotalBytes).IsGreaterThan(0);

        // Explicit invalidation (alt-screen re-entry, hot buffer swap) does the
        // same without a geometry change.
        layer.InvalidateEmitted();
        backend.ResetForTests();
        writer.BeginFrame();
        layer.Place(new Rect(2, 0, 10, 4), payload);
        layer.Emit(writer);
        writer.EndFrame();
        await Assert.That(backend.TotalBytes).IsGreaterThan(0);
    }

    [Test]
    public async Task Emit_ForgetsAPlacementThatLeftTheFrame()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend);
        var layer = new InlineImageLayer { Kind = InlineImageKind.KittyApc, FrameBounds = new Rect(0, 0, 40, 20) };
        byte[] payload = [0x41];

        writer.BeginFrame();
        layer.Place(new Rect(2, 0, 10, 4), payload);
        layer.Emit(writer);
        writer.EndFrame();

        // The block scrolls out of the viewport: the cells under the bitmap are
        // rewritten, so the terminal erased the picture. No placements at all.
        backend.ResetForTests();
        writer.BeginFrame();
        layer.Emit(writer);
        writer.EndFrame();
        await Assert.That(backend.TotalBytes).IsEqualTo(0);

        // It scrolls back to the SAME rect. Index 0 was freed by the empty
        // frame, so this must resend — deduping here would leave a blank hole
        // exactly where the image belongs.
        backend.ResetForTests();
        writer.BeginFrame();
        layer.Place(new Rect(2, 0, 10, 4), payload);
        layer.Emit(writer);
        writer.EndFrame();
        await Assert.That(backend.TotalBytes).IsGreaterThan(0);
    }

    [Test]
    public async Task NoGraphics_FrameIsByteIdenticalToThePre387TextCard()
    {
        var backend = new RecordingBackend();
        var session = new ScreenSession(new AnsiWriter(backend), 40, 20);
        // Images stays at its default InlineImageKind.None — the tmux/pipe/CI
        // case — and the frame must contain no escape payload at all.
        await Assert.That(session.Images.Enabled).IsFalse();

        var timeline = new VirtualizedChatTimeline { InlineImages = session.Images };
        timeline.Append(new ImageBlock("shot.png", "image/png", 2048, ImageBlockTests.PngHeader(640, 480)));

        session.BeginFrame();
        _ = timeline.PrepareFrame(40, 20);
        timeline.Paint(session.PaintBuffer, new Rect(0, 0, 40, 20));
        await session.FlushFrameAsync();

        string frame = Encoding.UTF8.GetString(backend.Writes[0]);
        await Assert.That(frame).DoesNotContain("\u001B_G");
        await Assert.That(frame).DoesNotContain("\u001B]1337");
        await Assert.That(GridDump.Art(session.PaintBuffer)).Contains("◉ shot.png");
    }

    // ── Fullscreen zoom viewer ──────────────────────────────────────────────

    [Test]
    public async Task Viewer_OpenZoomClose_RoundTrips()
    {
        var layer = new InlineImageLayer { Kind = InlineImageKind.KittyApc, FrameBounds = new Rect(0, 0, 80, 24) };
        var block = new ImageBlock("shot.png", "image/png", 2048, ImageBlockTests.PngHeader(640, 480), graphicsAvailable: true);
        var viewer = new ImageViewerOverlay();

        await Assert.That(viewer.Visible).IsFalse();
        viewer.Show(block, layer);
        await Assert.That(viewer.Visible).IsTrue();
        await Assert.That(viewer.Zoom).IsEqualTo(100);

        // Enter is a "close" gesture inside the viewer — the second half of the
        // open/close pair — and it is consumed, never leaked to the agent.
        await Assert.That(viewer.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsTrue();
        await Assert.That(viewer.Visible).IsFalse();
        await Assert.That(viewer.Source.HasNoValue).IsTrue();

        // Zoom resets on reopen: a fresh look, not the last magnification.
        viewer.Show(block, layer);
        viewer.ZoomIn();
        await Assert.That(viewer.Zoom).IsGreaterThan(100);
        viewer.Hide();
        viewer.Show(block, layer);
        await Assert.That(viewer.Zoom).IsEqualTo(100);
    }

    [Test]
    public async Task Viewer_ZoomIsClampedAtBothEnds()
    {
        var viewer = new ImageViewerOverlay();
        viewer.Show(new ImageBlock("s.png", "image/png", 10, ImageBlockTests.PngHeader(8, 8)), null);

        for (int i = 0; i < 40; i++)
        {
            viewer.ZoomIn();
        }

        await Assert.That(viewer.Zoom).IsEqualTo(ImageViewerOverlay.MaxZoom);

        for (int i = 0; i < 80; i++)
        {
            viewer.ZoomOut();
        }

        await Assert.That(viewer.Zoom).IsEqualTo(ImageViewerOverlay.MinZoom);
    }

    [Test]
    public async Task Viewer_KeysAreConsumed_AndHostChordsPassThrough()
    {
        var viewer = new ImageViewerOverlay();
        viewer.Show(new ImageBlock("s.png", "image/png", 10, ImageBlockTests.PngHeader(8, 8)), null);

        await Assert.That(viewer.HandleKey(KeyEvent.Char(new System.Text.Rune('+')))).IsTrue();
        int zoomed = viewer.Zoom;
        await Assert.That(viewer.HandleKey(KeyEvent.Char(new System.Text.Rune('-')))).IsTrue();
        await Assert.That(viewer.Zoom).IsLessThan(zoomed);

        await Assert.That(viewer.HandleKey(KeyEvent.Simple(KeyCode.Up))).IsTrue();
        await Assert.That(viewer.HandleKey(KeyEvent.Simple(KeyCode.Down))).IsTrue();
        await Assert.That(viewer.HandleKey(KeyEvent.Simple(KeyCode.Escape))).IsTrue();
        await Assert.That(viewer.Visible).IsFalse();

        // Closed viewer claims nothing; a Ctrl chord is the host keymap's.
        await Assert.That(viewer.HandleKey(KeyEvent.Char(new System.Text.Rune('q')))).IsFalse();
        viewer.Show(new ImageBlock("s.png", "image/png", 10, ImageBlockTests.PngHeader(8, 8)), null);
        await Assert.That(viewer.HandleKey(
            KeyEvent.Char(new System.Text.Rune('q'), KeyModifiers.Ctrl))).IsFalse();
    }

    [Test]
    public async Task Viewer_PaintsAFrame_AndPlacesTheImage()
    {
        var layer = new InlineImageLayer { Kind = InlineImageKind.KittyApc, FrameBounds = new Rect(0, 0, 60, 20) };
        var block = new ImageBlock("shot.png", "image/png", 2048, ImageBlockTests.PngHeader(640, 480), graphicsAvailable: true);
        var viewer = new ImageViewerOverlay();
        viewer.Show(block, layer);

        var buffer = new ScreenBuffer(60, 20);
        viewer.Paint(buffer, new Rect(0, 0, 60, 20));

        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("shot.png");
        await Assert.That(art).Contains("zoom 100%");
        await Assert.That(art).Contains("+/- zoom | m markup | esc close");
        await Assert.That(art.Contains('┌')).IsTrue();

        await Assert.That(layer.Count).IsEqualTo(1);
        var placement = layer.PlacementAt(0);
        await Assert.That(placement.Cells.Width).IsGreaterThan(0);
        await Assert.That(placement.Cells.Height).IsGreaterThan(0);
        await Assert.That(placement.Cells.Bottom).IsLessThanOrEqualTo(20);
    }

    /// <summary>
    /// The "viewer restores the previous viewport/scroll position and selection
    /// on close" acceptance point, asserted the only honest way: the viewer is
    /// driven through a full open → zoom → close round trip against a real,
    /// scrolled feed, and the feed is byte-for-byte where it was. It holds
    /// because the viewer owns no scroll, no selection and no UiState field at
    /// all — this test is the standing guard on that claim.
    /// </summary>
    [Test]
    public async Task Viewer_CloseLeavesTheFeedExactlyAsItWas()
    {
        var layer = new InlineImageLayer { Kind = InlineImageKind.KittyApc, FrameBounds = new Rect(0, 0, 60, 20) };
        var timeline = new VirtualizedChatTimeline { InlineImages = layer };
        for (int i = 0; i < 6; i++)
        {
            timeline.Append(new UserBlock($"line {i}"));
        }

        _ = timeline.PrepareFrame(60, 20);
        timeline.ScrollBy(3);
        _ = timeline.PrepareFrame(60, 20);

        long scrollBefore = timeline.ScrollY;
        bool followBefore = timeline.FollowTail;
        string artBefore = GridDump.Art(PaintTimeline(timeline, 60, 20));

        var viewer = new ImageViewerOverlay();
        viewer.Show(new ImageBlock("shot.png", "image/png", 10, ImageBlockTests.PngHeader(640, 480), graphicsAvailable: true), layer);

        var viewerBuffer = new ScreenBuffer(60, 20);
        viewer.Paint(viewerBuffer, new Rect(0, 0, 60, 20));
        viewer.ZoomIn();
        viewer.ZoomIn();
        viewer.Paint(viewerBuffer, new Rect(0, 0, 60, 20));
        viewer.HandleKey(KeyEvent.Simple(KeyCode.Escape));

        await Assert.That(viewer.Visible).IsFalse();
        await Assert.That(timeline.ScrollY).IsEqualTo(scrollBefore);
        await Assert.That(timeline.FollowTail).IsEqualTo(followBefore);
        await Assert.That(GridDump.Art(PaintTimeline(timeline, 60, 20))).IsEqualTo(artBefore);
    }

    private static ScreenBuffer PaintTimeline(VirtualizedChatTimeline timeline, int cols, int rows)
    {
        var buffer = new ScreenBuffer(cols, rows);
        timeline.Paint(buffer, new Rect(0, 0, cols, rows));
        return buffer;
    }

    [Test]
    public async Task Viewer_WithoutGraphics_ShowsTheDegradedCard()
    {
        // Same viewer, no protocol: it still opens and paints a frame with the
        // text card inside, rather than being a silent no-op.
        var viewer = new ImageViewerOverlay();
        viewer.Show(new ImageBlock("shot.png", "image/png", 2048, ImageBlockTests.PngHeader(640, 480)), null);

        var buffer = new ScreenBuffer(60, 20);
        viewer.Paint(buffer, new Rect(0, 0, 60, 20));

        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("640×480");
        await Assert.That(art).Contains("shot.png");
    }

    [Test]
    public async Task Viewer_HidesAtSmallGeometry_AndNeverPaintsWhileClosed()
    {
        var viewer = new ImageViewerOverlay();
        viewer.Show(new ImageBlock("shot.png", "image/png", 10, ImageBlockTests.PngHeader(8, 8)), null);

        await Assert.That(viewer.ComputeBox(new Rect(0, 0, 5, 20)).Width).IsEqualTo(0);

        viewer.Hide();
        var buffer = new ScreenBuffer(60, 20);
        viewer.Paint(buffer, new Rect(0, 0, 60, 20));
        await Assert.That(GridDump.Art(buffer).Trim()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task ViewerLayer_IsModal_SoKeysNeverReachTheAgent()
    {
        var viewer = new ImageViewerOverlay();
        var layer = new ImageViewerOverlayLayer(viewer);
        await Assert.That(layer.Id).IsEqualTo(ImageViewerOverlayLayer.LayerId);
        await Assert.That(layer.Visible).IsFalse();

        viewer.Show(new ImageBlock("shot.png", "image/png", 10, ImageBlockTests.PngHeader(8, 8)), null);
        layer.Sync(new Rect(0, 0, 60, 20));
        await Assert.That(layer.Visible).IsTrue();
        await Assert.That(layer.IsModal).IsTrue();
        await Assert.That(layer.Opaque).IsTrue();

        var stack = new OverlayStack();
        stack.Push(layer);
        await Assert.That(stack.HasModalBarrier).IsTrue();

        // A typing key is not consumed by the viewer's own vocabulary, but the
        // modal barrier still stops it from reaching the panels beneath.
        await Assert.That(stack.RouteKey(KeyEvent.Char(new System.Text.Rune('z')))).IsFalse();
        await Assert.That(stack.HasModalBarrier).IsTrue();

        await Assert.That(stack.RouteKey(KeyEvent.Simple(KeyCode.Escape))).IsTrue();
        await Assert.That(viewer.Visible).IsFalse();
        await Assert.That(stack.HasModalBarrier).IsFalse();
    }
}
