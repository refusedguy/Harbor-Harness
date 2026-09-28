using System.Buffers.Binary;
using Harbor.Tui.CellForge.Capabilities;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

public class ImageBlockTests
{
    internal static byte[] PngHeader(uint width, uint height)
    {
        var data = new byte[24];
        Signature(data);
        data[12] = (byte)'I';
        data[13] = (byte)'H';
        data[14] = (byte)'D';
        data[15] = (byte)'R';
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), height);
        return data;

        static void Signature(byte[] d)
        {
            d[0] = 0x89;
            d[1] = 0x50;
            d[2] = 0x4E;
            d[3] = 0x47;
            d[4] = 0x0D;
            d[5] = 0x0A;
            d[6] = 0x1A;
            d[7] = 0x0A;
        }
    }

    [Test]
    public async Task Probe_Reads_IhdrDimensions()
    {
        bool ok = PngProbe.TryReadDimensions(PngHeader(1920, 1080), out var w, out var h);
        await Assert.That(ok).IsTrue();
        await Assert.That(w).IsEqualTo(1920);
        await Assert.That(h).IsEqualTo(1080);
    }

    [Test]
    public async Task Probe_Rejects_SignatureMismatch_AndShortData()
    {
        var bad = PngHeader(10, 10);
        bad[1] = 0x51; // ломаем сигнатуру
        await Assert.That(PngProbe.TryReadDimensions(bad, out _, out _)).IsFalse();
        await Assert.That(PngProbe.TryReadDimensions([1, 2, 3], out _, out _)).IsFalse();
    }

    [Test]
    public async Task Paint_Shows_Name_Dims_AndSize()
    {
        var block = new ImageBlock("shots/screenshot.png", "image/png", 2048, PngHeader(640, 480));
        var buffer = new ScreenBuffer(40, 2);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 40, 2), 0));

        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("◉ screenshot.png");
        await Assert.That(art).Contains("640×480");
        await Assert.That(art).Contains("2 KB");
    }

    [Test]
    public async Task Jpeg_Data_YieldsDimensions_ViaFallbackProbe()
    {
        byte[] jpeg = JpegProbeTests.Jpeg(1280, 720);
        var block = new ImageBlock("shots/photo.jpg", "image/jpeg", 4096, jpeg);
        await Assert.That(block.HasPngHeader).IsFalse();
        await Assert.That(block.HasJpegHeader).IsTrue();
        await Assert.That(block.Dimensions).IsEqualTo("1280×720");

        // MIME image/* без узнаваемого заголовка держится на строке формата.
        var opaque = new ImageBlock("f.webp", "image/webp", 512, [0x52, 0x49, 0x46, 0x46, 0x24, 0x00]);
        await Assert.That(opaque.HasJpegHeader).IsFalse();
        await Assert.That(opaque.Dimensions).IsNull();
        await Assert.That(opaque.SummaryLine()).IsEqualTo("image/webp · 512 B");
    }

    [Test]
    public async Task NonImage_DataWithoutPng_FallsBackToMimeLine()
    {
        var pdf = new ImageBlock("report.pdf", "application/pdf", 64 * 1024, null);
        await Assert.That(pdf.IsImage).IsFalse();
        await Assert.That(pdf.HasPngHeader).IsFalse();
        await Assert.That(pdf.SummaryLine()).IsEqualTo("application/pdf · 64 KB");

        var pngNoData = new ImageBlock("a.png", "image/png", 128, null);
        await Assert.That(pngNoData.Dimensions).IsNull();

        string kindKind = new ImageBlock("f.png", "image/png", 1, PngHeader(2, 2)).Kind;
        await Assert.That(kindKind).IsEqualTo("image");
    }

    // ── #387: inline graphics through the terminal's own protocol ────────────

    /// <summary>
    /// Test double for the session's image sink. <see cref="Enabled" /> derives
    /// from the protocol, exactly like the real <c>InlineImageLayer</c> does,
    /// so a "None protocol" fixture is genuinely disabled instead of merely
    /// claiming to be — which is what the tmux-fallback case needs to be worth
    /// anything. The settable property exists only to exercise the block's own
    /// guard directly.
    /// </summary>
    private sealed class RecordingSink : IInlineImageSink
    {
        private readonly InlineImageKind _kind;

        public RecordingSink(InlineImageKind kind)
        {
            _kind = kind;
            Enabled = kind != InlineImageKind.None;
        }

        public bool Enabled { get; set; }

        public Rect FrameBounds { get; set; } = new(0, 0, 10_000, 10_000);

        public List<(Rect Cells, string Payload)> Placed { get; } = [];

        public int EncodeCalls { get; private set; }

        public bool TryEncode(string name, string mimeType, ReadOnlySpan<byte> data, int cols, int rows, out byte[]? payload)
        {
            EncodeCalls++;
            payload = InlineImageEncoder.Encode(_kind, name, data, cols, rows);
            return payload is { Length: > 0 };
        }

        public Rect ClipToFrame(Rect cellRect) => cellRect.Intersect(FrameBounds);

        public void Place(Rect cellRect, ReadOnlyMemory<byte> payload) =>
            Placed.Add((cellRect, System.Text.Encoding.UTF8.GetString(payload.Span)));
    }

    [Test]
    public async Task Graphics_Off_KeepsTheTwoLineTextCard()
    {
        var block = new ImageBlock("shots/screenshot.png", "image/png", 2048, PngHeader(640, 480));
        var sink = new RecordingSink(InlineImageKind.None);
        var buffer = new ScreenBuffer(40, 6);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 40, 6), 0, inlineImages: sink));

        await Assert.That(sink.Placed).IsEmpty();
        await Assert.That(sink.EncodeCalls).IsEqualTo(0);
        await Assert.That(GridDump.Art(buffer)).Contains("◉ screenshot.png");
        await Assert.That(GridDump.Art(buffer)).Contains("640×480");
        // Measure keeps the pre-#387 height so the timeline layout is untouched.
        await Assert.That(block.Measure(40).MinLines).IsEqualTo(2);
    }

    /// <summary>
    /// Regression guard for the layout hole this slice nearly shipped: a
    /// block that reserved GRAPHIC height while painting the two-line card
    /// left a dozen blank rows in the feed of every text-only terminal
    /// (pipes, CI, tmux/screen). Reserved height and painted content must
    /// agree, and the only thing that may change the reservation is the
    /// capability sampled at construction.
    /// </summary>
    [Test]
    public async Task Measure_ReservesGraphicHeightOnlyWhenTheSessionCanDrawIt()
    {
        var textCard = new ImageBlock("a.png", "image/png", 2048, PngHeader(640, 480));
        var graphics = new ImageBlock("b.png", "image/png", 2048, PngHeader(640, 480), graphicsAvailable: true);

        await Assert.That(textCard.GraphicsAvailable).IsFalse();
        await Assert.That(textCard.GraphicHeight(40)).IsEqualTo(0);
        await Assert.That(textCard.Measure(40).MinLines).IsEqualTo(2);

        await Assert.That(graphics.GraphicsAvailable).IsTrue();
        int rows = graphics.GraphicHeight(40);
        await Assert.That(rows).IsGreaterThan(2);
        await Assert.That(graphics.Measure(40).MinLines).IsEqualTo(rows + 1);

        // The raw fit is protocol-independent — the viewer uses it so a
        // card-sized hole in the feed can never cap the zoom.
        await Assert.That(textCard.FittedGraphicHeight(40)).IsEqualTo(rows);
    }

    [Test]
    public async Task Graphics_On_PlacesScaledPayload_AndKeepsTheCaption()
    {
        var block = new ImageBlock("shots/screenshot.png", "image/png", 2048, PngHeader(640, 480), graphicsAvailable: true);
        var sink = new RecordingSink(InlineImageKind.KittyApc);
        const int H = 20;
        var buffer = new ScreenBuffer(40, H);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 40, H), 0, inlineImages: sink));

        await Assert.That(sink.Placed.Count).IsEqualTo(1);
        var (cells, payload) = sink.Placed[0];

        // 640×480 at 38 usable columns ⇒ 480/640 × 38 / 2 (cell aspect) ≈ 14
        // rows, always inside the rect the block was handed.
        await Assert.That(cells.Width).IsEqualTo(38);
        await Assert.That(cells.Height).IsEqualTo(14);
        await Assert.That(cells.Y).IsGreaterThanOrEqualTo(0);
        await Assert.That(cells.Bottom).IsLessThanOrEqualTo(H);
        await Assert.That(payload).StartsWith("\u001B_Gf=100,a=T,C=1,c=38,r=14,");

        // Caption survives under the bitmap — that is the whole point of a card.
        await Assert.That(GridDump.Art(buffer)).Contains("640×480");
        // The graphic rows are blanked so no stale card text shows through.
        string topRow = GridDump.Art(buffer).Split('\n')[0];
        await Assert.That(topRow.Trim()).IsEqualTo(string.Empty);
        // ...while the caption row itself is still text.
        await Assert.That(GridDump.Art(buffer).Split('\n')[H - 1]).Contains("640×480");
    }

    [Test]
    public async Task Graphics_On_ReusesTheEncodedPayload_AcrossFrames()
    {
        var block = new ImageBlock("shots/screenshot.png", "image/png", 2048, PngHeader(640, 480), graphicsAvailable: true);
        var sink = new RecordingSink(InlineImageKind.Osc1337);
        var buffer = new ScreenBuffer(40, 20);

        for (int tick = 0; tick < 3; tick++)
        {
            block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 40, 20), tick, inlineImages: sink));
        }

        // Three frames, one encode: the payload is cached per block, not rebuilt
        // per frame (the "no image byte array retained per frame" contract).
        await Assert.That(sink.EncodeCalls).IsEqualTo(1);
        await Assert.That(sink.Placed.Count).IsEqualTo(3);
        await Assert.That(sink.Placed[0].Payload).Contains("width=38;height=14;");
    }

    [Test]
    public async Task Graphics_On_TmuxFallback_StaysOnTheTextCard()
    {
        // The probe refuses inside a multiplexer; a session started under tmux
        // therefore hands the block a disabled sink. Same block, same bytes,
        // text card — the behaviour tmux users already have.
        InlineImageKind kind = InlineImageProbe.Detect(name => name == "TMUX" ? "1" : null);
        await Assert.That(kind).IsEqualTo(InlineImageKind.None);

        var block = new ImageBlock("shots/screenshot.png", "image/png", 2048, PngHeader(640, 480));
        var sink = new RecordingSink(kind);
        var buffer = new ScreenBuffer(40, 6);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 40, 6), 0, inlineImages: sink));

        await Assert.That(sink.Enabled).IsFalse();
        await Assert.That(sink.Placed).IsEmpty();
        await Assert.That(GridDump.Art(buffer)).Contains("◉ screenshot.png");
        // ...and it reserves the card height, not a graphic-sized hole.
        await Assert.That(block.Measure(40).MinLines).IsEqualTo(2);
    }

    [Test]
    public async Task Graphics_On_CorruptHeader_DegradesToCardWithWarningMarker()
    {
        // A .png whose bytes are not a PNG: mime claims an image, no header
        // parses. Must degrade to the card, never throw. Graphics are AVAILABLE
        // here, so the bad header — not the protocol — is what forces the card.
        var block = new ImageBlock("shots/broken.png", "image/png", 900, [0x00, 0x01, 0x02, 0x03, 0x04], graphicsAvailable: true);
        await Assert.That(block.IsDamaged).IsTrue();
        await Assert.That(block.PixelWidth).IsEqualTo(0);

        var sink = new RecordingSink(InlineImageKind.KittyApc);
        var buffer = new ScreenBuffer(40, 6);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 40, 6), 0, inlineImages: sink));

        await Assert.That(sink.Placed).IsEmpty();
        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("◉ broken.png");
        await Assert.That(art).Contains("⚠");
    }

    [Test]
    public async Task Graphics_On_KittyRefusesJpeg_KeepsTheCard()
    {
        // kitty speaks PNG only (f=100). A JPEG under kitty must degrade to
        // the card rather than emit a payload the terminal will reject. The
        // box is TALL enough for the graphic path, so the mime — not the
        // geometry — is what forces the fallback.
        byte[] jpeg = JpegProbeTests.Jpeg(800, 600);
        var block = new ImageBlock("shots/photo.jpg", "image/jpeg", 4096, jpeg, graphicsAvailable: true);
        var sink = new RecordingSink(InlineImageKind.KittyApc);
        var buffer = new ScreenBuffer(40, 20);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 40, 20), 0, inlineImages: sink));

        await Assert.That(block.GraphicHeight(40)).IsGreaterThan(0); // the gate passed
        await Assert.That(sink.Placed).IsEmpty();
        await Assert.That(GridDump.Art(buffer)).Contains("◉ photo.jpg");
    }

    [Test]
    public async Task Graphics_On_FailedEncodeIsMemoized_NotRetriedEveryFrame()
    {
        byte[] jpeg = JpegProbeTests.Jpeg(800, 600);
        var block = new ImageBlock("shots/photo.jpg", "image/jpeg", 4096, jpeg, graphicsAvailable: true);
        var sink = new RecordingSink(InlineImageKind.KittyApc);
        var buffer = new ScreenBuffer(40, 20);

        for (int tick = 0; tick < 4; tick++)
        {
            block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 40, 20), tick, inlineImages: sink));
        }

        // A payload the protocol cannot carry must cost ONE attempt, not one
        // per frame — the miss is memoized exactly like the hit.
        await Assert.That(sink.EncodeCalls).IsEqualTo(1);
    }

    [Test]
    public async Task GraphicHeight_DerivesFromProbedPixels_AndClamps()
    {
        var wide = new ImageBlock("a.png", "image/png", 100, PngHeader(1000, 100), graphicsAvailable: true);
        await Assert.That(wide.GraphicHeight(60)).IsEqualTo(3); // 58 × 0.1 / 2 ≈ 2.9
        await Assert.That(wide.GraphicHeight(60)).IsLessThanOrEqualTo(ImageBlock.MaxGraphicRows);

        var tall = new ImageBlock("b.png", "image/png", 100, PngHeader(100, 4000), graphicsAvailable: true);
        await Assert.That(tall.GraphicHeight(60)).IsEqualTo(ImageBlock.MaxGraphicRows);

        // No probed pixels ⇒ no graphic, whatever the session can draw.
        var corrupt = new ImageBlock("c.png", "image/png", 100, [1, 2, 3], graphicsAvailable: true);
        await Assert.That(corrupt.GraphicHeight(60)).IsEqualTo(0);
    }
}

