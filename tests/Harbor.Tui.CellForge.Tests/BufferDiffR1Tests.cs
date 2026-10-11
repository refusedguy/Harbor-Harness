using System.Text;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

// R1 steal (epic #1155, issue #1156): BufferDiff wide/VS16-correctness ported
// from ratatui buffer/diff.rs — TrailingState, VISIBLE_ON_BLANK, Skip/
// ForcedWidth. Engine-level goldens (engine buffers, drained FrameDiff);
// the portable-encoder half lives in CellDiffR1EncoderTests, the
// TestBackend-harness half in CellDiffWideGoldenTests.
public class BufferDiffTrailingTests
{
    private static readonly Rune Cjk = new(0x4E2D);
    private static readonly EngineCells.CellStyle BlueBg = new(bg: EngineCells.PackedColor.Indexed(4));
    private static readonly EngineCells.CellStyle Reverse = new(attrs: EngineCells.StyleAttr.Reverse);

    private sealed record Yield(int X, int Y, EngineCells.Cell Cell, int Advance, bool Forced);

    private static List<Yield> Drain(EngineCells.DiffEngine engine, EngineCells.ScreenBuffer next)
    {
        var yields = new List<Yield>();
        var cursor = engine.Diff(next).GetEnumerator();
        while (cursor.MoveNext())
        {
            yields.Add(new Yield(cursor.X, cursor.Y, cursor.Target, cursor.Advance, cursor.IsForcedWidth));
        }

        return yields;
    }

    private static List<Yield> DiffAgainst(EngineCells.ScreenBuffer prev, EngineCells.ScreenBuffer next)
    {
        var engine = new EngineCells.DiffEngine(prev.Cols, prev.Rows);
        Drain(engine, prev);
        return Drain(engine, next);
    }

    [Test]
    public async Task NarrowOverStyledWide_ForceRefreshesTrailingColumns()
    {
        // Ratatui uncovered-trailing-cells shape: "你好，世界！"(Blue) replaced
        // by "Hello" (default). Columns 5, 7, 9, 11 are the trailing cells of
        // "，" "世" "界" "！" — the terminal painted them blue, so they must be
        // refreshed even though the buffer-level cells there look blank.
        var prev = new EngineCells.ScreenBuffer(12, 1);
        prev.SetText(0, 0, "你好，世界！", BlueBg);
        var next = new EngineCells.ScreenBuffer(12, 1);
        next.SetText(0, 0, "Hello", EngineCells.CellStyle.Plain);

        var yields = DiffAgainst(prev, next);

        foreach (int x in new[] { 5, 7, 9, 11 })
        {
            await Assert.That(yields.Any(y => y.X == x && y.Y == 0)).IsTrue();
        }
    }

    [Test]
    public async Task NarrowOverPlainWide_EmitsNoForceBeyondContentDiff()
    {
        // Same geometry, default style: no VISIBLE_ON_BLANK style anywhere, so
        // the trailing arm must not fire. Incremental paint keeps the tail
        // cell identical (At-write, no pair clear) — it must stay silent.
        var back = new EngineCells.ScreenBuffer(4, 1);
        back.SetRune(0, 0, Cjk, EngineCells.CellStyle.Plain);
        var engine = new EngineCells.DiffEngine(4, 1);
        Drain(engine, back);

        back.At(0, 0) = EngineCells.Cell.From(new Rune('b'), EngineCells.CellStyle.Plain);
        back.InvalidateAll();
        var yields = Drain(engine, back);

        await Assert.That(yields.Count).IsEqualTo(1);
        await Assert.That(yields[0].X).IsEqualTo(0);
        await Assert.That(engine.FrontMatches(back)).IsTrue();
    }

    [Test]
    public async Task NarrowOverVisibleWide_IncrementalTail_ForceEmitted()
    {
        // The live case for the force arm: incremental BACK painting rewrote
        // the lead in place (At-write, tail untouched), so the tail is
        // identical in FRONT and BACK — yet the terminal still shows the old
        // wide style there. Only the force arm can refresh it.
        var back = new EngineCells.ScreenBuffer(4, 1);
        back.SetRune(0, 0, Cjk, BlueBg);
        var engine = new EngineCells.DiffEngine(4, 1);
        Drain(engine, back);

        back.At(0, 0) = EngineCells.Cell.From(new Rune('b'), EngineCells.CellStyle.Plain);
        back.InvalidateAll();
        var yields = Drain(engine, back);

        await Assert.That(yields.Any(y => y.X == 1 && y.Y == 0)).IsTrue();
        await Assert.That(engine.FrontMatches(back)).IsTrue();
    }

