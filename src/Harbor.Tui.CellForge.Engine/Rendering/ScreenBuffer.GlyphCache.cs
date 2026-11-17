// #2352: glyph-cache integration for the engine screen grid.
// ScreenBuffer is a partial class, so the cache-backed paint path lives in its
// own file. Rendering/ScreenBuffer.cs and its shared twin stay byte-identical
// (the parity rule in CellForgeEngineVocabularyParityRules grades that file),
// and the cache itself is engine-only — the shared layer has no dependency on
// the backend-specific engine.

using System.Text;

namespace Harbor.Tui.CellForge.Rendering;

public sealed partial class ScreenBuffer
{
    /// <summary>
    ///     #2352: optional ready-cell-pattern cache. When set,
    ///     <see cref="SetGlyph"/> resolves each glyph through it, so repeated
    ///     symbols skip the width lookup and the style packing. Owned by one
    ///     render session — <see cref="DiffEngine"/> creates and shares it.
    /// </summary>
    public GlyphCache? GlyphCache { get; set; }

    /// <summary>
    ///     Paints a glyph from the cache. On a hit the prebuilt
    ///     <c>Cell[]</c> is copied into the grid: no width lookup, no style
    ///     packing, no allocation. Wide patterns keep the §1.3 lead+tail
    ///     structure and the edge/clobber rules of <see cref="SetRune"/>.
    ///     With no cache attached this delegates to the verbatim
    ///     <see cref="SetRune"/> path, so existing callers are unaffected.
    ///     Returns false when out of range or a wide glyph does not fit.
    /// </summary>
    public bool SetGlyph(int x, int y, int rune, in CellStyle style)
    {
        if ((uint)x >= (uint)Cols || (uint)y >= (uint)Rows)
        {
            return false;
        }

        GlyphCache? cache = GlyphCache;
        if (cache is null)
        {
            return SetRune(x, y, new Rune(rune), style);
        }

        Cell[] pattern = cache.GetOrInsert(rune, style);
        if (pattern.Length == 0)
        {
            return true; // zero-width glyph: paint nothing
        }

        if (pattern.Length == 2 && x + 1 >= Cols)
        {
            return false; // wide glyph does not fit before the right edge
        }

        ClearWidePairAt(x, y);
        int baseIndex = (y * Cols) + x;
        if (pattern.Length == 2)
        {
            // Orphaning the next pair's tail at x+2 would leave a ghost half.
            if (_cells[baseIndex + 1].Width == Cell.Wide && x + 2 < Cols)
            {
                ClearWidePairAt(x + 1, y);
            }

            _cells[baseIndex] = pattern[0];
            _cells[baseIndex + 1] = pattern[1];
        }
        else
        {
            _cells[baseIndex] = pattern[0];
        }

        _rowHashValid[y] = false;
        return true;
    }

    /// <summary>
    ///     Column advance of a glyph pattern already in the cache. A narrow
    ///     pattern occupies one cell, a wide one two (lead + tail), a zero-width
    ///     one none — so the pattern length IS the advance. This lets a text
    ///     loop reuse the cache's resolved width for cursor movement instead of
    ///     calling <see cref="UnicodeWidth.Width(Rune)"/> a second time. Returns
    ///     the direct width when no cache is attached.
    /// </summary>
    public int GlyphAdvance(int rune, in CellStyle style)
    {
        GlyphCache? cache = GlyphCache;
        return cache is null
            ? (Rune.IsValid(rune) ? UnicodeWidth.Width(new Rune(rune)) : 1)
            : cache.GetOrInsert(rune, style).Length;
    }
}
