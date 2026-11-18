// Issue #2375: domain-independent document viewer. A generic document widget
// with a token adapter (markdown / HTML / plain), word wrap and clamped pager
// navigation. Like LogView, it owns no keys, no events and no extension axis:
// the host drives it through methods and paints it into a cell buffer.

using System.Buffers;
using System.Text;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// Generic document viewer: a flat list of wrapped <see cref="DocumentLine"/>s
/// plus a clipped scroll window. Markdown and HTML sources are turned into the
/// neutral token stream by <see cref="DocumentAdapters"/>; callers may also
/// supply their own lines with <see cref="SetLines"/>. Pager moves are clamped
/// through the engine's <see cref="ScrollableViewport"/>, so PageUp/PageDown,
/// Home/End and scroll-by share one bounds formula with the rest of CellForge.
/// </summary>
public sealed class DocumentView
{
    private readonly ScrollableViewport _viewport = new();
    private IReadOnlyList<DocumentLine> _lines = [];
    private string? _source;
    private DocumentFormat _format = DocumentFormat.Markdown;
    private int _width = 80;
    private BracketPairResult _bracket = BracketPairResult.None;

    /// <summary>The current lines (already wrapped to <see cref="Width"/>).</summary>
    public IReadOnlyList<DocumentLine> Lines => _lines;

    /// <summary>Width used for wrap and token adaptation.</summary>
    public int Width => _width;

    /// <summary>Total wrapped rows in the document.</summary>
    public int TotalRows => _lines.Count;

    /// <summary>Top visible row.</summary>
    public long ScrollY => _viewport.Offset;

    /// <summary>True when the viewport shows the first row.</summary>
    public bool AtTop => _viewport.AtTop;

    /// <summary>True when the viewport shows the last window of rows.</summary>
    public bool AtBottom => _viewport.AtBottom;

    /// <summary>The shared clipped viewport. Exposed for scroll-info consumers.</summary>
    public ScrollableViewport Viewport => _viewport;

    /// <summary>Sets a source string and re-adapts it at the current width. Resets to the top.</summary>
    public void SetDocument(string? source, DocumentFormat format = DocumentFormat.Markdown, int? width = null)
    {
        _source = source ?? string.Empty;
        _format = format;
        if (width.HasValue)
        {
            _width = Math.Max(1, width.Value);
        }

        _viewport.ScrollToTop();
        _bracket = BracketPairResult.None;
        Relayout();
    }

    /// <summary>Sets a pre-tokenized line stream directly. No adapter is involved.</summary>
    public void SetLines(IReadOnlyList<DocumentLine> lines)
    {
        _lines = lines ?? [];
        _source = null;
        _viewport.ScrollToTop();
        _bracket = BracketPairResult.None;
        _viewport.SetTotal(_lines.Count);
    }

    /// <summary>
    /// Computes and stores the bracket pair under a document position for the
    /// next <see cref="Paint"/>. The host drives this; it stays internal so the
    /// viewer keeps its public surface unchanged (issue #2494). A position
    /// that is out of range or not on a bracket clears the highlight.
    /// </summary>
    internal void SetBracketCursor(int row, int col)
    {
        if (row < 0 || row >= _lines.Count || col < 0)
        {
            _bracket = BracketPairResult.None;
            return;
        }

        var text = new string[_lines.Count];
        for (int i = 0; i < _lines.Count; i++)
        {
            text[i] = LineText(_lines[i]);
        }

        _bracket = BracketPair.Find(text, row, col);
    }

