namespace Harbor.Ui.Framework.Tests;

using System.Collections.Immutable;
using System.Text;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

/// <summary>
///     Portable cell-diff protocol tests (renderer-unification sprint Phase
///     6.2): encoder fast paths, codec round-trips incl. V1 backward
///     compatibility, and the pipeline's producer/consumer contract.
/// </summary>
public class CellDiffProtocolTests
{
    private static ScreenBuffer MakeBuffer(int cols, int rows, char fill) =>
        FillBuffer(new ScreenBuffer(cols, rows), fill);

    private static ScreenBuffer FillBuffer(ScreenBuffer buffer, char fill)
    {
        for (int y = 0; y < buffer.Rows; y++)
        {
            buffer.SetText(0, y, new string(fill, buffer.Cols), CellStyle.Plain);
        }

        return buffer;
    }

    [Test]
    public async Task Encode_AllRowsIdentical_ProducesEmptyBatch()
    {
        var encoder = new RowHashDiffEncoder();
        var prev = MakeBuffer(20, 5, 'a');
        var next = MakeBuffer(20, 5, 'a');

        CellDiffBatch batch = encoder.Encode(prev, next, hints: null, sequence: 1);

        await Assert.That(batch.IsEmpty).IsTrue();
        await Assert.That(batch.Version).IsEqualTo(CellDiffProtocolVersion.V1);
        await Assert.That(batch.Sequence).IsEqualTo(1L);
    }

    [Test]
    public async Task Encode_SingleCellChange_ReportsExactlyOneChange()
    {
        var encoder = new RowHashDiffEncoder();
        var prev = MakeBuffer(20, 5, 'a');
        var next = MakeBuffer(20, 5, 'a');
        next.SetRune(3, 2, new Rune('X'), CellStyle.Plain);

        CellDiffBatch batch = encoder.Encode(prev, next, hints: null, sequence: 7);

        await Assert.That(batch.Changes.Length).IsEqualTo(1);
        await Assert.That(batch.Changes[0].X).IsEqualTo(3);
        await Assert.That(batch.Changes[0].Y).IsEqualTo(2);
        await Assert.That(batch.Changes[0].NewCell.Rune).IsEqualTo((int)'X');
    }

    [Test]
    public async Task Encode_WithHints_EmitsV2AndRestrictsScan()
    {
        var encoder = new RowHashDiffEncoder();
        var prev = MakeBuffer(40, 20, 'a');
        var next = MakeBuffer(40, 20, 'a');
        // Change OUTSIDE the hint rect: hint-restricted scan must not see it.
        next.SetRune(0, 19, new Rune('Z'), CellStyle.Plain);
        // Change INSIDE the hint rect: must be reported.
        next.SetRune(2, 1, new Rune('Y'), CellStyle.Plain);

        var hints = new List<Rect> { new(0, 0, 10, 3) };
        CellDiffBatch batch = encoder.Encode(prev, next, hints, sequence: 2);

        await Assert.That(batch.Version).IsEqualTo(CellDiffProtocolVersion.V2);
        await Assert.That(batch.FrameHints.Length).IsEqualTo(1);
        await Assert.That(batch.Changes.Length).IsEqualTo(1);
        await Assert.That(batch.Changes[0].NewCell.Rune).IsEqualTo((int)'Y');
    }

    [Test]
    public async Task Encode_HintsAboveThreshold_FallsBackToFullScan()
    {
        var encoder = new RowHashDiffEncoder();
        var prev = MakeBuffer(10, 10, 'a');
        var next = MakeBuffer(10, 10, 'a');
        next.SetRune(0, 0, new Rune('Z'), CellStyle.Plain); // outside the (huge) hint

        // A hint covering >25% of the 10x10 screen forces a full scan; the
        // batch downgrades to V1 (no hints) because the reported damage rects
        // would under-invalidate a hint-driven consumer.
        var hints = new List<Rect> { new(0, 0, 10, 6) };
        CellDiffBatch batch = encoder.Encode(prev, next, hints, sequence: 3);

        await Assert.That(batch.Version).IsEqualTo(CellDiffProtocolVersion.V1);
        await Assert.That(batch.FrameHints.IsEmpty).IsTrue();
        await Assert.That(batch.Changes.Length).IsEqualTo(1);
        await Assert.That(batch.Changes[0].NewCell.Rune).IsEqualTo((int)'Z');
    }

