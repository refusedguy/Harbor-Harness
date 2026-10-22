using System.Text;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Widgets;

namespace Harbor.TestKit;

/// <summary>
/// The one place the render skeleton is built: the grid a widget paints into,
/// the paint context over it, and the art that comes back out.
/// </summary>
/// <remarks>
/// <para>
/// The CellForge test suite built that skeleton by hand in three hundred and
/// fifty-six places: a <see cref="ScreenBuffer" /> of a given size, a
/// <see cref="BlockPaintContext" /> over it (ninety-two of them), and the art
/// that came back out. Every copy was free to drift from every other copy.
/// A clip could stop matching its buffer, and an art dump could stop matching
/// the one the golden fixtures were recorded against.
/// </para>
/// <para>
/// This type owns the skeleton. Its members are the axes the call sites
/// actually vary, and nothing else: the size of the grid, the grid itself,
/// the clip the widget paints into, and the widget under test. A call site
/// that needs a context of a different shape, such as a clamped height with
/// the panel-chrome separator or a skipped row, still builds the context
/// itself.
/// </para>
/// <para>
/// It lives in the UI-scoped sibling rather than the core kit because the
/// vocabulary it speaks (<see cref="ScreenBuffer" />,
/// <see cref="BlockPaintContext" />, <see cref="IChatBlock" />) belongs to
/// <c>Harbor.Ui.Framework.Rendering</c>. Putting it in the core kit would
/// put the UI framework back into the closure of the test projects that use
/// the core for contracts and doubles only.
/// </para>
/// </remarks>
public static class TestRender
{
    /// <summary>
    /// A grid of the given size — the surface a widget paints into.
    /// </summary>
    public static ScreenBuffer Buffer(int width, int height) => new(width, height);

    /// <summary>
    /// The paint context over the WHOLE grid: the clip is the grid, the frame
    /// tick is zero, and no inline-image sink is wired.
    /// </summary>
    public static BlockPaintContext Ctx(ScreenBuffer buffer) =>
        new(buffer, new Rect(0, 0, buffer.Cols, buffer.Rows), 0);

    /// <summary>
    /// The paint context over a window of the grid — the top-left
    /// <paramref name="width" /> × <paramref name="height" /> cells.
    /// </summary>
    public static BlockPaintContext Ctx(ScreenBuffer buffer, int width, int height) =>
        new(buffer, new Rect(0, 0, width, height), 0);

    /// <summary>
    /// The paint context over an arbitrary clip of the grid.
    /// </summary>
    public static BlockPaintContext Ctx(ScreenBuffer buffer, int x, int y, int width, int height) =>
        new(buffer, new Rect(x, y, width, height), 0);

    /// <summary>
    /// The paint context over the whole grid, with the frame tick the
    /// spinner blocks animate off.
    /// </summary>
    public static BlockPaintContext Ctx(ScreenBuffer buffer, long tick) =>
        new(buffer, new Rect(0, 0, buffer.Cols, buffer.Rows), tick);

    /// <summary>
    /// The paint context over the whole grid, with the inline-image sink the
    /// image blocks place their bytes into.
    /// </summary>
    public static BlockPaintContext Ctx(ScreenBuffer buffer, IInlineImageSink? inlineImages) =>
        new(buffer, new Rect(0, 0, buffer.Cols, buffer.Rows), 0, inlineImages: inlineImages);

    /// <summary>
    /// The paint context over the whole grid, with the frame tick AND the
    /// inline-image sink — the two axes an animated image block needs.
    /// </summary>
    public static BlockPaintContext Ctx(ScreenBuffer buffer, long tick, IInlineImageSink? inlineImages) =>
        new(buffer, new Rect(0, 0, buffer.Cols, buffer.Rows), tick, inlineImages: inlineImages);

    /// <summary>
    /// Paints the block into a fresh grid of the given size and returns the
    /// art of the result — the grid is not handed back, because a caller
    /// that wanted the grid would ask for it.
    /// </summary>
    public static string Art(IChatBlock block, int width, int height)
    {
        var buffer = Buffer(width, height);
        block.Paint(Ctx(buffer));
        return Art(buffer);
    }

    /// <summary>
    /// Cosmetic rendering of a grid: one line per row, wide tails collapsed,
    /// control cells shown as <c>?</c>. The byte-for-byte shape the golden
    /// fixtures were recorded against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The art is built by APPENDING CHARACTERS, and it used to append
    /// STRINGS: <c>((char)rune).ToString()</c> allocated a one-character
    /// string for every cell of every grid. An 80x24 grid is 1 920 cells, so
    /// one call allocated 1 920 short-lived strings — about 46 KiB of garbage
    /// for a picture that is 2 KiB long. <c>Art</c> is called by every widget
    /// test in the CellForge suite and once per frame by the golden frames,
    /// so the garbage was paid thousands of times over in the <c>ui</c> shard.
    /// </para>
    /// <para>
    /// The rendering is unchanged: a char appended to the builder and a
    /// one-character string appended to it produce the SAME characters, so the
    /// art is byte-for-byte what it was and the golden fixtures still match.
    /// <c>TestRenderArtParityTests</c> in <c>Harbor.TestKit.Tests</c> pins
    /// that against the old shape, and measures what it cost.
    /// </para>
    /// </remarks>
    public static string Art(ScreenBuffer buffer)
    {
        var sb = new StringBuilder();
        for (int y = 0; y < buffer.Rows; y++)
        {
            for (int x = 0; x < buffer.Cols; x++)
            {
                var cell = buffer.Get(x, y);
                if (cell.Width == Cell.WSkip)
                {
                    continue; // tail half — glyph already drawn by its lead
                }

                sb.Append(ArtChar(cell.Rune));
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// The character a cell renders as: the rune itself when it is printable,
    /// and <c>?</c> for a control cell or a lone surrogate.
    /// </summary>
    /// <remarks>
    /// A <c>char</c>, not a <c>string</c>. The old shape returned
    /// <c>((char)rune).ToString()</c> — one heap string per cell — and the
    /// builder only ever wanted the character. See
    /// <see cref="Art(ScreenBuffer)" /> for the measurement.
    /// </remarks>
    private static char ArtChar(int rune) => rune switch
    {
        >= 0x20 and <= 0x7E or >= 0xA0 and <= 0xFFFD when !char.IsSurrogate((char)rune) => (char)rune,
        _ => '?',
    };
}
