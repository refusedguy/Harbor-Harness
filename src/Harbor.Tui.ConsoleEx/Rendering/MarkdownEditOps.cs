namespace Harbor.Tui.ConsoleEx.Rendering;

/// <summary>
///     Terminal-native markdown editing helpers for the inline composer. Ops work
///     through the public <see cref="PromptBuffer" /> surface (caret moves +
///     one-shot range removal), mirror readline whitespace semantics, and stay
///     honest about MVP nesting: wrapping an already half-marked word simply adds
///     another marker layer instead of resolving overlaps.
/// </summary>
public static class MarkdownEditOps
{
    /// <summary>
    ///     Wraps the whitespace-delimited run under/next to the caret with
    ///     <paramref name="marker" /> (e.g. <c>**</c>). A run already framed by
    ///     the exact marker pair on both ends unwraps symmetrically; an empty or
    ///     whitespace-only buffer grows a bare marker pair with the caret parked
    ///     inside for immediate typing.
    /// </summary>
    public static EditOutcome ToggleWrap(PromptBuffer buffer, string marker)
    {
        string text = buffer.SnapshotText();
        if (!TryLocateRun(text, buffer.Cursor, out int start, out int end))
        {
            _ = buffer.InsertText(marker + marker);
            return buffer.MoveTo(buffer.Cursor - marker.Length);
        }

        int ml = marker.Length;
        bool framed = end - start > 2 * ml &&
                      SliceEquals(text, start, ml, marker) &&
                      SliceEquals(text, end - ml, ml, marker);
        if (framed)
        {
            _ = buffer.RemoveRange(end - ml, ml);
            _ = buffer.RemoveRange(start, ml);
            return buffer.MoveTo(start);
        }

        _ = buffer.MoveTo(end);
        _ = buffer.InsertText(marker);
        _ = buffer.MoveTo(start);
        _ = buffer.InsertText(marker);
        return new EditOutcome(EditOutcomeKind.TextAndCursor, start, buffer.Cursor);
    }

    /// <summary>
    ///     ATX heading toggle on the caret line: plain lines gain <c># </c>,
    ///     <c>#+ </c>-prefixed ones lose the whole run (any level collapses).
    /// </summary>
    public static EditOutcome ToggleHeading(PromptBuffer buffer)
    {
        return ToggleLinePrefix(buffer, "# ", stripHashRunToo: true);
    }

    /// <summary>Unordered list toggle on the caret line (<c>- </c> on/off).</summary>
    public static EditOutcome ToggleListItem(PromptBuffer buffer)
    {
        return ToggleLinePrefix(buffer, "- ", stripHashRunToo: false);
    }

    private static EditOutcome ToggleLinePrefix(PromptBuffer buffer, string prefix, bool stripHashRunToo)
    {
        int lineStart = buffer.LineStartOf(buffer.Cursor);
        string text = buffer.SnapshotText();
        if (HasPrefix(text, lineStart, prefix))
        {
            _ = buffer.RemoveRange(lineStart, prefix.Length);
            return new EditOutcome(EditOutcomeKind.TextAndCursor, lineStart, lineStart);
        }

        if (stripHashRunToo && HashRunLength(text, lineStart) > 0)
        {
            int cut = HashRunLength(text, lineStart) + 1; // run plus one separator space
            _ = buffer.RemoveRange(lineStart, cut);
            return new EditOutcome(EditOutcomeKind.TextAndCursor, lineStart, lineStart);
        }

        _ = buffer.MoveTo(lineStart);
        return buffer.InsertText(prefix);
    }

    private static bool HasPrefix(string text, int lineStart, string prefix)
    {
        return text.Length - lineStart >= prefix.Length &&
               text.AsSpan(lineStart, prefix.Length).SequenceEqual(prefix.AsSpan());
    }

    /// <summary>Leading <c>#</c> count at the caret line (0 when the first non-hash char is not a space).</summary>
    private static int HashRunLength(string text, int lineStart)
    {
        int hashes = 0;
        while (lineStart + hashes < text.Length && text[lineStart + hashes] == '#')
        {
            hashes++;
        }

        if (hashes == 0 || hashes > 6 ||
            (lineStart + hashes < text.Length && text[lineStart + hashes] != ' '))
        {
            return 0;
        }

        return hashes;
    }

    /// <summary>
    ///     Nearest contiguous non-whitespace run: the one containing the caret,
    ///     else the next one after the gap, else (caret past the last run) the
    ///     preceding one before trailing whitespace. Markers glue to words, so
    ///     framing checks operate on this run rather than an inner word.
    /// </summary>
    private static bool TryLocateRun(string text, int cur, out int start, out int end)
    {
        start = end = -1;
        int len = text.Length;
        if (len == 0)
        {
            return false;
        }

        if (cur < len && !char.IsWhiteSpace(text[cur]))
        {
            start = ExpandBack(text, cur);
            end = ExpandForward(text, cur + 1);
            return true;
        }

        int i = Math.Min(Math.Max(cur, 0), len);
        while (i < len && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        if (i < len)
        {
            start = ExpandBack(text, i);
            end = ExpandForward(text, i + 1);
            return true;
        }

        int j = Math.Min(Math.Max(cur, 0), len);
        while (j > 0 && char.IsWhiteSpace(text[j - 1]))
        {
            j--;
        }

        if (j == 0)
        {
            return false;
        }

        end = j;
        start = ExpandBack(text, j);
        return true;
    }

    private static int ExpandBack(string text, int from)
    {
        int s = from;
        while (s > 0 && !char.IsWhiteSpace(text[s - 1]))
        {
            s--;
        }

        return s;
    }

    private static int ExpandForward(string text, int from)
    {
        int e = from;
        while (e < text.Length && !char.IsWhiteSpace(text[e]))
        {
            e++;
        }

        return e;
    }

    private static bool SliceEquals(string text, int start, int length, string expected)
    {
        return start >= 0 &&
               start + length <= text.Length &&
               text.AsSpan(start, length).SequenceEqual(expected.AsSpan());
    }
}
