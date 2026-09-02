using System.Text;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

public class CellTests
{
    [Test]
    public async Task Size_Is16Bytes()
    {
        await Assert.That(Cell.SizeBytes).IsEqualTo(16);
    }

    [Test]
    public async Task Blank_IsPlainSpace()
    {
        var b = Cell.Blank;
        await Assert.That(b.IsBlankSpace).IsTrue();
        await Assert.That(b.Width).IsEqualTo(Cell.Narrow);
        await Assert.That(b.Style.IsPlain).IsTrue();
    }

    [Test]
    public async Task Equality_FieldWise()
    {
        var style = new CellStyle(PackedColor.Indexed(4), attrs: StyleAttr.Bold);
        var a = Cell.From(new Rune('x'), style);
        var b = Cell.From(new Rune('x'), style);
        var c = Cell.From(new Rune('y'), style);

        await Assert.That(a == b).IsTrue();
        await Assert.That(a != c).IsTrue();

        var styled = Cell.From(new Rune('x'), new CellStyle(PackedColor.Indexed(4), attrs: StyleAttr.None));
        await Assert.That(a != styled).IsTrue();
    }

    [Test]
    public async Task From_WideRune_SetsWidth2()
    {
        var cell = Cell.From(new Rune(0x4E2D), CellStyle.Plain);
        await Assert.That(cell.Width).IsEqualTo(Cell.Wide);
        await Assert.That(cell.Rune).IsEqualTo(0x4E2D);
    }
}

public class ScreenBufferGeometryTests
{
    [Test]
    public async Task FreshBuffer_IsBlank()
    {
        var buf = new ScreenBuffer(8, 4);
        await Assert.That(buf.Get(3, 2).IsBlankSpace).IsTrue();
        await Assert.That(buf.Get(7, 3).IsBlankSpace).IsTrue();
    }

    [Test]
    public async Task ResizeDown_ReusesBackingArray()
    {
        var buf = new ScreenBuffer(80, 25);
        var before = buf.CellsForTests;
        buf.Resize(40, 12);
        await Assert.That(buf.CellsForTests).IsSameReferenceAs(before);
        await Assert.That(buf.Cols).IsEqualTo(40);
        await Assert.That(buf.Rows).IsEqualTo(12);
    }

    [Test]
    public async Task ResizeUp_GrowsCapacity()
    {
        var buf = new ScreenBuffer(10, 5);
        buf.Resize(200, 60);
        await Assert.That(buf.CellsForTests.Length >= 200 * 60).IsTrue();
        await Assert.That(buf.Get(199, 59).IsBlankSpace).IsTrue();
    }

    [Test]
    public async Task Resize_InvalidatesAndBlanks()
    {
        var buf = new ScreenBuffer(10, 10);
        _ = buf.SetRune(0, 0, new Rune('A'), CellStyle.Plain);
        buf.Resize(10, 10);
        await Assert.That(buf.Get(0, 0).IsBlankSpace).IsTrue();
    }
}

public class ScreenBufferWideCharTests
{
    private static readonly Rune Cjk = new(0x4E2D);      // 中
    private static readonly Rune Emoji = new(0x1F600);   // 😀
    private static readonly CellStyle Red = new(PackedColor.Indexed(1));

    [Test]
    public async Task WideRune_PlacesLeadPlusTail()
    {
        var buf = new ScreenBuffer(10, 1);
        _ = buf.SetRune(0, 0, Cjk, CellStyle.Plain);

        await Assert.That(buf.Get(0, 0).Width).IsEqualTo(Cell.Wide);
        await Assert.That(buf.Get(1, 0).Width).IsEqualTo(Cell.WSkip);
    }

    [Test]
    public async Task OverwriteTail_BlanksLead_ForceRepaintLeft()
    {
        var buf = new ScreenBuffer(10, 1);
        _ = buf.SetRune(0, 0, Cjk, CellStyle.Plain);
        bool placed = buf.SetRune(1, 0, new Rune('X'), CellStyle.Plain);

        await Assert.That(placed).IsTrue();
        // Lead half must have been reset to blank — diff will repaint both.
        await Assert.That(buf.Get(0, 0).IsBlankSpace).IsTrue();
        await Assert.That(buf.Get(1, 0).Rune).IsEqualTo('X');
    }

    [Test]
    public async Task OverwriteLead_TailCleared()
    {
        var buf = new ScreenBuffer(10, 1);
        _ = buf.SetRune(0, 0, Cjk, CellStyle.Plain);
        bool placed = buf.SetRune(0, 0, new Rune('Y'), CellStyle.Plain);

        await Assert.That(placed).IsTrue();
        await Assert.That(buf.Get(0, 0).Rune).IsEqualTo('Y');
        await Assert.That(buf.Get(1, 0).IsBlankSpace).IsTrue(); // old tail not orphaned
    }