    [Test]
    public async Task WideStyleChange_ClearsTrailingBeforeRepaint()
    {
        // Ratatui #2652 shape: same glyph, style removed. The trailing column
        // still shows the old style on the terminal and must be cleared BEFORE
        // the lead repaint — clearing after can erase the glyph itself.
        var prev = new EngineCells.ScreenBuffer(3, 1);
        prev.SetRune(0, 0, Cjk, BlueBg);
        var next = new EngineCells.ScreenBuffer(3, 1);
        next.SetRune(0, 0, Cjk, EngineCells.CellStyle.Plain);

        var yields = DiffAgainst(prev, next);

        await Assert.That(yields.Count).IsEqualTo(2);
        await Assert.That(yields[0].X).IsEqualTo(1);
        await Assert.That(yields[1].X).IsEqualTo(0);
        await Assert.That(yields[1].Cell.Rune).IsEqualTo(Cjk.Value);
    }

    [Test]
    public async Task WideForegroundChange_DoesNotClearTrailing()
    {
        // Foreground-only change: invisible on blanks, so no trailing clear —
        // exactly one yield, the lead itself.
        var prev = new EngineCells.ScreenBuffer(3, 1);
        prev.SetRune(0, 0, Cjk, new EngineCells.CellStyle(fg: EngineCells.PackedColor.Indexed(4)));
        var next = new EngineCells.ScreenBuffer(3, 1);
        next.SetRune(0, 0, Cjk, new EngineCells.CellStyle(fg: EngineCells.PackedColor.Indexed(1)));

        var yields = DiffAgainst(prev, next);

        await Assert.That(yields.Count).IsEqualTo(1);
        await Assert.That(yields[0].X).IsEqualTo(0);
    }

    [Test]
    public async Task WideReverseChange_ClearsTrailing_WithoutBackground()
    {
        // VISIBLE_ON_BLANK is not bg-only: Reverse alone forces the trailing
        // refresh (ratatui REVERSED bit), proved with default background.
        var prev = new EngineCells.ScreenBuffer(3, 1);
        prev.SetRune(0, 0, Cjk, Reverse);
        var next = new EngineCells.ScreenBuffer(3, 1);
        next.SetRune(0, 0, Cjk, EngineCells.CellStyle.Plain);

        var yields = DiffAgainst(prev, next);

        await Assert.That(yields.Count).IsEqualTo(2);
        await Assert.That(yields[0].X).IsEqualTo(1);
        // The trailing yield paints a real space, never a NUL tail: WideTail
        // (rune 0) is undrawable, and emitting it would write a NUL byte.
        await Assert.That(yields[0].Cell.Rune).IsEqualTo((int)' ');
        await Assert.That(yields[1].X).IsEqualTo(0);
    }

    [Test]
    public async Task WideGlyphAtLastColumn_DoesNotOverrun()
    {
        // Ratatui #2695 shape: a wide glyph in the last cell, replaced by
        // narrower content. The forced range is clamped to the row span, not
        // the buffer length — no index escape, only the replaced cell emits.
        var prev = new EngineCells.ScreenBuffer(3, 1);
        prev.At(2, 0) = EngineCells.Cell.FromRaw(Cjk.Value, 0, Indexed(1), 0, EngineCells.Cell.Wide);
        var next = new EngineCells.ScreenBuffer(3, 1);
        next.SetRune(2, 0, new Rune('a'), EngineCells.CellStyle.Plain);

        var yields = DiffAgainst(prev, next);

        await Assert.That(yields.Count).IsEqualTo(1);
        await Assert.That(yields[0].X).IsEqualTo(2);
        await Assert.That(yields[0].Cell.Rune).IsEqualTo((int)'a');
    }

    private static uint Indexed(byte i) => EngineCells.PackedColor.Indexed(i).Value;
}

public class BufferDiffOptionTests
{
    private static readonly Rune Cjk = new(0x4E2D);

    private sealed record Yield(int X, int Y, EngineCells.Cell Cell, int Advance, bool Forced);