public class JpegProbeTests
{
    /// <summary>FFD8 + APP0-заглушка + SOF-сегмент с указанными размерами.</summary>
    internal static byte[] Jpeg(ushort width, ushort height, byte sofMarker = 0xC0, bool padBeforeSof = false)
    {
        var data = new List<byte>(24) { 0xFF, 0xD8 };
        if (padBeforeSof)
        {
            data.AddRange([0xFF, 0xE0, 0x00, 0x04, 0x4A]); // APP0 с усечённым контентом
        }

        data.Add(0xFF);
        if (padBeforeSof)
        {
            data.Add(0xFF); // забивной байт перед кодом маркера
        }

        data.AddRange([sofMarker, 0x00, 0x11, 0x08]);
        data.Add((byte)(height >> 8));
        data.Add((byte)height);
        data.Add((byte)(width >> 8));
        data.Add((byte)width);
        data.AddRange([0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01, 0xFF, 0xDA, 0xFF, 0xD9]);
        return [.. data];
    }

    [Test]
    public async Task Probe_ReadsBaselineAndProgressiveDimensions()
    {
        bool baseline = JpegProbe.TryReadDimensions(Jpeg(1920, 1080), out var w, out var h);
        await Assert.That(baseline).IsTrue();
        await Assert.That(w).IsEqualTo(1920);
        await Assert.That(h).IsEqualTo(1080);

        // SOF0 хранит height раньше width — проверяем перепутывание порядка.
        await Assert.That(JpegProbe.TryReadDimensions(Jpeg(480, 700, sofMarker: 0xC2), out w, out h)).IsTrue();
        await Assert.That(w).IsEqualTo(480);
        await Assert.That(h).IsEqualTo(700);
    }

    [Test]
    public async Task Probe_ToleratesFillBytes_AndSkipsSegments()
    {
        await Assert.That(JpegProbe.TryReadDimensions(Jpeg(640, 480, padBeforeSof: true), out var w, out _)).IsTrue();
        await Assert.That(w).IsEqualTo(640);
    }

    [Test]
    public async Task Probe_RejectsSignatureTruncationZeroDimsAndSosFirst()
    {
        await Assert.That(JpegProbe.TryReadDimensions([], out _, out _)).IsFalse();
        await Assert.That(JpegProbe.TryReadDimensions([0xFF, 0xD9], out _, out _)).IsFalse();

        byte[] truncated = Jpeg(10, 10)[..8]; // обрыв внутри SOF
        await Assert.That(JpegProbe.TryReadDimensions(truncated, out _, out _)).IsFalse();

        byte[] zeros = Jpeg(0, 42);
        await Assert.That(JpegProbe.TryReadDimensions(zeros, out _, out _)).IsFalse();

        byte[] noSof = [0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02]; // SOS без SOF → метаданных нет
        await Assert.That(JpegProbe.TryReadDimensions(noSof, out _, out _)).IsFalse();
    }
}
