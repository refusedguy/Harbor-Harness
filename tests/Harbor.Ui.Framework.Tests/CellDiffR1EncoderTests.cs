namespace Harbor.Ui.Framework.Tests;

using System.Text;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

/// <summary>
/// R1 steal (epic #1155, issue #1156): portable-encoder parity for the
/// BufferDiff port — Skip/AlwaysUpdate/ForcedWidth directives, trailing
/// force-refresh on visibly-styled wide glyphs, AlwaysUpdate fast-path
/// bypass. Engine-level twins live in BufferDiffR1Tests (CellForge.Tests).
/// </summary>
public class CellDiffR1EncoderTests
{
    private static readonly Rune Cjk = new(0x4E2D);
    private static readonly CellStyle BlueBg = new(bg: PackedColor.Indexed(4));

    [Test]
    public async Task Encode_SkipCells_Omitted()
    {
        var encoder = new RowHashDiffEncoder();
        var prev = new ScreenBuffer(3, 1);
        prev.SetText(0, 0, "abc", CellStyle.Plain);
        var next = new ScreenBuffer(3, 1);
        next.SetText(0, 0, "xyz", CellStyle.Plain);
        next.SetDiffOption(1, 0, CellDiffOption.Skip);

        CellDiffBatch batch = encoder.Encode(prev, next, hints: null, sequence: 1);

        await Assert.That(batch.Changes.Length).IsEqualTo(2);
        await Assert.That(batch.Changes[0].NewCell.Rune).IsEqualTo((int)'x');
        await Assert.That(batch.Changes[1].NewCell.Rune).IsEqualTo((int)'z');
    }

    [Test]
    public async Task Encode_AlwaysUpdate_EmittedWhenIdentical()
    {
        var encoder = new RowHashDiffEncoder();
        var prev = new ScreenBuffer(3, 1);
        prev.SetText(0, 0, "abc", CellStyle.Plain);
        prev.SetDiffOption(1, 0, CellDiffOption.AlwaysUpdate);
        var next = new ScreenBuffer(3, 1);
        next.SetText(0, 0, "abc", CellStyle.Plain);
        next.SetDiffOption(1, 0, CellDiffOption.AlwaysUpdate);

        CellDiffBatch batch = encoder.Encode(prev, next, hints: null, sequence: 1);

        await Assert.That(batch.Changes.Length).IsEqualTo(1);
        await Assert.That(batch.Changes[0].X).IsEqualTo(1);
        await Assert.That(batch.Changes[0].NewCell.Rune).IsEqualTo((int)'b');
    }

    [Test]
    public async Task Encode_ForcedWidth_CoversReserved_KeepsLaterCells()
    {
        var encoder = new RowHashDiffEncoder();
        var prev = new ScreenBuffer(6, 1);
        prev.SetText(0, 0, "SSSSSS", CellStyle.Plain);
        var next = new ScreenBuffer(6, 1);
        next.SetText(0, 0, "Lbcdef", CellStyle.Plain);
        next.SetDiffOption(0, 0, CellDiffOption.ForcedWidth, 3);

        CellDiffBatch batch = encoder.Encode(prev, next, hints: null, sequence: 1);

        await Assert.That(batch.Changes.Any(m => m.X == 0)).IsTrue();
        await Assert.That(batch.Changes.Any(m => m.X == 1 || m.X == 2)).IsFalse();
        await Assert.That(batch.Changes.Any(m => m.X == 3)).IsTrue();
    }

    [Test]
    public async Task Encode_WideStyleRemoved_TrailingBeforeLead()
    {
        var encoder = new RowHashDiffEncoder();
        var prev = new ScreenBuffer(3, 1);
        prev.SetRune(0, 0, Cjk, BlueBg);
        var next = new ScreenBuffer(3, 1);
        next.SetRune(0, 0, Cjk, CellStyle.Plain);

        CellDiffBatch batch = encoder.Encode(prev, next, hints: null, sequence: 1);

        await Assert.That(batch.Changes.Length).IsEqualTo(2);
        await Assert.That(batch.Changes[0].X).IsEqualTo(1);
        await Assert.That(batch.Changes[1].X).IsEqualTo(0);
    }

    [Test]
    public async Task Encode_NarrowOverStyledWide_ForceRefreshesTrailing()
    {
        // Incremental shape via direct writes (tail kept identical): the only
        // thing refreshing it is the force arm.
        var encoder = new RowHashDiffEncoder();
        var prev = new ScreenBuffer(4, 1);
        prev.SetRune(0, 0, Cjk, BlueBg);
        var next = new ScreenBuffer(4, 1);
        next.SetRune(0, 0, Cjk, BlueBg);
        next.At(0, 0) = Cell.From(new Rune('b'), CellStyle.Plain);
        next.InvalidateAll();

        CellDiffBatch batch = encoder.Encode(prev, next, hints: null, sequence: 1);

        await Assert.That(batch.Changes.Any(m => m.X == 0)).IsTrue();
        await Assert.That(batch.Changes.Any(m => m.X == 1)).IsTrue();
    }

    [Test]
    public async Task Encode_ApplyBatch_ReplaysOntoMirror()
    {
        // End-to-end portable contract: applying every message to a mirror of
        // prev converges it to next, trailing force-refresh included.
        var encoder = new RowHashDiffEncoder();
        var prev = new ScreenBuffer(6, 1);
        prev.SetText(0, 0, "a中b文c", CellStyle.Plain);
        var next = new ScreenBuffer(6, 1);
        next.SetText(0, 0, "中文ab", BlueBg);

        CellDiffBatch batch = encoder.Encode(prev, next, hints: null, sequence: 1);

        var mirror = new ScreenBuffer(6, 1);
        mirror.SetText(0, 0, "a中b文c", CellStyle.Plain);
        foreach (var change in batch.Changes)
        {
            mirror.At(change.X, change.Y) = change.NewCell;
        }

        for (int y = 0; y < 1; y++)
        {
            for (int x = 0; x < 6; x++)
            {
                await Assert.That(mirror.Get(x, y)).IsEqualTo(next.Get(x, y));
            }
        }
    }
}