    private static string LineText(DocumentLine line)
    {
        var spans = line.Spans;
        int length = 0;
        for (int i = 0; i < spans.Count; i++)
        {
            length += spans[i].Text.Length;
        }

        var sb = new StringBuilder(length);
        for (int i = 0; i < spans.Count; i++)
        {
            sb.Append(spans[i].Text);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Sets the viewport height used for pager steps and for the scroll bounds.
    /// A width change re-adapts a source-backed document and preserves the
    /// (clamped) scroll offset; a height change only resizes the window.
    /// </summary>
    public void Configure(int width, int viewportH)
    {
        int w = Math.Max(1, width);
        _viewport.Resize(viewportH);
        if (w == _width)
        {
            return;
        }

        _width = w;
        _bracket = BracketPairResult.None;
        if (_source is not null)
        {
            Relayout(); // SetTotal clamps the offset into the new row count
        }
    }

    /// <summary>Moves the window by a signed number of rows, clamped.</summary>
    public void ScrollBy(int rows) => _viewport.ScrollBy(rows);

    /// <summary>Scrolls up one page, leaving one row of context.</summary>
    public void PageUp(int viewportH) => _viewport.ScrollBy(-PageStep(viewportH));

    /// <summary>Scrolls down one page, leaving one row of context.</summary>
    public void PageDown(int viewportH) => _viewport.ScrollBy(PageStep(viewportH));

    /// <summary>Pins to the first row.</summary>
    public void ScrollToTop() => _viewport.ScrollToTop();

    /// <summary>Pins to the last visible window.</summary>
    public void ScrollToEnd() => _viewport.ScrollToEnd();

    /// <summary>Paints the visible lines into <paramref name="rect"/>, clipping at its right edge.</summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        int first = (int)Math.Min(_viewport.Offset, Math.Max(0, _lines.Count));
        for (int row = 0; row < rect.Height && first + row < _lines.Count; row++)
        {
            PaintLine(buffer, rect, rect.Y + row, _lines[first + row], first + row);
        }
    }

    private void Relayout()
    {
        _lines = _source is null ? _lines : DocumentAdapters.Adapt(_source, _format, _width);
        _viewport.SetTotal(_lines.Count);
    }

    private static int PageStep(int viewportH) => Math.Max(1, Math.Max(0, viewportH) - 1);

    private void PaintLine(ScreenBuffer buffer, Rect rect, int y, DocumentLine line, int lineIndex)
    {
        int openCell = -1;
        int closeCell = -1;
        CellStyle highlight = default;
        if (_bracket.Open.HasValue || _bracket.Close.HasValue)
        {
            openCell = CellOffsetOf(line, lineIndex, _bracket.Open);
            closeCell = CellOffsetOf(line, lineIndex, _bracket.Close);
            if (openCell >= 0 || closeCell >= 0)
            {
                highlight = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
            }
        }

        int cursor = rect.X;
        int end = rect.X + rect.Width;
        var spans = line.Spans;
        for (int s = 0; s < spans.Count && cursor < end; s++)
        {
            var span = spans[s];
            var rest = span.Text.AsSpan();
            while (!rest.IsEmpty && cursor < end)
            {
                if (Rune.DecodeFromUtf16(rest, out var rune, out int consumed) != OperationStatus.Done)
                {
                    rune = Rune.ReplacementChar;
                    consumed = 1;
                }

                int w = UnicodeWidth.Width(rune);
                if (w == 0)
                {
                    rest = rest[consumed..];
                    continue;
                }

                int cell = cursor - rect.X;
                var style = cell == openCell || cell == closeCell ? highlight : span.Style;
                if (cursor + w > end || !buffer.SetRune(cursor, y, rune, style))
                {
                    return;
                }

                cursor += w;
                rest = rest[consumed..];
            }
        }
    }

    /// <summary>
    /// Display-cell offset of a (row, col) bracket inside its line, or -1 when
    /// the position belongs to another line. Wide runes before the bracket are
    /// counted so the highlight lands on the painted cell, not the char index.
    /// </summary>
    private static int CellOffsetOf(DocumentLine line, int lineIndex, (int Row, int Col)? pos)
    {
        if (pos is null || pos.Value.Row != lineIndex)
        {
            return -1;
        }

        string text = LineText(line);
        int col = Math.Min(pos.Value.Col, text.Length);
        return UnicodeWidth.Width(text.AsSpan(0, col));
    }
}
