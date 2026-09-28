using System.Text;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

// ENG9 audit (#281): wide-grapheme/emoji cell correctness.
// Cell-writing scope ONLY: UnicodeWidth tables + ScreenBuffer pair
// invariants + documented per-rune sequence pins. Diff algorithm,
// layout, overlays and widgets are untouched.
public class Eng9WidthTableTests
{
    [Test]
    public async Task C1Controls_AreZero()
    {
        await Assert.That(UnicodeWidth.Width(new Rune(0x80))).IsEqualTo(0);
        await Assert.That(UnicodeWidth.Width(new Rune(0x9B))).IsEqualTo(0);
        await Assert.That(UnicodeWidth.Width(new Rune(0x9F))).IsEqualTo(0);
    }

    [Test]
    public async Task HangulVowelAndTrailingJamo_AreZero_FillerStaysWide()
    {
        await Assert.That(UnicodeWidth.Width(new Rune(0x1160))).IsEqualTo(0);
        await Assert.That(UnicodeWidth.Width(new Rune(0x11A8))).IsEqualTo(0);
        await Assert.That(UnicodeWidth.Width(new Rune(0x11FF))).IsEqualTo(0);
        await Assert.That(UnicodeWidth.Width(new Rune(0xD7B0))).IsEqualTo(0);
        await Assert.That(UnicodeWidth.Width(new Rune(0xD7FB))).IsEqualTo(0);
        // U+115F CHOSEONG FILLER pairs with V/T jamo into a wide syllable.
        await Assert.That(UnicodeWidth.Width(new Rune(0x115F))).IsEqualTo(2);
    }

    [Test]
    public async Task TagCharacters_AreZero()
    {
        await Assert.That(UnicodeWidth.Width(new Rune(0xE0001))).IsEqualTo(0);
        await Assert.That(UnicodeWidth.Width(new Rune(0xE0020))).IsEqualTo(0);
        await Assert.That(UnicodeWidth.Width(new Rune(0xE007F))).IsEqualTo(0);
    }

    [Test]
    public async Task IsolatesAndJoiners_AreZero()
    {
        await Assert.That(UnicodeWidth.Width(new Rune(0x2060))).IsEqualTo(0);
        await Assert.That(UnicodeWidth.Width(new Rune(0x2066))).IsEqualTo(0);
        await Assert.That(UnicodeWidth.Width(new Rune(0x2069))).IsEqualTo(0);
    }

    [Test]
    public async Task MongolianFvs_AreZero()
    {
        await Assert.That(UnicodeWidth.Width(new Rune(0x180B))).IsEqualTo(0);
        await Assert.That(UnicodeWidth.Width(new Rune(0x180F))).IsEqualTo(0);
    }

    [Test]
    public async Task PrependFormat_AreZero()
    {
        await Assert.That(UnicodeWidth.Width(new Rune(0x0600))).IsEqualTo(0);
        await Assert.That(UnicodeWidth.Width(new Rune(0x070F))).IsEqualTo(0);
        await Assert.That(UnicodeWidth.Width(new Rune(0x0890))).IsEqualTo(0);
        await Assert.That(UnicodeWidth.Width(new Rune(0x08E2))).IsEqualTo(0);
    }

    [Test]
    public async Task CombiningInsideWideRanges_AreZero_NeighborsStayWide()
    {
        await Assert.That(UnicodeWidth.Width(new Rune(0x302A))).IsEqualTo(0);
        await Assert.That(UnicodeWidth.Width(new Rune(0x302D))).IsEqualTo(0);
        await Assert.That(UnicodeWidth.Width(new Rune(0x3099))).IsEqualTo(0);
        await Assert.That(UnicodeWidth.Width(new Rune(0x16FE4))).IsEqualTo(0);
        await Assert.That(UnicodeWidth.Width(new Rune(0x3000))).IsEqualTo(2);
    }

    [Test]
    public async Task MissingWideDingbats_AreTwo()
    {
        await Assert.That(UnicodeWidth.Width(new Rune(0x231A))).IsEqualTo(2);
        await Assert.That(UnicodeWidth.Width(new Rune(0x25FD))).IsEqualTo(2);
        await Assert.That(UnicodeWidth.Width(new Rune(0x2614))).IsEqualTo(2);
        await Assert.That(UnicodeWidth.Width(new Rune(0x2648))).IsEqualTo(2);
        await Assert.That(UnicodeWidth.Width(new Rune(0x267F))).IsEqualTo(2);
        await Assert.That(UnicodeWidth.Width(new Rune(0x26A1))).IsEqualTo(2);
        await Assert.That(UnicodeWidth.Width(new Rune(0x26BD))).IsEqualTo(2);
        await Assert.That(UnicodeWidth.Width(new Rune(0x2705))).IsEqualTo(2);
        await Assert.That(UnicodeWidth.Width(new Rune(0x2728))).IsEqualTo(2);
        await Assert.That(UnicodeWidth.Width(new Rune(0x274C))).IsEqualTo(2);
        await Assert.That(UnicodeWidth.Width(new Rune(0x2B50))).IsEqualTo(2);
    }

