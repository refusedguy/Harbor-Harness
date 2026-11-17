using Ensure = Harbor.Abstractions.Extensions.Ensure;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// Generic log stream viewer (issue #2323): follow-tail autoscroll, substring
/// filter, optional wrap and bounded scrollback. The clipped row window comes
/// from the engine's <see cref="ScrollableViewport"/> and the resident history
/// from <see cref="LogRing"/>, so the follow and eviction math matches the chat
/// timeline's shape without depending on any chat block type. No key handling,
/// no event subscription and no extension axis: the host drives the primitive
/// through its methods and paints it into a cell buffer.
/// </summary>
public sealed class LogView
{
    private const int DefaultWidth = 80;

    private readonly LogRing _ring;
    private readonly LogRing _matched;
    private readonly ScrollableViewport _viewport = new();
    private int _width = DefaultWidth;
    private string? _filter;
    private bool _caseSensitive;
    private bool _wrap = true;
    private long _rows;

    /// <summary>Creates a viewer with the given scrollback budget and line cap.</summary>
    public LogView(long budgetBytes = LogRing.DefaultBudgetBytes, int maxLines = int.MaxValue)
    {
        _ring = new LogRing(budgetBytes, maxLines, OnRingEvicted);
        _matched = new LogRing(long.MaxValue, int.MaxValue);
    }

    /// <summary>Resident byte budget. Eviction keeps the newest line even if it alone is over budget.</summary>
    public long BudgetBytes => _ring.BudgetBytes;

    /// <summary>
    /// Maximum resident lines. Lowering it trims immediately and keeps the
    /// filtered view and row counter in sync.
    /// </summary>
    public int MaxLines
    {
        get => _ring.MaxLines;
        set
        {
            _ring.MaxLines = Math.Max(0, value);
            SyncAfterChange();
        }
    }

    /// <summary>Whether long lines wrap to the configured width. Changing it re-lays out the view.</summary>
    public bool Wrap
    {
        get => _wrap;
        set
        {
            if (_wrap == value)
            {
                return;
            }

            _wrap = value;
            RebuildRows();
            SyncAfterChange();
        }
    }

    /// <summary>Substring filter applied to line text. Null or empty means show all lines.</summary>
    public string? Filter
    {
        get => _filter;
        set => SetFilter(value);
    }

    /// <summary>Whether the substring filter uses ordinal case-sensitive matching.</summary>
    public bool CaseSensitive
    {
        get => _caseSensitive;
        set
        {
            if (_caseSensitive == value)
            {
                return;
            }

            _caseSensitive = value;
            RebuildMatched();
        }
    }

    /// <summary>True while the view is pinned to the tail. Upward scroll unpins it.</summary>
    public bool FollowTail { get; private set; } = true;

    /// <summary>Width used for wrap and row layout. Set by <see cref="Configure"/>.</summary>
    public int Width => _width;

    /// <summary>The clipped row window shared with the other virtualized widgets.</summary>
    public ScrollableViewport Viewport => _viewport;

    /// <summary>Top visible row in filtered-and-wrapped row space.</summary>
    public long ScrollY => _viewport.Offset;

    /// <summary>Total wrapped rows in the filtered view.</summary>
    public long TotalRows => _rows;

    /// <summary>Resident log lines before filtering.</summary>
    public int Count => _ring.Count;

    /// <summary>Resident lines that pass the filter.</summary>
    public int MatchedCount => _matched.Count;

    /// <summary>Total lines appended since construction, including evicted ones.</summary>
    public long AppendedCount { get; private set; }

    /// <summary>Total lines dropped by scrollback eviction.</summary>
    public long EvictedCount => _ring.EvictedCount;

    /// <summary>True when a non-empty filter is active.</summary>
    public bool IsFiltering => _filter is not null;

    /// <summary>True when the viewport shows the last window of rows.</summary>
    public bool AtBottom => _viewport.AtBottom;

    /// <summary>Appends a line. While following, the viewport snaps to the new tail.</summary>
    public void Append(LogLine line)
    {
        bool matches = Matches(line);
        _ring.Append(line);
        AppendedCount++;
        if (matches)
        {
            _matched.Append(line);
            _rows += LogWrap.RowCount(line.Text, _width, _wrap);
        }

        SyncAfterChange();
    }

    /// <summary>Convenience overload that appends plain text at a severity.</summary>
    public void Append(string text, LogLevel level = LogLevel.Info) => Append(new LogLine(text, level));

    /// <summary>Clears all lines and re-engages follow mode.</summary>
    public void Clear()
    {
        _ring.Clear();
        _matched.Clear();
        _rows = 0;
        AppendedCount = 0;
        FollowTail = true;
        _viewport.SetTotal(0);
        _viewport.ScrollToTop();
    }

    /// <summary>Sets the substring filter and rebuilds the filtered view.</summary>
    public void SetFilter(string? filter)
    {
        _filter = string.IsNullOrEmpty(filter) ? null : filter;
        RebuildMatched();
    }

    /// <summary>
    /// Reports the layout geometry and re-lays out when the width changes.
    /// Call with the same width the paint rect will use.
    /// </summary>
    public void Configure(int width, int viewportH)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(viewportH);

