// Issue #2217: OSC 8 hyperlinks ride in a side-table NEXT TO the buffer, the
// same shape as CellDiffOption (CellDiffOption.cs:9-13). A link never occupies
// a Cell field, so the 16-byte layout and its five-compare equality stay as
// they are and the fused full-scan diff keeps its byte contract.

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Per-cell OSC 8 hyperlink side-table. Owns one link id per cell index plus
/// an interner for the URLs, so N cells sharing a target store N integers and
/// one string. The engine's <see cref="DiffEngine.Flush"/> reads it to wrap a
/// changed cell's visible rune in an OSC 8 open/close pair; the escape bytes
/// are zero-width, so a link takes no column and never touches the grid.
///
/// <para><see cref="Enabled"/> is the capability switch: when the terminal
/// probe did not confirm OSC 8, <see cref="UrlAt"/> reports no link and the
/// emitter writes only the visible text.</para>
///
/// <para>Render-thread only, like the diff itself. Not thread-safe.</para>
/// </summary>
public sealed class HyperlinkTable
{
    private int[] _links;
    private int _cols;
    private int _rows;
    private readonly List<string> _urls = new() { string.Empty }; // id 0 = no link
    private readonly Dictionary<string, int> _interned = new(StringComparer.Ordinal);

    /// <summary>Creates a table covering a grid of <paramref name="cols"/> × <paramref name="rows"/>.</summary>
    public HyperlinkTable(int cols, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cols);
        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        _cols = cols;
        _rows = rows;
        _links = new int[cols * rows];
    }

    /// <summary>Grid columns this table covers.</summary>
    public int Cols => _cols;

    /// <summary>Grid rows this table covers.</summary>
    public int Rows => _rows;

    /// <summary>
    /// Capability switch. Defaults to <see langword="false"/>, so a session
    /// that never probes emits no OSC bytes even if links were set.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Number of distinct URLs interned so far (diagnostics/tests).</summary>
    public int UrlCount => _urls.Count - 1;

    /// <summary>
    /// Attaches <paramref name="url"/> to <c>(x, y)</c>. An empty URL clears
    /// the cell. Sticky across paints, like <see cref="ScreenBuffer.SetDiffOption"/>:
    /// a repaint keeps the link until this method or <see cref="ClearAll"/> runs.
    /// Returns false when the coordinate is outside the grid.
    /// </summary>
    public bool Set(int x, int y, string url)
    {
        if ((uint)x >= (uint)_cols || (uint)y >= (uint)_rows)
        {
            return false;
        }

        _links[(y * _cols) + x] = string.IsNullOrEmpty(url) ? 0 : Intern(url);
        return true;
    }

    /// <summary>Removes the link at <c>(x, y)</c>, if any.</summary>
    public bool Clear(int x, int y) => Set(x, y, string.Empty);

    /// <summary>Removes every link. The URL interner is kept for reuse.</summary>
    public void ClearAll() => Array.Clear(_links, 0, _links.Length);

    /// <summary>
    /// Resizes the table to a new grid. Shrinking reuses the array; growing
    /// reallocates. Geometry changes reflow the terminal, so all links drop.
    /// </summary>
    public void Resize(int cols, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cols);
        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        int needed = cols * rows;
        if (needed > _links.Length)
        {
            _links = new int[needed];
        }

        _cols = cols;
        _rows = rows;
        ClearAll();
    }

    /// <summary>
    /// Link id at <c>(x, y)</c>, or 0 for none. Out-of-range reads return 0.
    /// </summary>
    public int LinkIdAt(int x, int y)
    {
        if ((uint)x >= (uint)_cols || (uint)y >= (uint)_rows)
        {
            return 0;
        }

        return _links[(y * _cols) + x];
    }

    /// <summary>
    /// URL to wrap around <c>(x, y)</c>, or <see langword="null"/> when the
    /// capability is off or the cell carries no link. This is the one read the
    /// emitter makes.
    /// </summary>
    public string? UrlAt(int x, int y)
    {
        if (!Enabled)
        {
            return null;
        }

        int id = LinkIdAt(x, y);
        return id == 0 ? null : _urls[id];
    }

    private int Intern(string url)
    {
        if (_interned.TryGetValue(url, out int id))
        {
            return id;
        }

        id = _urls.Count;
        _urls.Add(url);
        _interned.Add(url, id);
        return id;
    }
}