    [Test]
    public async Task MissingWideBlocks_AreTwo()
    {
        await Assert.That(UnicodeWidth.Width(new Rune(0x4DC0))).IsEqualTo(2);
        await Assert.That(UnicodeWidth.Width(new Rune(0x1B155))).IsEqualTo(2);
        await Assert.That(UnicodeWidth.Width(new Rune(0x1B170))).IsEqualTo(2);
        await Assert.That(UnicodeWidth.Width(new Rune(0x1D300))).IsEqualTo(2);
        await Assert.That(UnicodeWidth.Width(new Rune(0x1D360))).IsEqualTo(2);
        await Assert.That(UnicodeWidth.Width(new Rune(0x1F7E0))).IsEqualTo(2);
        await Assert.That(UnicodeWidth.Width(new Rune(0x16FF0))).IsEqualTo(2);
        await Assert.That(UnicodeWidth.Width(new Rune(0x18CFF))).IsEqualTo(2);
    }

    [Test]
    public async Task AmbiguousStaysNarrow_NeighborsStayWide()
    {
        await Assert.That(UnicodeWidth.Width(new Rune(0x3248))).IsEqualTo(1);
        await Assert.That(UnicodeWidth.Width(new Rune(0x324F))).IsEqualTo(1);
        await Assert.That(UnicodeWidth.Width(new Rune(0x3247))).IsEqualTo(2);
        await Assert.That(UnicodeWidth.Width(new Rune(0x3250))).IsEqualTo(2);
    }

    [Test]
    public async Task TerminalFaithfulPins()
    {
        // Regional indicators pair at the string level; per-rune they are 1.
        await Assert.That(UnicodeWidth.Width(new Rune(0x1F1E6))).IsEqualTo(1);
        // Emoji-presentation-but-narrow symbols render one cell in terminals.
        await Assert.That(UnicodeWidth.Width(new Rune(0x00A9))).IsEqualTo(1);
        // Halfwidth voicing marks occupy their own cell.
        await Assert.That(UnicodeWidth.Width(new Rune(0xFF9E))).IsEqualTo(1);
        // U+17D8 needs a 3-cell model the grid does not have (stays 1, see audit).
        await Assert.That(UnicodeWidth.Width(new Rune(0x17D8))).IsEqualTo(1);
        // Emoji modifiers are wide.
        await Assert.That(UnicodeWidth.Width(new Rune(0x1F3FB))).IsEqualTo(2);
    }
}

public class Eng9TailInvariantTests
{
    private static readonly Rune Cjk = new(0x4E2D);
    private static readonly CellStyle Red = new(PackedColor.Indexed(1));

    [Test]
    public async Task Fill_WideCell_AtRectRightEdge_SkipsPair()
    {
        var buf = new ScreenBuffer(4, 1);
        buf.Fill(new Rect(3, 0, 2, 1), Cell.From(Cjk, CellStyle.Plain));

        // Pair would cross the rect edge — ratatui skip policy, cell stays blank.
        await Assert.That(buf.Get(3, 0).IsBlankSpace).IsTrue();
    }

    [Test]
    public async Task Fill_WideCell_ClearsOrphanNeighborTail()
    {
        var buf = new ScreenBuffer(6, 1);
        _ = buf.SetRune(2, 0, Cjk, CellStyle.Plain); // pair (2,3)
        buf.Fill(new Rect(1, 0, 2, 1), Cell.From(Cjk, CellStyle.Plain)); // pair (1,2)

        await Assert.That(buf.Get(1, 0).Width).IsEqualTo(Cell.Wide);
        await Assert.That(buf.Get(2, 0).Width).IsEqualTo(Cell.WSkip);
        // Old pair (2,3) lost its lead to the fill — its tail must not survive alone.
        await Assert.That(buf.Get(3, 0).IsBlankSpace).IsTrue();
    }

    [Test]
    public async Task SetStyleAt_Tail_RoutesToLead_RestylesWholePair()
    {
        var buf = new ScreenBuffer(6, 1);
        _ = buf.SetRune(2, 0, Cjk, CellStyle.Plain);
        bool ok = buf.SetStyleAt(3, 0, Red);

        await Assert.That(ok).IsTrue();
        await Assert.That(buf.Get(2, 0).Width).IsEqualTo(Cell.Wide);
        await Assert.That(buf.Get(3, 0).Width).IsEqualTo(Cell.WSkip);
        await Assert.That(buf.Get(2, 0).Fg).IsEqualTo(buf.Get(3, 0).Fg);
        await Assert.That(buf.Get(2, 0).Fg).IsNotEqualTo(0u);
    }

