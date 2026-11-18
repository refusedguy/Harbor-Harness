using System.Buffers.Binary;
using Harbor.TestKit;
using Harbor.Tui.CellForge.Generic.Widgets;
using Harbor.Ui.Framework.Rendering;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Painting reads the global TerminalColorPalette — serialized with other paint tests.
[NotInParallel("pty")]
public class AttachmentCardTests
{
    private static string FixtureDir() => Golden.FixtureDir("attachment-card");

    private static byte[] Png(uint width, uint height)
    {
        var data = new byte[24];
        data[0] = 0x89; data[1] = (byte)'P'; data[2] = (byte)'N'; data[3] = (byte)'G';
        data[4] = 0x0D; data[5] = 0x0A; data[6] = 0x1A; data[7] = 0x0A;
        data[12] = (byte)'I'; data[13] = (byte)'H'; data[14] = (byte)'D'; data[15] = (byte)'R';
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), height);
        return data;
    }

    /// <summary>
    /// Minimal sink double: it reports the enabled state the host chose and
    /// records placements. Payload bytes are placeholders — the card must not
    /// care what the protocol carries, only that ImageBlock used the sink.
    /// </summary>
    private sealed class RecordingSink(bool enabled) : IInlineImageSink
    {
        public bool Enabled { get; set; } = enabled;

        public Rect FrameBounds { get; set; } = new(0, 0, 10_000, 10_000);

        public List<Rect> Placed { get; } = [];

        public int EncodeCalls { get; private set; }

        public bool TryEncode(string name, string mimeType, ReadOnlySpan<byte> data, int cols, int rows, out byte[]? payload) =>
            TryEncode(name, mimeType, data, cols, rows, 0, out payload);

        public bool TryEncode(string name, string mimeType, ReadOnlySpan<byte> data, int cols, int rows, int zIndex, out byte[]? payload)
        {
            EncodeCalls++;
            payload = [1, 2, 3];
            return true;
        }

        public Rect ClipToFrame(Rect cellRect) => cellRect.Intersect(FrameBounds);

        public void Place(Rect cellRect, ReadOnlyMemory<byte> payload) => Placed.Add(cellRect);

        public void Place(Rect cellRect, ReadOnlyMemory<byte> payload, int zIndex) => Placed.Add(cellRect);
    }

    private static async Task VerifyGolden(string name, AttachmentCard card, int width, int height)
    {
        var buffer = TestRender.Buffer(width, height);
        card.Paint(buffer, new Rect(0, 0, width, height));
        string[] split = TestRender.Art(buffer).Split('\n');
        var lines = new string[height];
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = (i < split.Length ? split[i] : string.Empty).TrimEnd();
        }

        string actual = $"# golden: {name} ({width}x{height})\n" + string.Join("\n", lines) + "\n";
        string expected = Golden.VerifyText(FixtureDir(), name, actual);
        await Assert.That(Golden.Normalize(actual)).IsEqualTo(expected);
    }

    [Test]
    public async Task Record_IsExternalAndRefreshable()
    {
        var first = new AttachmentRecord("a.png", "image/png", 10, Path: "/tmp/a.png");
        var card = new AttachmentCard(first);
        AttachmentRecord? seen = null;
        card.Preview = record => { seen = record; return null; };
        _ = card.Preview!(first);

        await Assert.That(ReferenceEquals(seen, first)).IsTrue();

        var second = new AttachmentRecord("b.txt", "text/plain", 512);
        card.SetRecord(second);
        _ = card.Preview!(second);
        await Assert.That(card.Name).IsEqualTo("b.txt");
        await Assert.That(ReferenceEquals(seen, second)).IsTrue();
    }

    [Test]
    public async Task Preview_CallbackReceivesRecordAndPaintsLines()
    {
        var record = new AttachmentRecord("report.pdf", "application/pdf", 64 * 1024);
        var card = new AttachmentCard(record)
        {
            Preview = r => new AttachmentPreview(["pages=" + r.Name, "ok"]),
        };
        var buffer = TestRender.Buffer(40, 6);
        card.Paint(buffer, new Rect(0, 0, 40, 6));
        string art = TestRender.Art(buffer);

        await Assert.That(art).Contains("pages=report.pdf");
        await Assert.That(art).Contains("ok");
    }

    [Test]
    public async Task Actions_ActivateInvokesHostCallbackWithCurrentRecord()
    {
        var record = new AttachmentRecord("shot.png", "image/png", 1024);
        AttachmentRecord? invoked = null;
        var card = new AttachmentCard(record)
        {
            Actions =
            [
                new AttachmentAction("Open", r => invoked = r),
                new AttachmentAction("Copy", _ => { }),
            ],
        };

        await Assert.That(card.ActivateAction(0)).IsTrue();
        await Assert.That(ReferenceEquals(invoked, record)).IsTrue();
        await Assert.That(card.ActivateAction(2)).IsFalse();
        await Assert.That(card.ActivateAction(-1)).IsFalse();
    }

    [Test]
    public async Task Measure_CountsHeaderPreviewAndActionRows()
    {
        var record = new AttachmentRecord("a.png", "image/png", 8, Data: Png(100, 50));
        var card = new AttachmentCard(record)
        {
            Preview = r => new AttachmentPreview(ImageBytes: r.Data, ImageMimeType: r.MimeType),
            Actions = [new AttachmentAction("Open", _ => { })],
        };

        BlockMeasure measure = card.Measure(40);
        await Assert.That(measure.IsExact).IsTrue();
        await Assert.That(measure.MinLines).IsEqualTo(14);
    }

    [Test]
    public async Task ImagePreview_DelegatesToInlineImageSink()
    {
        var record = new AttachmentRecord("shot.png", "image/png", 4096, Data: Png(100, 50));
        var card = new AttachmentCard(record)
        {
            Preview = r => new AttachmentPreview(ImageBytes: r.Data, ImageMimeType: r.MimeType),
        };
        var sink = new RecordingSink(true);
        var buffer = TestRender.Buffer(40, 20);

        card.Paint(buffer, new Rect(0, 0, 40, 20), sink);

        await Assert.That(sink.EncodeCalls).IsGreaterThan(0);
        await Assert.That(sink.Placed.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ImagePreview_DisabledSink_KeepsOnlyTextCard()
    {
        var record = new AttachmentRecord("shot.png", "image/png", 4096, Data: Png(100, 50));
        var card = new AttachmentCard(record)
        {
            Preview = r => new AttachmentPreview(ImageBytes: r.Data, ImageMimeType: r.MimeType),
        };
        var sink = new RecordingSink(false);
        var buffer = TestRender.Buffer(40, 20);

        card.Paint(buffer, new Rect(0, 0, 40, 20), sink);

        await Assert.That(sink.EncodeCalls).IsEqualTo(0);
        await Assert.That(sink.Placed).IsEmpty();
        await Assert.That(TestRender.Art(buffer)).Contains("▣ shot.png");
    }

    [Test]
    public async Task Golden_TextPreviewAndAction()
    {
        var record = new AttachmentRecord("report.pdf", "application/pdf", 64 * 1024);
        var card = new AttachmentCard(record)
        {
            Preview = _ => new AttachmentPreview(["pages=12", "encrypted=no"]),
            Actions = [new AttachmentAction("Open", _ => { })],
        };

        await VerifyGolden("attachment-card-text", card, 32, 6);
    }

    [Test]
    public async Task Golden_MinimalCard()
    {
        var card = new AttachmentCard(new AttachmentRecord("notes.txt", "text/plain", 512));

        await VerifyGolden("attachment-card-minimal", card, 24, 3);
    }
}
