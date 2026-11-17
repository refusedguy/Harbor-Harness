namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Per-cell underline color side-table (issue #2219). Like
/// <see cref="HyperlinkTable"/>, it lives NEXT TO the buffer so Cell keeps its
/// 16-byte layout: a color is a value in a flat array, not a Cell field. The
/// engine reads it when repainting a changed cell and folds SGR 58/59 into the
/// same SGR automaton as the style, so the color consumes no column.
///
/// <para><see cref="Enabled"/> is the capability switch: when the terminal
/// probe did not confirm underline color support, <see cref="ColorAt"/>
/// reports default and only classic SGR reaches the terminal.</para>
///
/// Render-thread only, like the diff itself. Not thread-safe.
/// </summary>
public sealed class UnderlineColorTable
{
    private PackedColor[] _colors;
    private int _cols;
    private int _rows;

    public UnderlineColorTable(int cols, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cols);
        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        _cols = cols;
        _rows = rows;
        _colors = new PackedColor[cols * rows];
    }

    public int Cols => _cols;
    public int Rows => _rows;

    /// <summary>Capability switch. Defaults to off, so an unprobed frame is classic.</summary>
    public bool Enabled { get; set; }

    public bool Set(int x, int y, PackedColor color)
    {
        if ((uint)x >= (uint)_cols || (uint)y >= (uint)_rows)
        {
            return false;
        }

        _colors[(y * _cols) + x] = color;
        return true;
    }

    public bool Clear(int x, int y) => Set(x, y, default);

    public void ClearAll() => Array.Clear(_colors, 0, _colors.Length);

    public void Resize(int cols, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cols);
        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        int needed = cols * rows;
        if (needed > _colors.Length)
        {
            _colors = new PackedColor[needed];
        }

        _cols = cols;
        _rows = rows;
        ClearAll();
    }

    /// <summary>Color at <c>(x, y)</c>, or default when disabled/out of range.</summary>
    public PackedColor ColorAt(int x, int y)
    {
        if (!Enabled || (uint)x >= (uint)_cols || (uint)y >= (uint)_rows)
        {
            return default;
        }

        return _colors[(y * _cols) + x];
    }
}
