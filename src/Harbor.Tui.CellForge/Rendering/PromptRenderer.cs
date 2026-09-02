namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Draws the composer line(s) into an <see cref="AnsiWriter"/>: visible slice
/// per logical line (horizontal scroll via <see cref="PromptViewport"/>),
/// dim placeholder when empty, and caret parking at the end of the frame
/// (celldiff §3.3 — DECTCEM is not toggled per frame).
/// </summary>
public static class PromptRenderer
{
    public static readonly CellStyle PlaceholderStyle = new(PackedColor.Indexed(243));
    public const char CaretGlyph = '\u2588'; // full block as the visual caret

    /// <summary>Number of screen rows the prompt occupies for current content.</summary>
    public static int MeasureLineCount(in PromptBuffer buffer)
    {
        int lines = 1;
        var text = buffer.SnapshotText();
        foreach (var c in text)
        {
            if (c == '\n')
            {
                lines++;
            }
        }

        return lines;
    }

    /// <summary>
    /// Renders the whole prompt starting at the current pen position (caller
    /// guarantees the region below is clear). Returns the display column of
    /// the caret on its last row.
    /// </summary>
    public static int Render(AnsiWriter writer, in PromptBuffer buffer, int widthCells, string? placeholder = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(widthCells);

        if (buffer.IsEmpty && !string.IsNullOrEmpty(placeholder))
        {
            writer.SetStyle(PlaceholderStyle);
            writer.WriteText(placeholder);
            writer.ResetStyle();
            return 0;
        }

        var text = buffer.SnapshotText();
        int caret = buffer.Cursor;

        // Iterate logical lines with their char ranges.
        int lineStart = 0;
        int caretColumn = 0;
        bool caretPlaced = false;
        while (true)
        {
            int lineEnd = lineStart;
            while (lineEnd < text.Length && text[lineEnd] != '\n')
            {
                lineEnd++;
            }

            bool caretOnThisLine = caret >= lineStart && caret <= lineEnd && !caretPlaced;
            int sliceCaret = Math.Clamp(caret - lineStart, 0, lineEnd - lineStart);

            var viewport = PromptViewport.ScrollIntoView(text.AsSpan(lineStart, lineEnd - lineStart), sliceCaret, widthCells);
            var slice = text.AsSpan(lineStart + viewport.Start, lineEnd - lineStart - viewport.Start);

            writer.WriteText(slice);

            if (caretOnThisLine)
            {
                caretColumn = PromptBuffer.DisplayCells(text.AsSpan(lineStart + viewport.Start, sliceCaret - viewport.Start));
                caretPlaced = true;
            }

            if (lineEnd >= text.Length)
            {
                break;
            }

            writer.WriteLineBreak();
            lineStart = lineEnd + 1;
        }

        return caretColumn;
    }
}
