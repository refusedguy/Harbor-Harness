// #2696 item 13: tile blit node for the engine's sprite/tile route. A LeafNode
// that samples a named tile map into a ScreenBuffer, skipping transparent
// cells and wrapping at the map edges so a scrolling offset reveals an
// endless surface. BCL-only, AOT-compatible, render-thread owned.

using System;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Blits a <see cref="TileAtlas"/> tile map into a
/// <see cref="ScreenBuffer"/> (#2696, item 13). The map is a grid of tile
/// names; each frame the node walks only its clipped paint area, resolves the
/// map cell under every screen cell (applying the scroll offset and wrapping)
/// and copies the atlas cell when it is opaque. Transparent cells leave the
/// underlying buffer untouched, so a map can float above already-painted
/// content.
///
/// <para>There is no per-frame allocation: the map and atlas are owned by the
/// node, and the paint loop only reads them and writes cells.</para>
/// </summary>
public sealed class TileMapNode : LeafNode
{
    private readonly TileAtlas _atlas;
    private readonly int _mapRows;
    private readonly int _mapCols;
    private readonly string?[] _tiles;
    private int _scrollX;
    private int _scrollY;

    /// <summary>Creates an empty map of the given tile dimensions.</summary>
    public TileMapNode(TileAtlas atlas, int mapRows, int mapCols)
    {
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mapRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mapCols);
        _atlas = atlas;
        _mapRows = mapRows;
        _mapCols = mapCols;
        _tiles = new string?[mapRows * mapCols];
    }

    /// <summary>Map height in tiles.</summary>
    public int MapRows => _mapRows;

    /// <summary>Map width in tiles.</summary>
    public int MapCols => _mapCols;

    /// <summary>Horizontal scroll offset in cells (content shifts left as it grows).</summary>
    public int ScrollX => _scrollX;

    /// <summary>Vertical scroll offset in cells (content shifts up as it grows).</summary>
    public int ScrollY => _scrollY;

    /// <summary>Assigns a tile name to a map slot. A null id clears the slot.</summary>
    public void SetTile(int mapRow, int mapCol, string? tileId)
    {
        if ((uint)mapRow >= (uint)_mapRows || (uint)mapCol >= (uint)_mapCols)
        {
            throw new ArgumentOutOfRangeException(
                mapRow >= _mapRows ? nameof(mapRow) : nameof(mapCol));
        }

        _tiles[(mapRow * _mapCols) + mapCol] = tileId;
    }

    /// <summary>Updates the scrolling offset in cells. Negative values are allowed and wrap.</summary>
    public void SetScroll(int x, int y)
    {
        _scrollX = x;
        _scrollY = y;
    }

    /// <inheritdoc />
    public override Size Measure(Constraints constraints) => new(
        Math.Min(constraints.Width, _mapCols * _atlas.TileWidth),
        Math.Min(constraints.Height, _mapRows * _atlas.TileHeight));

    /// <inheritdoc />
    public override void Paint(ScreenBuffer buffer, Rect clip)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        var area = PaintArea(clip).Intersect(new Rect(0, 0, buffer.Cols, buffer.Rows));
        if (area.Width <= 0 || area.Height <= 0)
        {
            return;
        }

        int tileW = _atlas.TileWidth;
        int tileH = _atlas.TileHeight;
        int mapW = _mapCols * tileW;
        int mapH = _mapRows * tileH;
        int originX = Arranged.X;
        int originY = Arranged.Y;

        for (int y = area.Y; y < area.Bottom; y++)
        {
            int my = Wrap((y - originY) + _scrollY, mapH);
            int tileRow = my / tileH;
            int localY = my % tileH;
            bool rowTouched = false;

            for (int x = area.X; x < area.Right; x++)
            {
                int mx = Wrap((x - originX) + _scrollX, mapW);
                int tileCol = mx / tileW;
                int localX = mx % tileW;

                string? id = _tiles[(tileRow * _mapCols) + tileCol];
                if (id is null || _atlas.Get(id) is not { } cells)
                {
                    continue;
                }

                Cell cell = cells.Span[(localY * tileW) + localX];
                if (cell.Rune == TileAtlas.TransparentRune)
                {
                    continue;
                }

                buffer.At(x, y) = cell;
                rowTouched = true;
            }

            if (rowTouched)
            {
                buffer.MarkRowDirty(y);
            }
        }
    }

    /// <summary>Wraps a map coordinate into <c>[0, modulus)</c> (handles negatives).</summary>
    private static int Wrap(int value, int modulus)
    {
        if (modulus <= 0)
        {
            return 0;
        }

        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }
}