    [Test]
    public async Task SetText_WideRejectedAtEdge_LeavesBlank()
    {
        var buf = new ScreenBuffer(3, 1);
        buf.SetText(0, 0, "ab" + Cjk, CellStyle.Plain);

        await Assert.That(buf.Get(0, 0).Rune).IsEqualTo('a');
        await Assert.That(buf.Get(1, 0).Rune).IsEqualTo('b');
        await Assert.That(buf.Get(2, 0).IsBlankSpace).IsTrue();
    }

    [Test]
    public async Task SetText_WideAtPenultimateColumn_ExactFit()
    {
        var buf = new ScreenBuffer(4, 1);
        buf.SetText(0, 0, "ab" + Cjk + "c", CellStyle.Plain);

        await Assert.That(buf.Get(2, 0).Width).IsEqualTo(Cell.Wide);
        await Assert.That(buf.Get(3, 0).Width).IsEqualTo(Cell.WSkip);
    }

    [Test]
    public async Task OverwriteWideWithWide_KeepsPairStructure()
    {
        var buf = new ScreenBuffer(6, 1);
        _ = buf.SetRune(0, 0, Cjk, CellStyle.Plain);
        _ = buf.SetRune(2, 0, Cjk, CellStyle.Plain);
        bool placed = buf.SetRune(0, 0, new Rune(0x1F600), CellStyle.Plain);

        await Assert.That(placed).IsTrue();
        await Assert.That(buf.Get(0, 0).Width).IsEqualTo(Cell.Wide);
        await Assert.That(buf.Get(0, 0).Rune).IsEqualTo(0x1F600);
        await Assert.That(buf.Get(1, 0).Width).IsEqualTo(Cell.WSkip);
        await Assert.That(buf.Get(2, 0).Width).IsEqualTo(Cell.Wide);
        await Assert.That(buf.Get(3, 0).Width).IsEqualTo(Cell.WSkip);
    }
}

public class Eng9SequencePinTests
{
    [Test]
    public async Task SetText_Vs16Cluster_MeasuresPerRune()
    {
        var buf = new ScreenBuffer(10, 1);
        buf.SetText(0, 0, "\u2764\uFE0F", CellStyle.Plain); // U+2764 U+FE0F, text presentation

        await Assert.That(UnicodeWidth.Width("\u2764\uFE0F")).IsEqualTo(1);
        await Assert.That(buf.Get(0, 0).Width).IsEqualTo(Cell.Narrow);
        await Assert.That(buf.Get(1, 0).IsBlankSpace).IsTrue();
    }

    [Test]
    public async Task SetText_CombiningMark_DoesNotAdvance()
    {
        var buf = new ScreenBuffer(10, 1);
        buf.SetText(0, 0, "e\u0301", CellStyle.Plain); // e + U+0301, NFD on purpose

        await Assert.That(UnicodeWidth.Width("e\u0301")).IsEqualTo(1);
        await Assert.That(buf.Get(0, 0).Rune).IsEqualTo('e');
        await Assert.That(buf.Get(1, 0).IsBlankSpace).IsTrue();
    }

    [Test]
    public async Task SetRune_ControlAndC1_PaintNothing()
    {
        var buf = new ScreenBuffer(10, 1);
        bool nul = buf.SetRune(0, 0, new Rune(0), CellStyle.Plain);
        bool csi = buf.SetRune(1, 0, new Rune(0x9B), CellStyle.Plain);

        await Assert.That(nul).IsTrue();
        await Assert.That(csi).IsTrue();
        await Assert.That(buf.Get(0, 0).IsBlankSpace).IsTrue();
        await Assert.That(buf.Get(1, 0).IsBlankSpace).IsTrue();
    }

    [Test]
    public async Task SetText_ZeroWidthOnly_PaintsNothing()
    {
        var buf = new ScreenBuffer(4, 1);
        buf.SetText(0, 0, "\u0301\uFE0F", CellStyle.Plain); // U+0301 U+FE0F

        for (int x = 0; x < 4; x++)
        {
            await Assert.That(buf.Get(x, 0).IsBlankSpace).IsTrue();
        }
    }
}

public class Eng9ForcedWidthTests
{
    [Test]
    public async Task PutRuneWidth_ExplicitAdvance_Honored()
    {
        var backend = new RecordingBackend();
        var w = new AnsiWriter(backend);
        w.BeginFrame();
        w.MoveTo(0, 0);
        w.PutRuneWidth(new Rune('A'), 3);
        await w.EndFrameAsync();

        await Assert.That(w.TrackedX).IsEqualTo(3);
        await Assert.That(backend.Text.Contains("A")).IsTrue();
    }

    [Test]
    public async Task FromRaw_PreservesSkipWidth()
    {
        await Assert.That(Cell.FromRaw(0, 0, 0, 0, Cell.WSkip)).IsEqualTo(Cell.WideTail);
        await Assert.That(Cell.WideTail.Width).IsEqualTo(Cell.WSkip);
    }
}
