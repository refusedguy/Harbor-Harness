// #2696 item 13: the tile atlas and tile-map blit node. These assert observable
// paint behaviour (which cells land in the buffer, transparency, wrapping and
// clipping), not the loop that produces it.
using System.Text;

namespace Harbor.Tui.CellForge.Tests;

public sealed class TileMapNodeTests
{
    private static EngineCells.ScreenBuffer MarkedBuffer(int cols, int rows, char marker = 'z')
    {
        var buffer = new EngineCells.ScreenBuffer(cols, rows);
        buffer.FillAll(EngineCells.Cell.From(new Rune(marker), EngineCells.CellStyle.Plain));
        return buffer;
    }

    private static EngineCells.TileAtlas Atlas(int w, int h) => new(w, h);

    [Test]
    public async Task Atlas_DefineAscii_MapsSpacesToTransparentAndGlyphsToCells()
    {
        var atlas = Atlas(3, 2);
        atlas.DefineAscii("box", ["A B", " C "], EngineCells.CellStyle.Plain);

        var cells = atlas.Get("box")!.Value.ToArray();
        await Assert.That(cells.Length).IsEqualTo(6);
        await Assert.That(cells[0].Rune).IsEqualTo((int)'A');
        await Assert.That(cells[1].Rune).IsEqualTo(EngineCells.TileAtlas.TransparentRune);
        await Assert.That(cells[2].Rune).IsEqualTo((int)'B');
        await Assert.That(cells[3].Rune).IsEqualTo(EngineCells.TileAtlas.TransparentRune);
        await Assert.That(cells[4].Rune).IsEqualTo((int)'C');
        await Assert.That(cells[5].Rune).IsEqualTo(EngineCells.TileAtlas.TransparentRune);
        await Assert.That(atlas.Get("missing").HasValue).IsFalse();
    }