    [Test]
    public async Task Codec_RoundTripsV2Batch()
    {
        var changes = ImmutableArray.Create(
            new CellDiffMessage(1, 2, Cell.Blank, Cell.From(new Rune('Ж'), CellStyle.Plain)),
            new CellDiffMessage(4, 5, Cell.Blank, Cell.Blank));
        var hints = ImmutableArray.Create(new Rect(0, 1, 8, 3));
        var batch = new CellDiffBatch(
            CellDiffProtocolVersion.V2, 42, 80, 24, changes, hints);

        CellDiffBatch decoded = CellDiffBatchCodec.Decode(CellDiffBatchCodec.Encode(batch));

        await Assert.That(decoded.Version).IsEqualTo(CellDiffProtocolVersion.V2);
        await Assert.That(decoded.Sequence).IsEqualTo(42L);
        await Assert.That(decoded.Cols).IsEqualTo(80);
        await Assert.That(decoded.Rows).IsEqualTo(24);
        await Assert.That(decoded.Changes.Length).IsEqualTo(2);
        await Assert.That(decoded.Changes[0].NewCell.Rune).IsEqualTo((int)'Ж');
        await Assert.That(decoded.FrameHints.Length).IsEqualTo(1);
        await Assert.That(decoded.FrameHints[0]).IsEqualTo(new Rect(0, 1, 8, 3));
    }

    [Test]
    public async Task Codec_V1BatchDecodesBackwardCompatibly()
    {
        // A V1 batch (no hints section) must decode through the same codec —
        // the protocol's backward-compatibility contract.
        var changes = ImmutableArray.Create(new CellDiffMessage(0, 0, Cell.Blank, Cell.Blank));
        var v1 = new CellDiffBatch(
            CellDiffProtocolVersion.V1, 1, 10, 10, changes, ImmutableArray<Rect>.Empty);

        CellDiffBatch decoded = CellDiffBatchCodec.Decode(CellDiffBatchCodec.Encode(v1));

        await Assert.That(decoded.Version).IsEqualTo(CellDiffProtocolVersion.V1);
        await Assert.That(decoded.Changes.Length).IsEqualTo(1);
        await Assert.That(decoded.FrameHints.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Pipeline_RendersDifferentialFramesToSinks()
    {
        var pipeline = new DifferentialRenderPipeline();
        var batches = new List<CellDiffBatch>();
        pipeline.Subscribe(new CollectingSink(batches));

        // Frame 1: full repaint of the initial content.
        var first = MakeBuffer(10, 2, 'a');
        CellDiffBatch b1 = pipeline.Render(first);
        await Assert.That(b1.Sequence).IsEqualTo(1L);
        await Assert.That(b1.Changes.Length).IsEqualTo(20);

        // Frame 2: one cell changes — differential.
        var second = FillBuffer(new ScreenBuffer(10, 2), 'a');
        second.SetRune(5, 1, new Rune('Z'), CellStyle.Plain);
        CellDiffBatch b2 = pipeline.Render(second);
        await Assert.That(b2.Sequence).IsEqualTo(2L);
        await Assert.That(b2.Changes.Length).IsEqualTo(1);
        await Assert.That(b2.Changes[0].NewCell.Rune).IsEqualTo((int)'Z');

        await Assert.That(batches.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Pipeline_ApplyTo_ReplaysBatchOntoConsumerBuffer()
    {
        var pipeline = new DifferentialRenderPipeline();
        var producer = FillBuffer(new ScreenBuffer(10, 2), 'a');
        _ = pipeline.Render(producer);

        producer.SetRune(3, 0, new Rune('W'), CellStyle.Plain);
        CellDiffBatch batch = pipeline.Render(producer);

        var consumer = new ScreenBuffer(10, 2);
        DifferentialRenderPipeline.ApplyTo(batch, consumer);

        await Assert.That(consumer.Get(3, 0).Rune).IsEqualTo((int)'W');
        await Assert.That(consumer.Get(0, 0).Rune).IsEqualTo((int)' '); // untouched cell is still blank
    }

    private sealed class CollectingSink(List<CellDiffBatch> batches) : ICellDiffSink
    {
        public void Accept(in CellDiffBatch batch) => batches.Add(batch);
    }
}