    private static List<Yield> Drain(EngineCells.DiffEngine engine, EngineCells.ScreenBuffer next)
    {
        var yields = new List<Yield>();
        var cursor = engine.Diff(next).GetEnumerator();
        while (cursor.MoveNext())
        {
            yields.Add(new Yield(cursor.X, cursor.Y, cursor.Target, cursor.Advance, cursor.IsForcedWidth));
        }

        return yields;
    }

    [Test]
    public async Task Skip_NeverYielded_ButMirrored()
    {
        var prev = new EngineCells.ScreenBuffer(3, 1);
        prev.SetText(0, 0, "abc", EngineCells.CellStyle.Plain);
        var next = new EngineCells.ScreenBuffer(3, 1);
        next.SetText(0, 0, "xyz", EngineCells.CellStyle.Plain);
        next.SetDiffOption(1, 0, EngineCells.CellDiffOption.Skip);

        var engine = new EngineCells.DiffEngine(3, 1);
        Drain(engine, prev);
        var yields = Drain(engine, next);

        await Assert.That(yields.Count).IsEqualTo(2);
        await Assert.That(yields[0].Cell.Rune).IsEqualTo((int)'x');
        await Assert.That(yields[1].Cell.Rune).IsEqualTo((int)'z');
        // Silent mirror: the post-drain invariant holds for Skip cells too.
        await Assert.That(engine.FrontMatches(next)).IsTrue();
    }

    [Test]
    public async Task AlwaysUpdate_EmittedEvenWhenIdentical()
    {
        var prev = new EngineCells.ScreenBuffer(3, 1);
        prev.SetText(0, 0, "abc", EngineCells.CellStyle.Plain);
        prev.SetDiffOption(1, 0, EngineCells.CellDiffOption.AlwaysUpdate);
        var next = new EngineCells.ScreenBuffer(3, 1);
        next.SetText(0, 0, "abc", EngineCells.CellStyle.Plain);
        next.SetDiffOption(1, 0, EngineCells.CellDiffOption.AlwaysUpdate);

        var engine = new EngineCells.DiffEngine(3, 1);
        Drain(engine, prev);
        var yields = Drain(engine, next);

        await Assert.That(yields.Count).IsEqualTo(1);
        await Assert.That(yields[0].X).IsEqualTo(1);
        await Assert.That(yields[0].Cell.Rune).IsEqualTo((int)'b');
    }

    [Test]
    public async Task ForcedWidth_SkipsReserved_DoesNotHideLaterCells()
    {
        // Ratatui #2685 shape: a cell measuring wider than the grid must not
        // hide the cells after it, changed or not.
        var prev = new EngineCells.ScreenBuffer(6, 1);
        prev.SetText(0, 0, "SSSSSS", EngineCells.CellStyle.Plain);
        var next = new EngineCells.ScreenBuffer(6, 1);
        next.SetText(0, 0, "Lbcdef", EngineCells.CellStyle.Plain);
        next.SetDiffOption(0, 0, EngineCells.CellDiffOption.ForcedWidth, 3);

        var engine = new EngineCells.DiffEngine(6, 1);
        Drain(engine, prev);
        var yields = Drain(engine, next);

        await Assert.That(yields.Any(y => y.X == 0 && y.Forced && y.Advance == 3)).IsTrue();
        // Reserved columns 1-2 are covered by the forced advance...
        await Assert.That(yields.Any(y => y.X == 1 || y.X == 2)).IsFalse();
        // ...but later cells still diff normally.
        await Assert.That(yields.Any(y => y.X == 3)).IsTrue();
        await Assert.That(yields.Any(y => y.X == 5)).IsTrue();
        await Assert.That(engine.FrontMatches(next)).IsTrue();
    }

    [Test]
    public async Task OptionOnlyChange_BreaksRowHashFastPath()
    {
        // The row hash folds the directive table: arming Skip on an otherwise
        // identical row must still surface (as silence for that cell) instead
        // of vanishing into the fast path.
        var prev = new EngineCells.ScreenBuffer(3, 1);
        prev.SetText(0, 0, "abc", EngineCells.CellStyle.Plain);
        var next = new EngineCells.ScreenBuffer(3, 1);
        next.SetText(0, 0, "abc", EngineCells.CellStyle.Plain);
        next.SetDiffOption(0, 0, EngineCells.CellDiffOption.Skip);

        var engine = new EngineCells.DiffEngine(3, 1);
        Drain(engine, prev);
        var yields = Drain(engine, next);

        await Assert.That(yields).IsEmpty();
        await Assert.That(engine.FrontMatches(next)).IsTrue();
    }
}

