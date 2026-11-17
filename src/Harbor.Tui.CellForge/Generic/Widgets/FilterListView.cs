using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// Generic rendering for <see cref="FilterList{T}" />: paints ranked rows with
/// fuzzy-matched characters in a highlight style, marks the selected row, and
/// renders the selected item's preview text in a side pane. Like the primitive
/// it renders, it knows nothing about commands, files or any other domain —
/// hosts supply items, text and preview through <see cref="FilterList{T}" />.
/// </summary>
/// <remarks>
/// This is the paint half of the slice; the state/scoring half lives in the
/// BCL-only engine (<c>FilterList&lt;T&gt;</c>). Highlight positions come from
/// the shared <see cref="FuzzyMatcher" />, so there is one definition of "what
/// matched" across the scorer, the state and the view.
/// </remarks>
public static class FilterListView
{
    /// <summary>
    /// Paints <paramref name="list" /> into <paramref name="listRect" /> and its
    /// <see cref="FilterList{T}.Preview" /> into <paramref name="previewRect" />.
    /// Matched characters use an accented + underlined style; the selected row
    /// gets a <c>&gt;</c> marker and a bold style. Both rectangles are clip-
    /// bounded by the buffer; zero-sized rectangles paint nothing.
    /// </summary>
    public static void Paint<T>(FilterList<T> list, ScreenBuffer buffer, Rect listRect, Rect previewRect)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(buffer);

        var rowStyle = ChatPalette.ToolArgs;
        var selectedStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        var matchStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold | StyleAttr.Underline);
        var dimStyle = ChatPalette.Dim;

        int rows = Math.Min(listRect.Height, list.Count);
        int textWidth = Math.Max(0, listRect.Width - 2); // reserve "> " for the marker
        int queryLen = list.Query.Length;
        Span<int> matches = Span<int>.Empty;
        if (queryLen > 0)
        {
            matches = stackalloc int[queryLen];
        }

        for (int r = 0; r < rows; r++)
        {
            int y = listRect.Y + r;
            bool selected = r == list.SelectedIndex;
            buffer.SetText(listRect.X, y, selected ? "> " : "  ", selected ? selectedStyle : dimStyle);
            if (textWidth == 0)
            {
                continue;
            }

            string text = list.TextOfResult(r);
            int matchCount = list.WriteMatchIndices(r, matches);
            var cellStyle = selected ? selectedStyle : rowStyle;
            int mi = 0;
            for (int ci = 0; ci < text.Length && ci < textWidth; ci++)
            {
                bool isMatch = mi < matchCount && matches[mi] == ci;
                if (isMatch)
                {
                    mi++;
                }

                buffer.SetText(listRect.X + 2 + ci, y, text.AsSpan(ci, 1), isMatch ? matchStyle : cellStyle);
            }
        }

        PaintPreview(list.Preview, buffer, previewRect, dimStyle);
    }

    private static void PaintPreview(string preview, ScreenBuffer buffer, Rect rect, CellStyle style)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || string.IsNullOrEmpty(preview))
        {
            return;
        }

        int line = 0;
        int start = 0;
        for (int i = 0; i <= preview.Length && line < rect.Height; i++)
        {
            if (i != preview.Length && preview[i] != '\n')
            {
                continue;
            }

            int len = Math.Min(i - start, rect.Width);
            if (len > 0)
            {
                buffer.SetText(rect.X, rect.Y + line, preview.AsSpan(start, len), style);
            }

            line++;
            start = i + 1;
        }
    }
}