        if (width != _width)
        {
            _width = width;
            RebuildRows();
        }

        _viewport.Resize(viewportH);
        SyncAfterChange();
    }

    /// <summary>
    /// Moves by a signed number of rows. Upward movement unpins follow;
    /// reaching the bottom again re-engages it.
    /// </summary>
    public void ScrollBy(long rows)
    {
        if (rows < 0)
        {
            FollowTail = false;
        }

        _viewport.ScrollBy(rows);
        if (rows > 0 && _viewport.AtBottom)
        {
            FollowTail = true;
        }
    }

    /// <summary>Scrolls up one page without changing the viewport height.</summary>
    public void PageUp(int viewportH) => ScrollBy(-Math.Max(1, viewportH - 1));

    /// <summary>Scrolls down one page without changing the viewport height.</summary>
    public void PageDown(int viewportH) => ScrollBy(Math.Max(1, viewportH - 1));

    /// <summary>Pins to the first row and disengages follow.</summary>
    public void ScrollToTop()
    {
        FollowTail = false;
        _viewport.ScrollToTop();
    }

    /// <summary>Pins to the last visible window and re-engages follow.</summary>
    public void ScrollToEnd() => SetFollow(true);

    /// <summary>Sets follow mode, snapping to the tail when engaging it.</summary>
    public void SetFollow(bool follow)
    {
        FollowTail = follow;
        if (follow)
        {
            _viewport.ScrollToEnd();
        }
    }

    /// <summary>
    /// Wrapped text rows currently in the viewport. Pure and allocation-light:
    /// one string per visible row, no snapshot of the whole history.
    /// </summary>
    public IReadOnlyList<string> VisibleRows()
    {
        var (first, last) = _viewport.VisibleSlice();
        if (last <= first)
        {
            return Array.Empty<string>();
        }

        var rows = new List<string>();
        long cursor = 0;
        for (int i = 0; i < _matched.Count && cursor < last; i++)
        {
            var line = _matched[i];
            int count = LogWrap.RowCount(line.Text, _width, _wrap);
            long end = cursor + count;
            if (end > first)
            {
                int from = (int)Math.Max(0, first - cursor);
                int to = (int)Math.Min(count, last - cursor);
                for (int r = from; r < to; r++)
                {
                    rows.Add(LogWrap.Segment(line.Text, _width, _wrap, r).ToString());
                }
            }

            cursor = end;
        }

        return rows.ToArray();
    }

    /// <summary>
    /// Paints only the visible wrapped rows into <paramref name="rect"/>.
    /// Cells outside the rect are never touched.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        Ensure.NotNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0 || _matched.Count == 0)
        {
            return;
        }

        var (first, last) = _viewport.VisibleSlice();
        if (last <= first)
        {
            return;
        }

        int y = rect.Y;
        int bottom = rect.Y + rect.Height;
        long cursor = 0;
        for (int i = 0; i < _matched.Count && cursor < last && y < bottom; i++)
        {
            var line = _matched[i];
            int count = LogWrap.RowCount(line.Text, _width, _wrap);
            long end = cursor + count;
            if (end > first)
            {
                int from = (int)Math.Max(0, first - cursor);
                int to = (int)Math.Min(count, last - cursor);
                var style = StyleFor(line.Level);
                for (int r = from; r < to && y < bottom; r++)
                {
                    var segment = LogWrap.Segment(line.Text, _width, _wrap, r);
                    buffer.SetText(rect.X, y, LogWrap.Fit(segment, rect.Width), style);
                    y++;
                }
            }

            cursor = end;
        }
    }

    private static CellStyle StyleFor(LogLevel level) => level switch
    {
        LogLevel.Error or LogLevel.Fatal => new CellStyle(ChatPalette.Error, attrs: StyleAttr.Bold),
        LogLevel.Warn => new CellStyle(ChatPalette.Warning),
        LogLevel.Debug or LogLevel.Trace => new CellStyle(ChatPalette.Muted),
        _ => new CellStyle(ChatPalette.Text),
    };

    private bool Matches(LogLine line) =>
        _filter is null ||
        line.Text.Contains(_filter, _caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private void OnRingEvicted(LogLine evicted)
    {
        if (!Matches(evicted))
        {
            return;
        }

        if (_matched.Count > 0)
        {
            _matched.RemoveOldest();
        }

        _rows = Math.Max(0, _rows - LogWrap.RowCount(evicted.Text, _width, _wrap));
    }

    private void RebuildRows()
    {
        _rows = 0;
        for (int i = 0; i < _matched.Count; i++)
        {
            _rows += LogWrap.RowCount(_matched[i].Text, _width, _wrap);
        }
    }

    private void RebuildMatched()
    {
        _matched.Clear();
        _rows = 0;
        for (int i = 0; i < _ring.Count; i++)
        {
            var line = _ring[i];
            if (Matches(line))
            {
                _matched.Append(line);
                _rows += LogWrap.RowCount(line.Text, _width, _wrap);
            }
        }

        SyncAfterChange();
    }

    private void SyncAfterChange()
    {
        _viewport.SetTotal(_rows);
        if (FollowTail)
        {
            _viewport.ScrollToEnd();
        }
    }
}