public class UncertainWidthTests
{
    [Test]
    public async Task HasUncertainWidth_DetectsVs16()
    {
        await Assert.That(EngineCells.UnicodeWidth.HasUncertainWidth("❤️".AsSpan())).IsTrue();
        await Assert.That(EngineCells.UnicodeWidth.HasUncertainWidth("1️⃣".AsSpan())).IsTrue();
        await Assert.That(EngineCells.UnicodeWidth.HasUncertainWidth("hello".AsSpan())).IsFalse();
        await Assert.That(EngineCells.UnicodeWidth.HasUncertainWidth("中".AsSpan())).IsFalse();
        await Assert.That(EngineCells.UnicodeWidth.HasUncertainWidth("😀".AsSpan())).IsFalse();
    }

    [Test]
    public async Task WriteText_Vs16Run_InvalidatesPen()
    {
        // Ratatui VS16 backend rule: after uncertain-width output the pen is
        // unknowable, so the next MoveTo must be emitted, never elided.
        var backend = new RecordingBackend();
        var writer = new EngineCells.AnsiWriter(backend);
        writer.BeginFrame();
        writer.MoveTo(0, 0);
        writer.WriteText("a❤️b");
        writer.MoveTo(4, 0);
        await writer.EndFrameAsync();

        // Two CUP sequences: the pre-text one and the post-VS16 one that
        // elision would otherwise have swallowed.
        await Assert.That(CountOccurrences(backend.Text, "H")).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task WriteText_AsciiRun_KeepsPenElision()
    {
        var backend = new RecordingBackend();
        var writer = new EngineCells.AnsiWriter(backend);
        writer.BeginFrame();
        writer.MoveTo(0, 0);
        writer.WriteText("ab");
        writer.MoveTo(2, 0);
        await writer.EndFrameAsync();

        // Pen survived: the adjacent MoveTo elided to a single CUP.
        await Assert.That(CountOccurrences(backend.Text, "H")).IsEqualTo(1);
    }

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        int at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }

        return count;
    }
}

/// <summary>
/// Model-terminal replay (R1 steal: ratatui diff.rs <c>terminal_model</c>
/// port, adapted to single-rune cells). The model starts as the previous
/// frame rendered, replays FrameDiff yields the way Flush does (explicit move
/// unless adjacent, pen advanced by the yield's advance), and must land
/// exactly on the next frame's expected columns. A stale column surviving
/// into the comparison IS the artifact — wide tails painted with a replaced
/// glyph's style, NUL bytes written for tails, ghost halves.
/// </summary>
public class TerminalModelTests
{
    private static readonly Rune Cjk = new(0x4E2D);
    private static readonly EngineCells.CellStyle BlueBg = new(bg: EngineCells.PackedColor.Indexed(4));

    private sealed record Yield(int X, int Y, EngineCells.Cell Cell, int Advance);

    private sealed class ModelRow
    {
        private readonly int[] _rune;
        private readonly uint[] _bg;
        private readonly bool[] _cont;

        public ModelRow(int cols)
        {
            _rune = new int[cols];
            _bg = new uint[cols];
            _cont = new bool[cols];
            for (int i = 0; i < cols; i++)
            {
                _rune[i] = ' ';
            }
        }

        public int Cols => _rune.Length;

        public void Paint(int col, int rune, uint bg, int advance)
        {
            if (col < 0 || col >= Cols)
            {
                return;
            }

            int end = Math.Min(col + Math.Max(1, advance), Cols);
            // Overwriting either half of a multi-column glyph destroys the
            // whole glyph: blank its full footprint before the new paint.
            int lo = col;
            while (lo > 0 && _cont[lo])
            {
                lo--;
            }

            int hi = end;
            while (hi < Cols && _cont[hi])
            {
                hi++;
            }

            for (int i = lo; i < hi; i++)
            {
                _rune[i] = ' ';
                _bg[i] = 0;
                _cont[i] = false;
            }

            _rune[col] = rune;
            _bg[col] = bg;
            for (int i = col + 1; i < end; i++)
            {
                _cont[i] = true;
            }
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Cols; i++)
            {
                sb.Append(_cont[i] ? '»' : (char)_rune[i]).Append('(').Append(_bg[i]).Append(')');
            }