    [Test]
    public async Task Atlas_WrongGridSize_Throws()
    {
        var atlas = Atlas(2, 1);
        bool threw = false;
        try
        {
            atlas.DefineAscii("bad", ["ABC"], EngineCells.CellStyle.Plain);
        }
        catch (ArgumentException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task Node_BlitsTile_IntoArrangedRect()
    {
        var atlas = Atlas(2, 1);
        atlas.DefineAscii("a", ["AB"], EngineCells.CellStyle.Plain);
        var node = new EngineCells.TileMapNode(atlas, 1, 1);
        node.SetTile(0, 0, "a");
        node.Arrange(new EngineCells.Rect(1, 1, 2, 1));

        var buffer = MarkedBuffer(6, 3);
        var full = new EngineCells.Rect(0, 0, 6, 3);
        node.Paint(buffer, full);

        await Assert.That(buffer.Get(1, 1).Rune).IsEqualTo((int)'A');
        await Assert.That(buffer.Get(2, 1).Rune).IsEqualTo((int)'B');
        await Assert.That(buffer.Get(0, 1).Rune).IsEqualTo((int)'z');
        await Assert.That(buffer.Get(3, 1).Rune).IsEqualTo((int)'z');
        await Assert.That(buffer.Get(1, 0).Rune).IsEqualTo((int)'z');
        await Assert.That(buffer.Get(1, 2).Rune).IsEqualTo((int)'z');
    }

    [Test]
    public async Task Node_TransparentCells_LeaveUnderlyingBufferUntouched()
    {
        var atlas = Atlas(3, 1);
        atlas.DefineAscii("h", ["A B"], EngineCells.CellStyle.Plain);
        var node = new EngineCells.TileMapNode(atlas, 1, 1);
        node.SetTile(0, 0, "h");
        node.Arrange(new EngineCells.Rect(0, 0, 3, 1));

        var buffer = MarkedBuffer(3, 1);
        node.Paint(buffer, new EngineCells.Rect(0, 0, 3, 1));

        await Assert.That(buffer.Get(0, 0).Rune).IsEqualTo((int)'A');
        await Assert.That(buffer.Get(1, 0).Rune).IsEqualTo((int)'z');
        await Assert.That(buffer.Get(2, 0).Rune).IsEqualTo((int)'B');
    }

    [Test]
    public async Task Node_ScrollOffset_ShiftsAndWrapsTheMap()
    {
        var atlas = Atlas(1, 1);
        atlas.DefineAscii("l", ["L"], EngineCells.CellStyle.Plain);
        atlas.DefineAscii("r", ["R"], EngineCells.CellStyle.Plain);
        var node = new EngineCells.TileMapNode(atlas, 1, 2);
        node.SetTile(0, 0, "l");
        node.SetTile(0, 1, "r");
        node.Arrange(new EngineCells.Rect(0, 0, 2, 1));

        var full = new EngineCells.Rect(0, 0, 2, 1);

        var zero = MarkedBuffer(2, 1);
        node.Paint(zero, full);
        await Assert.That(zero.Get(0, 0).Rune).IsEqualTo((int)'L');
        await Assert.That(zero.Get(1, 0).Rune).IsEqualTo((int)'R');

        node.SetScroll(1, 0);
        var shifted = MarkedBuffer(2, 1);
        node.Paint(shifted, full);
        await Assert.That(shifted.Get(0, 0).Rune).IsEqualTo((int)'R');
        await Assert.That(shifted.Get(1, 0).Rune).IsEqualTo((int)'L');

        node.SetScroll(-1, 0);
        var negative = MarkedBuffer(2, 1);
        node.Paint(negative, full);
        await Assert.That(negative.Get(0, 0).Rune).IsEqualTo((int)'R');
        await Assert.That(negative.Get(1, 0).Rune).IsEqualTo((int)'L');
    }

    [Test]
    public async Task Node_EmptyMapSlot_PaintsNothing()
    {
        var atlas = Atlas(1, 1);
        var node = new EngineCells.TileMapNode(atlas, 1, 1);
        node.Arrange(new EngineCells.Rect(0, 0, 1, 1));

        var buffer = MarkedBuffer(1, 1);
        node.Paint(buffer, new EngineCells.Rect(0, 0, 1, 1));

        await Assert.That(buffer.Get(0, 0).Rune).IsEqualTo((int)'z');
    }

    [Test]
    public async Task Node_RespectsClipAndBufferBounds()
    {
        var atlas = Atlas(1, 1);
        atlas.DefineAscii("x", ["X"], EngineCells.CellStyle.Plain);
        var node = new EngineCells.TileMapNode(atlas, 2, 2);
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                node.SetTile(y, x, "x");
            }
        }

        var smallClip = MarkedBuffer(8, 8);
        node.Arrange(new EngineCells.Rect(5, 5, 4, 4));
        node.Paint(smallClip, new EngineCells.Rect(6, 6, 1, 1));
        await Assert.That(smallClip.Get(6, 6).Rune).IsEqualTo((int)'X');
        await Assert.That(smallClip.Get(5, 5).Rune).IsEqualTo((int)'z');
        await Assert.That(smallClip.Get(7, 7).Rune).IsEqualTo((int)'z');

        var bounded = MarkedBuffer(8, 8);
        node.Arrange(new EngineCells.Rect(6, 6, 4, 4));
        node.Paint(bounded, new EngineCells.Rect(0, 0, 8, 8));
        await Assert.That(bounded.Get(7, 7).Rune).IsEqualTo((int)'X');
        await Assert.That(bounded.Get(5, 5).Rune).IsEqualTo((int)'z');
    }

    [Test]
    public async Task Node_Measure_ClampsToConstraints()
    {
        var atlas = Atlas(2, 2);
        var node = new EngineCells.TileMapNode(atlas, 4, 3);

        var clamped = node.Measure(new EngineCells.Constraints(4, 5));
        var full = node.Measure(new EngineCells.Constraints(100, 100));

        await Assert.That(clamped.Width).IsEqualTo(4);
        await Assert.That(clamped.Height).IsEqualTo(5);
        await Assert.That(full.Width).IsEqualTo(6);
        await Assert.That(full.Height).IsEqualTo(8);
        await Assert.That(node.MapRows).IsEqualTo(4);
        await Assert.That(node.MapCols).IsEqualTo(3);
    }
}
