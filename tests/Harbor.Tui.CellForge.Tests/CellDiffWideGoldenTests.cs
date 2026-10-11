using System.Text;
using Harbor.Ui.Framework.Rendering.Protocol;

namespace Harbor.Tui.CellForge.Tests;

// R1 steal (epic #1155, issue #1156): wide/VS16 golden tests over the
// TestBackend harness (R2, #1194) — the per-line assert idiom. Encoding goes
// through the portable RowHashDiffEncoder against TestBackend buffers;
// applying every message to a mirror of the previous frame must converge it
// to the next frame's lines. A missing force-refresh shows up as a line
// mismatch here, not as a silent terminal stain.
public class CellDiffWideGoldenTests
{
    private static readonly CellStyle BlueBg = new(bg: PackedColor.Indexed(4));

    private static async Task AssertConverges(TestBackend prev, TestBackend next)
    {
        var encoder = new RowHashDiffEncoder();
        CellDiffBatch batch = encoder.Encode(prev.Buffer, next.Buffer, hints: null, sequence: 1);

        var mirror = TestBackend.WithLines(prev.Lines());
        foreach (var change in batch.Changes)
        {
            mirror.Buffer.At(change.X, change.Y) = change.NewCell;
        }

        await mirror.AssertBufferLinesAsync(next.Lines());
    }

    [Test]
    public async Task Golden_CjkReplace_Converges()
    {
        var prev = new TestBackend(8, 2);
        prev.Buffer.SetText(0, 0, "a中b文c", CellStyle.Plain);
        prev.Buffer.SetText(0, 1, "hello", CellStyle.Plain);
        var next = new TestBackend(8, 2);
        next.Buffer.SetText(0, 0, "中文ab", CellStyle.Plain);
        next.Buffer.SetText(0, 1, "world!", CellStyle.Plain);

        await AssertConverges(prev, next);
    }

    [Test]
    public async Task Golden_StyledWideToNarrow_Converges()
    {
        // "你好，世界！"(Blue) → "Hello": the terminal kept blue on the
        // trailing columns; the batch must carry their refresh.
        var prev = new TestBackend(12, 1);
        prev.Buffer.SetText(0, 0, "你好，世界！", CellStyle.Plain);
        for (int x = 0; x < prev.Buffer.Cols; x++)
        {
            prev.Buffer.SetStyleAt(x, 0, BlueBg);
        }

        var next = new TestBackend(12, 1);
        next.Buffer.SetText(0, 0, "Hello", CellStyle.Plain);

        await AssertConverges(prev, next);
    }

    [Test]
    public async Task Golden_StyledWideToNarrow_IncrementalTail_Converges()
    {
        // Incremental shape: the lead rewritten in place, the tail cell kept
        // identical — only the force arm refreshes it. The message-level
        // assert is the pin (line art is style-blind, so it converges either
        // way); on a real terminal the missing message is a blue stain.
        var prev = TestBackend.WithLines("中b");
        prev.Buffer.SetStyleAt(0, 0, BlueBg);
        var next = TestBackend.WithLines("中b");
        next.Buffer.SetStyleAt(0, 0, BlueBg);
        next.Buffer.At(0, 0) = Cell.From(new Rune('a'), CellStyle.Plain);
        next.Buffer.InvalidateAll();

        var encoder = new RowHashDiffEncoder();
        CellDiffBatch batch = encoder.Encode(prev.Buffer, next.Buffer, hints: null, sequence: 1);

        await Assert.That(batch.Changes.Any(m => m.X == 1)).IsTrue();

        var mirror = TestBackend.WithLines(prev.Lines());
        foreach (var change in batch.Changes)
        {
            mirror.Buffer.At(change.X, change.Y) = change.NewCell;
        }

        await mirror.AssertBufferLinesAsync(next.Lines());
    }

    [Test]
    public async Task Golden_WideStyleRemoved_Converges()
    {
        var prev = TestBackend.WithLines("中ab");
        prev.Buffer.SetStyleAt(0, 0, BlueBg);
        var next = TestBackend.WithLines("中ab");

        var encoder = new RowHashDiffEncoder();
        CellDiffBatch batch = encoder.Encode(prev.Buffer, next.Buffer, hints: null, sequence: 1);

        // Trailing clear first, lead repaint second (ratatui #2652 order).
        await Assert.That(batch.Changes.Length).IsEqualTo(2);
        await Assert.That(batch.Changes[0].X).IsEqualTo(1);
        await Assert.That(batch.Changes[1].X).IsEqualTo(0);

        await AssertConverges(prev, next);
    }

    [Test]
    public async Task Golden_Vs16Line_RoundTripsAsPainted()
    {
        // Paint-model pin (ENG9): VS16 clusters measure per rune — the
        // cluster paints its base narrow and drops the selector. The golden
        // pins that the diff round-trips exactly what the paint produced
        // (no phantom trailing, no drift), under the harness idiom.
        var prev = TestBackend.WithLines("a❤️b", "plain");
        var next = TestBackend.WithLines("a❤️c", "plain");

        await AssertConverges(prev, next);
        await prev.AssertBufferLinesAsync("a❤️b", "plain");
    }

    [Test]
    public async Task Golden_SkipAndForcedWidth_Converge()
    {
        var prev = TestBackend.WithLines("abcdef");
        var next = TestBackend.WithLines("xBCdeF");
        next.Buffer.SetDiffOption(1, 0, CellDiffOption.Skip);
        next.Buffer.SetDiffOption(2, 0, CellDiffOption.Skip);
        next.Buffer.SetDiffOption(5, 0, CellDiffOption.ForcedWidth, 1);

        var encoder = new RowHashDiffEncoder();
        CellDiffBatch batch = encoder.Encode(prev.Buffer, next.Buffer, hints: null, sequence: 1);

        // Skipped cells never encode, even when changed.
        await Assert.That(batch.Changes.Any(m => m.X == 1 || m.X == 2)).IsFalse();
        await Assert.That(batch.Changes.Any(m => m.X == 0)).IsTrue();

        // The mirror converges everywhere EXCEPT the skipped columns, which
        // are out-of-band by contract — pin the boundary explicitly.
        var mirror = TestBackend.WithLines(prev.Lines());
        foreach (var change in batch.Changes)
        {
            mirror.Buffer.At(change.X, change.Y) = change.NewCell;
        }

        string[] lines = mirror.Lines();
        await Assert.That(lines[0][0]).IsEqualTo('x');
        await Assert.That(lines[0].Substring(1, 2)).IsEqualTo("bc");
    }
}