            return sb.ToString();
        }
    }

    private static ModelRow ExpectedColumns(EngineCells.ScreenBuffer buf)
    {
        var row = new ModelRow(buf.Cols);
        int x = 0;
        while (x < buf.Cols)
        {
            var c = buf.Get(x, 0);
            if (c.Width == EngineCells.Cell.Wide)
            {
                // A wide lead paints its own column plus the column it
                // advances over; the terminal shows the lead style on both.
                row.Paint(x, c.Rune, c.Bg, 2);
                x += 2;
            }
            else if (c.Width == EngineCells.Cell.WSkip)
            {
                // Orphan tail (lead rewritten in place): a real terminal shows
                // whatever the last paint left — the buffer's own cell, with
                // rune 0 reading as a blank.
                row.Paint(x, c.Rune == 0 ? ' ' : c.Rune, c.Bg, 1);
                x += 1;
            }
            else
            {
                row.Paint(x, c.Rune, c.Bg, 1);
                x += 1;
            }
        }

        return row;
    }

    private static ModelRow Replay(EngineCells.ScreenBuffer prev, EngineCells.ScreenBuffer next)
    {
        var terminal = ExpectedColumns(prev);
        var engine = new EngineCells.DiffEngine(prev.Cols, 1);
        var prime = engine.Diff(prev).GetEnumerator();
        while (prime.MoveNext())
        {
        }

        int penX = -1;
        int penY = -1;
        var cursor = engine.Diff(next).GetEnumerator();
        while (cursor.MoveNext())
        {
            // Flush's adjacency rule: explicit move unless the pen is already
            // on the cell. (Flush never faces uncertain advances from grid
            // cells — widths are table-certain — so the pen stays truthful.)
            int col = cursor.X == penX + 1 && cursor.Y == penY ? penX + 1 : cursor.X;
            terminal.Paint(col, cursor.Target.Rune, cursor.Target.Bg, cursor.Advance);
            penX = col + cursor.Advance - 1;
            penY = cursor.Y;
        }

        return terminal;
    }

    private static async Task AssertNoArtifacts(EngineCells.ScreenBuffer prev, EngineCells.ScreenBuffer next)
    {
        string actual = Replay(prev, next).Describe();
        string expected = ExpectedColumns(next).Describe();
        await Assert.That(actual).IsEqualTo(expected);
    }

    [Test]
    public async Task StyledWideReplacedByNarrow_LeavesNoStaleColumns()
    {
        var prev = new EngineCells.ScreenBuffer(12, 1);
        prev.SetText(0, 0, "你好，世界！", BlueBg);
        var next = new EngineCells.ScreenBuffer(12, 1);
        next.SetText(0, 0, "Hello", EngineCells.CellStyle.Plain);

        await AssertNoArtifacts(prev, next);
    }

    [Test]
    public async Task StyledWideReplacedByNarrow_IncrementalBack_LeavesNoStaleColumns()
    {
        // Incremental BACK (lead rewritten in place, tail kept): the force arm
        // is the only thing refreshing the identical tail cell.
        var back = new EngineCells.ScreenBuffer(4, 1);
        back.SetRune(0, 0, Cjk, BlueBg);
        var prev = new EngineCells.ScreenBuffer(4, 1);
        prev.SetRune(0, 0, Cjk, BlueBg);
        back.At(0, 0) = EngineCells.Cell.From(new Rune('b'), EngineCells.CellStyle.Plain);

        await AssertNoArtifacts(prev, back);
    }

    [Test]
    public async Task WideStyleRemoved_LeavesNoStaleColumns()
    {
        var prev = new EngineCells.ScreenBuffer(4, 1);
        prev.SetRune(0, 0, Cjk, BlueBg);
        prev.SetText(2, 0, "ab", EngineCells.CellStyle.Plain);
        var next = new EngineCells.ScreenBuffer(4, 1);
        next.SetRune(0, 0, Cjk, EngineCells.CellStyle.Plain);
        next.SetText(2, 0, "ab", EngineCells.CellStyle.Plain);

        await AssertNoArtifacts(prev, next);
    }

    [Test]
    public async Task MixedWideAndNarrow_LeavesNoStaleColumns()
    {
        var prev = new EngineCells.ScreenBuffer(8, 1);
        prev.SetText(0, 0, "a中b文c", EngineCells.CellStyle.Plain);
        var next = new EngineCells.ScreenBuffer(8, 1);
        next.SetText(0, 0, "中文ab", BlueBg);

        await AssertNoArtifacts(prev, next);
        await AssertNoArtifacts(next, prev);
    }
}