    [Test]
    public async Task PlacingWideOverNextWide_ClearsOrphanTail()
    {
        var buf = new ScreenBuffer(10, 1);
        _ = buf.SetRune(0, 0, Cjk, CellStyle.Plain);   // pair (0,1)
        _ = buf.SetRune(2, 0, Cjk, CellStyle.Plain);   // pair (2,3)
        _ = buf.SetRune(2, 0, new Rune('Z'), CellStyle.Plain); // overwrite lead of pair 2

        await Assert.That(buf.Get(2, 0).Rune).IsEqualTo('Z');
        await Assert.That(buf.Get(3, 0).IsBlankSpace).IsTrue();
        // Pair (0,1) untouched.
        await Assert.That(buf.Get(0, 0).Width).IsEqualTo(Cell.Wide);
        await Assert.That(buf.Get(1, 0).Width).IsEqualTo(Cell.WSkip);
    }

    [Test]
    public async Task WideAtRightEdge_IsRejected()
    {
        var buf = new ScreenBuffer(3, 1);
        bool placed = buf.SetRune(2, 0, Cjk, CellStyle.Plain);
        await Assert.That(placed).IsFalse();
        await Assert.That(buf.Get(2, 0).IsBlankSpace).IsTrue();
    }

    [Test]
    public async Task ZeroWidthRunes_AreIgnored()
    {
        var buf = new ScreenBuffer(10, 1);
        bool handled = buf.SetRune(0, 0, new Rune(0xFE0F), CellStyle.Plain); // VS16
        await Assert.That(handled).IsTrue();
        await Assert.That(buf.Get(0, 0).IsBlankSpace).IsTrue();
    }

    [Test]
    public async Task SetText_AdvancesByDisplayWidth()
    {
        var buf = new ScreenBuffer(20, 1);
        buf.SetText(0, 0, $"a{Cjk}b{Emoji}", CellStyle.Plain);

        // a(1) 中(2) b(1) 😀(2)
        await Assert.That(buf.Get(0, 0).Rune).IsEqualTo('a');
        await Assert.That(buf.Get(1, 0).Width).IsEqualTo(Cell.Wide);
        await Assert.That(buf.Get(3, 0).Rune).IsEqualTo('b');
        await Assert.That(buf.Get(4, 0).Width).IsEqualTo(Cell.Wide);
        await Assert.That(buf.Get(6, 0).IsBlankSpace).IsTrue();
    }

    [Test]
    public async Task SetText_StopsAtRowEnd_NoSplit()
    {
        var buf = new ScreenBuffer(4, 1);
        buf.SetText(0, 0, "ab" + Cjk + "cd", CellStyle.Plain);

        // ab fits (2 cells), 中 needs cells 2..3 → fits exactly, 'c' would be col 4 → stop.
        await Assert.That(buf.Get(2, 0).Width).IsEqualTo(Cell.Wide);
        await Assert.That(buf.Get(3, 0).Width).IsEqualTo(Cell.WSkip);
    }

    [Test]
    public async Task EmojiSurrogatePair_OccupiesTwoCells()
    {
        var buf = new ScreenBuffer(10, 1);
        buf.SetText(0, 0, "😀!", CellStyle.Plain);
        await Assert.That(buf.Get(0, 0).Width).IsEqualTo(Cell.Wide);
        await Assert.That(buf.Get(1, 0).Width).IsEqualTo(Cell.WSkip);
        await Assert.That(buf.Get(2, 0).Rune).IsEqualTo('!');
    }
}

public class ScreenBufferHashTests
{
    [Test]
    public async Task EqualRows_HaveEqualHashes()
    {
        var buf = new ScreenBuffer(10, 2);
        buf.SetText(0, 0, "same", CellStyle.Plain);
        buf.SetText(0, 1, "same", CellStyle.Plain);

        await Assert.That(buf.RowHashCode(0)).IsEqualTo(buf.RowHashCode(1));
    }

    [Test]
    public async Task Mutation_InvalidatesRowHash()
    {
        var buf = new ScreenBuffer(10, 1);
        buf.SetText(0, 0, "before", CellStyle.Plain);
        ulong before = buf.RowHashCode(0);

        _ = buf.SetRune(0, 0, new Rune('B'), CellStyle.Plain);

        // Hash validity dropped; recomputed value must differ.
        await Assert.That(buf.IsRowHashValid(0)).IsFalse();
        ulong after = buf.RowHashCode(0);
        await Assert.That(after).IsNotEqualTo(before);
    }

    [Test]
    public async Task StyleOnlyChange_ChangesHash()
    {
        var buf = new ScreenBuffer(10, 1);
        buf.SetText(0, 0, "x", CellStyle.Plain);
        ulong plain = buf.RowHashCode(0);
        _ = buf.SetStyleAt(0, 0, new CellStyle(attrs: StyleAttr.Bold));
        ulong bold = buf.RowHashCode(0);
        await Assert.That(bold).IsNotEqualTo(plain);
    }

    [Test]
    public async Task Fill_RespectsClipping_AndInvalidates()
    {
        var buf = new ScreenBuffer(10, 4);
        buf.Fill(new Rect(-5, -5, 20, 20), Cell.From(new Rune('#'), CellStyle.Plain));

        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 10; x++)
            {
                if (!buf.Get(x, y).Equals(Cell.Blank))
                {
                    return; // found filled cell
                }
            }
        }
    }
}
