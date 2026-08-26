namespace Harbor.Tui.ConsoleEx.Widgets.Markdown;

internal enum MdBlockKind : byte
{
    Paragraph,
    Heading,
    Fence,
    ListItem,
}

/// <summary>
/// One parsed markdown block spanning source chars [Start, End). Only
/// complete, newline-terminated blocks are freezable; a trailing partial
/// region always stays in the re-rendered tail.
/// </summary>
internal readonly struct MdBlock(MdBlockKind kind, int start, int end, bool complete, int level)
{
    public MdBlockKind Kind { get; } = kind;
    public int Start { get; } = start;
    public int End { get; } = end;
    public bool Complete { get; } = complete;

    /// <summary>Heading level 1..6, or list marker width.</summary>
    public int Level { get; } = level;

    public bool Freezable => Complete;
}

internal enum LineKind : byte
{
    Blank,
    Text,
    Heading,
    FenceOpen,
    ListItem,
}

/// <summary>
/// Context-free line-oriented parser over the simplified CE-3 dialect:
/// fenced code blocks, ATX headings, «- »/«1. » lists and blank-line
/// separated paragraphs. A paragraph/list run also terminates cleanly when a
/// new block type starts (heading/fence/list) so mid-document freezes never
/// swallow later structure. Pure function of the input text.
/// </summary>
internal static class MarkdownBlockParser
{
    public static List<MdBlock> Parse(ReadOnlySpan<char> source)
    {
        var blocks = new List<MdBlock>(8);
        int pos = 0;

        while (pos < source.Length)
        {
            var (lineEnd, terminated) = LineBounds(source, pos);
            var trimmed = source.Slice(pos, lineEnd - pos).TrimStart(' ');
            var kind = Classify(trimmed);

            switch (kind)
            {
                case LineKind.Blank:
                    // Blank between blocks: skip entirely (paragraph spacing is implicit).
                    pos = terminated ? lineEnd + 1 : source.Length;
                    continue;

                case LineKind.FenceOpen:
                    {
                        int closePos = FindFenceClose(source, lineEnd);
                        if (closePos >= 0)
                        {
                            var (closeEnd, closeTerm) = LineBounds(source, closePos);
                            _ = closeTerm;
                            blocks.Add(new MdBlock(MdBlockKind.Fence, pos, closeEnd, true, 0));
                            pos = closeEnd < source.Length ? closeEnd + 1 : source.Length;
                        }
                        else
                        {
                            blocks.Add(new MdBlock(MdBlockKind.Fence, pos, source.Length, false, 0));
                            return blocks;
                        }

                        break;
                    }

                case LineKind.Heading:
                    {
                        int end = terminated ? lineEnd + 1 : source.Length;
                        blocks.Add(new MdBlock(MdBlockKind.Heading, pos, end, terminated, HeadingLevel(trimmed)));
                        pos = end;
                        break;
                    }

                case LineKind.ListItem:
                    {
                        int end = pos;
                        bool complete = false;
                        int cursor = pos;
                        while (cursor < source.Length)
                        {
                            var (le, term) = LineBounds(source, cursor);
                            var t = source.Slice(cursor, le - cursor).TrimStart(' ');
                            var k = Classify(t);

                            if (k == LineKind.ListItem)
                            {
                                end = term ? le + 1 : le;
                                cursor = term ? le + 1 : source.Length;
                                continue;
                            }

                            // Blank line or a foreign block start terminates the
                            // run; the blank is consumed (symmetric with paragraphs)
                            // so freeze-time spacer detection sees it.
                            complete = true;
                            if (k == LineKind.Blank)
                            {
                                end = term ? le + 1 : le;
                            }

                            break;
                        }

                        // Only an explicit terminator (blank line / foreign
                        // start) completes a list run; EOF-with-newline must
                        // not — a later chunk may still add its separator.
                        blocks.Add(new MdBlock(MdBlockKind.ListItem, pos, end, complete, 2));
                        pos = end;
                        break;
                    }

                case LineKind.Text:
                default:
                    {
                        int end = pos;
                        bool complete = false;
                        int cursor = pos;
                        while (cursor < source.Length)
                        {
                            var (le, term) = LineBounds(source, cursor);
                            var t = source.Slice(cursor, le - cursor).TrimStart(' ');
                            var k = Classify(t);

                            if (k == LineKind.Blank)
                            {
                                end = term ? le + 1 : le; // blank consumed as terminator
                                complete = true;
                                break;
                            }

                            if (k != LineKind.Text)
                            {
                                end = cursor; // foreign block starts here — exclude
                                complete = true;
                                break;
                            }

                            end = term ? le + 1 : le;
                            cursor = term ? le + 1 : source.Length;
                        }

                        // Same strictness as lists: only blank-line/foreign
                        // terminators complete a paragraph.
                        blocks.Add(new MdBlock(MdBlockKind.Paragraph, pos, end, complete, 0));
                        pos = Math.Max(end, pos + 1);
                        break;
                    }
            }
        }

        return blocks;
    }

    private static (int LineEnd, bool Terminated) LineBounds(ReadOnlySpan<char> source, int start)
    {
        int nl = source.Slice(start).IndexOf('\n');
        return nl >= 0 ? (start + nl, true) : (source.Length, false);
    }

    public static LineKind Classify(ReadOnlySpan<char> trimmedLine)
    {
        if (trimmedLine.IsWhiteSpace() || trimmedLine.IsEmpty)
        {
            return LineKind.Blank;
        }

        // Any «»»-prefixed line opens (or closes) a fence; at top level we
        // always enter fence-seeking mode from it.
        if (trimmedLine.StartsWith("```"))
        {
            return LineKind.FenceOpen;
        }

        if (HeadingLevel(trimmedLine) > 0)
        {
            return LineKind.Heading;
        }

        if (IsListItem(trimmedLine, out _))
        {
            return LineKind.ListItem;
        }

        return LineKind.Text;
    }

    /// <summary>Position of the line starting the closing fence, or -1 when open at EOF.</summary>
    private static int FindFenceClose(ReadOnlySpan<char> source, int searchFrom)
    {
        int cursor = searchFrom;
        while (cursor < source.Length)
        {
            var (le, term) = LineBounds(source, cursor);
            var t = source.Slice(cursor, le - cursor).TrimStart(' ');
            if (t.StartsWith("```"))
            {
                return cursor;
            }

            cursor = term ? le + 1 : source.Length;
        }

        return -1;
    }

    public static int HeadingLevel(ReadOnlySpan<char> trimmedLine)
    {
        int i = 0;
        while (i < trimmedLine.Length && trimmedLine[i] == '#')
        {
            i++;
        }

        if (i is < 1 or > 6)
        {
            return 0;
        }

        return i < trimmedLine.Length && trimmedLine[i] == ' ' ? i : 0;
    }

    public static bool IsListItem(ReadOnlySpan<char> trimmedLine, out int markerWidth)
    {
        markerWidth = 0;
        if (trimmedLine.StartsWith("- "))
        {
            markerWidth = 2;
            return true;
        }

        int digits = 0;
        while (digits < trimmedLine.Length && trimmedLine[digits] is >= '0' and <= '9')
        {
            digits++;
        }

        if (digits > 0 && digits + 1 < trimmedLine.Length &&
            trimmedLine[digits] == '.' && trimmedLine[digits + 1] == ' ')
        {
            markerWidth = digits + 2;
            return true;
        }

        return false;
    }
}
