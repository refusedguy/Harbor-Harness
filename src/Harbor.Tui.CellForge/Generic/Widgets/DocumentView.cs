// Issue #2375: domain-independent document viewer. A generic document widget
// with a token adapter (markdown / HTML / plain), word wrap and clamped pager
// navigation. Like LogView, it owns no keys, no events and no extension axis:
// the host drives it through methods and paints it into a cell buffer.

using System.Buffers;
using System.Text;
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
        Relayout();
    }

    /// <summary>Sets a pre-tokenized line stream directly. No adapter is involved.</summary>
    public void SetLines(IReadOnlyList<DocumentLine> lines)
    {
        _lines = lines ?? [];
        _source = null;
        _viewport.ScrollToTop();
        _viewport.SetTotal(_lines.Count);
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
            PaintLine(buffer, rect, rect.Y + row, _lines[first + row]);
        }
    }

    private void Relayout()
    {
        _lines = _source is null ? _lines : DocumentAdapters.Adapt(_source, _format, _width);
        _viewport.SetTotal(_lines.Count);
    }

    private static int PageStep(int viewportH) => Math.Max(1, Math.Max(0, viewportH) - 1);

    private static void PaintLine(ScreenBuffer buffer, Rect rect, int y, DocumentLine line)
    {
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

                if (cursor + w > end || !buffer.SetRune(cursor, y, rune, span.Style))
                {
                    return;
                }

                cursor += w;
                rest = rest[consumed..];
            }
        }
    }
}
