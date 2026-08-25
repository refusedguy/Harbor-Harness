using System.Buffers;
using System.Text;

namespace Harbor.Tui.ConsoleEx.Rendering;

/// <summary>
/// Double-duty screen grid (celldiff §1.2): BACK holds what panels painted
/// this frame, FRONT mirrors the terminal after a flush. The backing arrays
/// grow geometrically and are never shrunk — resizing within capacity is
/// allocation-free; only growth allocates.
///
/// Wide-char invariants (§1.3): a wide rune occupies its lead cell plus a
/// <see cref="Cell.WideTail"/> cell; overwriting either half of an existing
/// pair blanks the whole pair first so the diff repaints both halves and no
/// glyph ghost survives.
/// </summary>
public sealed class ScreenBuffer
{
    private const ulong FnvOffset = 0xCBF2_9CE4_8422_2325UL;
    private const ulong FnvPrime = 0x0000_0100_0000_01B3UL;

    private Cell[] _cells;
    private bool[] _rowHashValid;
    private int _capCols;
    private int _capRows;

    public ScreenBuffer(int cols, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cols);
        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        _cells = [];
        _rowHashValid = [];
        Resize(cols, rows);
    }

    public int Cols { get; private set; }
    public int Rows { get; private set; }

    /// <summary>Row hash cache — valid only where <see cref="IsRowHashValid"/> says so.</summary>
    public ulong[] RowHash { get; private set; } = [];

    internal bool IsRowHashValid(int y) => _rowHashValid[y];

    /// <summary>Backing array identity, for capacity-reuse assertions in tests.</summary>
    internal Cell[] CellsForTests => _cells;

    public ref Cell At(int x, int y) => ref _cells[(y * Cols) + x];

    public Cell Get(int x, int y) => _cells[(y * Cols) + x];

    // ── Geometry ───────────────────────────────────────────────────────────

    /// <summary>
    /// Changes visible geometry. Shrinking reuses the same array (only dims
    /// change); growing reallocates geometrically (≥ ×1.25). All rows are
    /// invalidated and blanked — content is repainted from state.
    /// </summary>
    public void Resize(int cols, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cols);
        ArgumentOutOfRangeException.ThrowIfNegative(rows);

        Cols = cols;
        Rows = rows;
        long needed = (long)cols * rows;

        if (needed > _cells.Length || _cells.Length == 0 && needed > 0)
        {
            int targetCols = Math.Max(_capCols, cols);
            int targetRows = Math.Max(_capRows, rows);
            while ((long)targetCols * targetRows < needed)
            {
                if (targetCols <= targetRows)
                {
                    targetCols = Math.Max(targetCols + 1, (int)(targetCols * 1.25));
                }
                else
                {
                    targetRows = Math.Max(targetRows + 1, (int)(targetRows * 1.25));
                }
            }

            _capCols = targetCols;
            _capRows = targetRows;
            _cells = new Cell[(long)targetCols * targetRows];
            RowHash = new ulong[targetRows];
            _rowHashValid = new bool[targetRows];
            BlankAll();
            return;
        }

        if (_rowHashValid.Length < rows)
        {
            var hash = new ulong[Math.Max(rows, _capRows)];
            var valid = new bool[Math.Max(rows, _capRows)];
            Array.Copy(RowHash, hash, Math.Min(RowHash.Length, hash.Length));
            Array.Copy(_rowHashValid, valid, Math.Min(_rowHashValid.Length, valid.Length));
            RowHash = hash;
            _rowHashValid = valid;
        }

        BlankAll();
    }

    public void BlankAll()
    {
        Array.Fill(_cells, Cell.Blank, 0, Cols * Rows);
        InvalidateAll();
    }

    public void InvalidateAll() => Array.Clear(_rowHashValid, 0, Rows);

    public void MarkRowDirty(int y)
    {
        if ((uint)y < (uint)Rows)
        {
            _rowHashValid[y] = false;
        }
    }

    // ── Painting ───────────────────────────────────────────────────────────

    public void FillAll(in Cell cell) => Fill(new Rect(0, 0, Cols, Rows), in cell);

    public void Fill(Rect rect, in Cell cell)
    {
        var clipped = rect.Intersect(new Rect(0, 0, Cols, Rows));
        for (int y = clipped.Y; y < clipped.Bottom; y++)
        {
            int rowBase = y * Cols;
            for (int x = clipped.X; x < clipped.Right; x++)
            {
                ClearWidePairAt(x, y);
                _cells[rowBase + x] = cell;
            }

            _rowHashValid[y] = false;
        }
    }

    /// <summary>
    /// Places one rune with wide-char handling. Zero-width runes are ignored
    /// (documented simplification: per-rune widths, VS16/ZWJ are no-ops).
    /// Returns false when a wide rune does not fit at the row edge — nothing
    /// is painted then (ratatui skip policy).
    /// </summary>
    public bool SetRune(int x, int y, Rune rune, in CellStyle style)
    {
        if ((uint)x >= (uint)Cols || (uint)y >= (uint)Rows)
        {
            return false;
        }

        int width = UnicodeWidth.Width(rune);
        if (width == 0)
        {
            return true; // zero-width: attach nowhere, paint nothing
        }

        if (width == 2 && x + 1 >= Cols)
        {
            return false; // wide does not fit before the right edge
        }

        ClearWidePairAt(x, y);

        var cell = Cell.From(rune, style);
        int baseIndex = (y * Cols) + x;
        if (width == 2)
        {
            // If the next cell leads its own wide pair, orphaning its tail at
            // x+2 would leave a ghost half — reset that pair too.
            if (_cells[baseIndex + 1].Width == Cell.Wide && x + 2 < Cols)
            {
                ClearWidePairAt(x + 1, y);
            }

            _cells[baseIndex] = cell;
            _cells[baseIndex + 1] = Cell.WideTail;
        }
        else
        {
            _cells[baseIndex] = cell;
        }

        _rowHashValid[y] = false;
        return true;
    }

    /// <summary>Writes a text run starting at (x,y), stopping at the row end.</summary>
    public void SetText(int x, int y, ReadOnlySpan<char> text, in CellStyle style)
    {
        if ((uint)y >= (uint)Rows)
        {
            return;
        }

        var rest = text;
        int cursor = x;
        while (!rest.IsEmpty && cursor < Cols)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out int consumed) != OperationStatus.Done)
            {
                consumed = 1;
                rune = Rune.ReplacementChar;
            }

            if (!SetRune(cursor, y, rune, style))
            {
                break;
            }

            cursor += UnicodeWidth.Width(rune);
            rest = rest[consumed..];
        }
    }

    /// <summary>Recolors one narrow cell without touching its rune.</summary>
    public bool SetStyleAt(int x, int y, in CellStyle style)
    {
        if ((uint)x >= (uint)Cols || (uint)y >= (uint)Rows)
        {
            return false;
        }

        ref Cell cell = ref At(x, y);
        if (cell.Width != Cell.Wide)
        {
            cell = Cell.From(new Rune(cell.Rune), style);
            _rowHashValid[y] = false;
            return true;
        }

        return false;
    }

    // ── Row hashes (§2.3) ──────────────────────────────────────────────────

    /// <summary>Returns the cached hash, computing it on first use since last dirt.</summary>
    public ulong RowHashCode(int y)
    {
        if (!_rowHashValid[y])
        {
            ComputeRowHash(y);
        }

        return RowHash[y];
    }

    private void ComputeRowHash(int y)
    {
        int baseIndex = y * Cols;
        ulong hash = FnvOffset;
        for (int x = 0; x < Cols; x++)
        {
            ref readonly Cell c = ref _cells[baseIndex + x];
            hash ^= (uint)c.Rune;
            hash *= FnvPrime;
            hash ^= c.Fg;
            hash *= FnvPrime;
            hash ^= c.Bg;
            hash *= FnvPrime;
            hash ^= ((ulong)c.Flags << 8) | c.Width;
            hash *= FnvPrime;
        }

        RowHash[y] = hash;
        _rowHashValid[y] = true;
    }

    /// <summary>
    /// If (x,y) sits on any half of a wide pair, resets BOTH halves to blanks.
    /// This is what guarantees the diff repaints the surviving half (§1.3).
    /// </summary>
    private void ClearWidePairAt(int x, int y)
    {
        int index = (y * Cols) + x;
        if (_cells[index].Width == Cell.WSkip && x > 0 && _cells[index - 1].Width == Cell.Wide)
        {
            _cells[index - 1] = Cell.Blank;
        }

        switch (_cells[index].Width)
        {
            case Cell.Wide when x + 1 < Cols:
                _cells[index + 1] = Cell.Blank;
                break;
            case Cell.Narrow:
                break;
        }
    }
}
