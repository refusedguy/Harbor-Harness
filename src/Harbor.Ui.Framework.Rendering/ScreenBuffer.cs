using System.Buffers;
using System.Text;

namespace Harbor.Ui.Framework.Rendering;

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

    // R1 steal (epic #1155): per-cell diff directives live here, NOT in Cell —
    // the 16-byte cell layout and its five-compare equality stay untouched.
    // Both arrays track _cells.Length exactly (allocated in the same breath),
    // so any valid cell index is a valid option index. Paint ops (Fill/SetRune/
    // SetText/SetStyleAt) are sticky — they never touch these; BlankAll resets.
    private CellDiffOption[] _diffOptions;
    private ushort[] _forcedWidths;

    public ScreenBuffer(int cols, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cols);
        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        _cells = [];
        _rowHashValid = [];
        _diffOptions = [];
        _forcedWidths = [];
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
            _diffOptions = new CellDiffOption[_cells.Length];
            _forcedWidths = new ushort[_cells.Length];
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
        // Full clear drops diff directives too: a blanked screen is a blank
        // slate, and a stale Skip would hide the next paint. Per-cell paint ops
        // stay sticky (ratatui parity) — only this and Resize reset the table.
        Array.Clear(_diffOptions, 0, Cols * Rows);
        Array.Clear(_forcedWidths, 0, Cols * Rows);
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

    /// <summary>
    /// Fills the clipped rectangle. Wide fill cells keep the §1.3 pair
    /// structure: each lead gets a tail, a pair that would cross the rect's
    /// right edge is skipped (ratatui policy), and clobbered neighbors have
    /// their halves cleared so no ghost glyph survives.
    /// </summary>
    public void Fill(Rect rect, in Cell cell)
    {
        var clipped = rect.Intersect(new Rect(0, 0, Cols, Rows));
        bool wide = cell.Width == Cell.Wide;
        for (int y = clipped.Y; y < clipped.Bottom; y++)
        {
            int rowBase = y * Cols;
            for (int x = clipped.X; x < clipped.Right; )
            {
                ClearWidePairAt(x, y);
                if (wide)
                {
                    if (x + 1 >= clipped.Right)
                    {
                        x += 1; // pair does not fit before the rect edge — skip
                        continue;
                    }

                    if (_cells[rowBase + x + 1].Width == Cell.Wide && x + 2 < Cols)
                    {
                        ClearWidePairAt(x + 1, y); // don't orphan the next pair's tail
                    }

                    _cells[rowBase + x] = cell;
                    _cells[rowBase + x + 1] = Cell.WideTail;
                    x += 2;
                }
                else
                {
                    _cells[rowBase + x] = cell;
                    x += 1;
                }
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

    /// <summary>Recolors one cell without touching its rune. Wide clusters are
    /// restyled as a whole — lead plus tail — so animation blends never leave
    /// half-colored glyphs; a write landing on a tail is routed to its lead.</summary>
    public bool SetStyleAt(int x, int y, in CellStyle style)
    {
        if ((uint)x >= (uint)Cols || (uint)y >= (uint)Rows)
        {
            return false;
        }

        ref Cell cell = ref At(x, y);
        if (cell.Width == Cell.WSkip)
        {
            return x > 0 && SetStyleAt(x - 1, y, in style);
        }

        cell = Cell.From(new Rune(cell.Rune), style);
        if (cell.Width == Cell.Wide && x + 1 < Cols)
        {
            // Styled tail: rune 0 resolves to width 0 (WSkip) via Cell.From.
            At(x + 1, y) = Cell.From(new Rune(0), style);
        }

        _rowHashValid[y] = false;
        return true;
    }

    // ── Diff directives (R1 steal, epic #1155) ──────────────────────────

    /// <summary>Diff directive at (x,y) (None when never armed).</summary>
    public CellDiffOption GetDiffOption(int x, int y) => _diffOptions[(y * Cols) + x];

    /// <summary>
    /// Explicit cursor advance for <see cref="CellDiffOption.ForcedWidth"/>
    /// cells (0 unless armed with ForcedWidth — use sites clamp to ≥ 1).
    /// </summary>
    public ushort GetForcedWidth(int x, int y) => _forcedWidths[(y * Cols) + x];

    /// <summary>
    /// Arms a diff directive at (x,y). Sticky across paint ops (ratatui
    /// parity): repainting the cell keeps the directive — a ForcedWidth image
    /// placeholder is armed once, not every frame. Cleared by
    /// <see cref="BlankAll"/>/Resize. Marks the row dirty: the row hash folds
    /// the directive, so an option-only change still breaks hash equality and
    /// the fast path cannot hide it. Returns false when out of range.
    /// </summary>
    public bool SetDiffOption(int x, int y, CellDiffOption option, ushort forcedWidth = 0)
    {
        if ((uint)x >= (uint)Cols || (uint)y >= (uint)Rows)
        {
            return false;
        }

        int index = (y * Cols) + x;
        _diffOptions[index] = option;
        // A zero advance would stall the scan (_x += 0); clamp at arm time so
        // use sites can trust the stored value.
        _forcedWidths[index] = option == CellDiffOption.ForcedWidth ? Math.Max((ushort)1, forcedWidth) : (ushort)0;
        _rowHashValid[y] = false;
        return true;
    }

    /// <summary>
    /// Raw directive mirror for the diff scan (R1 steal): writes without
    /// dirty-marking — the scan owns hash discipline through CloseRow
    /// adopt/invalidate, and per-cell invalidation there would cost a flag
    /// write per mirrored cell. NOT for widget use (use
    /// <see cref="SetDiffOption"/>).
    /// </summary>
    internal void MirrorDiffOption(int x, int y, CellDiffOption option, ushort forcedWidth)
    {
        int index = (y * Cols) + x;
        _diffOptions[index] = option;
        _forcedWidths[index] = forcedWidth;
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

    /// <summary>Copies an authoritative row hash from another buffer (the
    /// diff uses this to keep FRONT's cache in lockstep with BACK).</summary>
    internal void AdoptRowHash(ScreenBuffer source, int y)
    {
        RowHash[y] = source.RowHash[y];
        _rowHashValid[y] = true;
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
            // R1 steal: the directive table is outside Cell, so the hash folds
            // it explicitly — otherwise an option-only change (Skip armed, stale
            // AlwaysUpdate cleared) would keep hash equality and the fast path
            // would hide the behavioral change.
            hash ^= ((ulong)_diffOptions[baseIndex + x] << 16) | _forcedWidths[baseIndex + x];
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
