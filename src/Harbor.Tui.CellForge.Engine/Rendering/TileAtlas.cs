// #2696 item 13: immutable atlas of fixed-size cell tiles for the engine's
// sprite/tile route. A tile is a row-major grid of ready Cell values (lead and
// wide-tail cells included), so blitting is a plain copy — no glyph shaping,
// no allocation on the paint path. Transparency is a sentinel rune, which
// keeps the 16-byte cell budget and the diff protocol untouched.
// BCL-only, AOT-compatible, render-thread owned; no Harbor references.

using System;
using System.Collections.Generic;
using System.Text;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// A named collection of equal-sized cell tiles (#2696, item 13). The atlas
/// owns a fixed <see cref="TileWidth"/> × <see cref="TileHeight"/> grid per
/// tile; a <see cref="TileMapNode"/> samples those grids into a
/// <see cref="ScreenBuffer"/> with transparency and a scrolling offset.
///
/// <para>Tiles are prebuilt cells, not glyphs: a caller that needs a wide
/// glyph supplies the lead cell and its <see cref="Cell.WideTail"/> follow-up
/// itself. That keeps the sprite route byte-compatible with the cell-diff
/// protocol and free of per-frame shaping or allocation.</para>
/// </summary>
public sealed class TileAtlas
{
    /// <summary>
    /// Runes at or below zero are not Unicode scalar values, so this sentinel
    /// can never collide with a printable cell or with <see cref="Cell.WideTail"/>
    /// (whose rune is 0). A tile cell carrying it is left unpainted.
    /// </summary>
    public const int TransparentRune = -1;

    /// <summary>Ready-made transparent cell for building tile grids.</summary>
    public static Cell Transparent { get; } = Cell.FromRaw(TransparentRune, 0, 0, 0, Cell.Narrow);

    private readonly Dictionary<string, Cell[]> _tiles = new(StringComparer.Ordinal);

    /// <summary>Creates an atlas whose every tile is this size in cells.</summary>
    public TileAtlas(int tileWidth, int tileHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tileWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tileHeight);
        TileWidth = tileWidth;
        TileHeight = tileHeight;
    }

    /// <summary>Column count of every tile in the atlas.</summary>
    public int TileWidth { get; }

    /// <summary>Row count of every tile in the atlas.</summary>
    public int TileHeight { get; }

    /// <summary>Registers a tile from a row-major cell grid.</summary>
    /// <param name="id">Stable tile name referenced by the map.</param>
    /// <param name="cells">Exactly <c>TileWidth * TileHeight</c> cells, copied into the atlas.</param>
    public void Define(string id, ReadOnlySpan<Cell> cells)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        int expected = TileWidth * TileHeight;
        if (cells.Length != expected)
        {
            throw new ArgumentException(
                $"Tile '{id}' has {cells.Length} cells; expected {expected} ({TileWidth}x{TileHeight}).",
                nameof(cells));
        }

        _tiles[id] = cells.ToArray();
    }

    /// <summary>
    /// Convenience overload that builds a tile from ASCII art. One character
    /// maps to one cell; a space becomes transparent, any other character is
    /// painted with <paramref name="style"/>. Rows shorter than
    /// <see cref="TileWidth"/> are padded with transparent cells.
    /// </summary>
    public void DefineAscii(string id, ReadOnlySpan<string> rows, in CellStyle style)
    {
        if (rows.Length != TileHeight)
        {
            throw new ArgumentException(
                $"Tile '{id}' has {rows.Length} rows; expected {TileHeight}.",
                nameof(rows));
        }

        var cells = new Cell[TileWidth * TileHeight];
        for (int y = 0; y < TileHeight; y++)
        {
            string row = rows[y] ?? string.Empty;
            if (row.Length > TileWidth)
            {
                throw new ArgumentException(
                    $"Row {y} of tile '{id}' is {row.Length} chars wide; expected at most {TileWidth}.",
                    nameof(rows));
            }

            for (int x = 0; x < TileWidth; x++)
            {
                char ch = x < row.Length ? row[x] : ' ';
                cells[(y * TileWidth) + x] = ch == ' '
                    ? Transparent
                    : Cell.From(new Rune(ch), style);
            }
        }

        Define(id, cells);
    }

    /// <summary>Looks up a tile's cells. The returned memory is owned by the atlas.</summary>
    public ReadOnlyMemory<Cell>? Get(string id) =>
        _tiles.TryGetValue(id, out Cell[]? cells) ? cells : null;
}
