namespace Harbor.Ui.Framework.Rendering.Markdown;

internal enum MdBlockKind : byte
{
    Paragraph,
    Heading,
    Fence,
    ListItem,

    /// <summary>GFM pipe-table run (| header | + | --- | + rows).</summary>
    Table,

    /// <summary>Display-math run delimited by «$$» lines (fence-like).</summary>
    Math,
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

    /// <summary>«| cell |» candidate row of a GFM pipe table.</summary>
    TableRow,

    /// <summary>«$$» display-math delimiter (opens or closes).</summary>
    MathFence,
}

/// <summary>
/// Context-free line-oriented parser over the simplified CE-3 dialect:
/// fenced code blocks, ATX headings, «- »/«1. » lists, GFM pipe tables,
/// «$$» display math and blank-line separated paragraphs. A paragraph/list
/// run also terminates cleanly when a new block type starts (heading/fence/
/// list/table/math) so mid-document freezes never swallow later structure.
/// A «|»-led run freezes as plain text rows once terminated by a blank line
/// or a foreign block; at EOF it stays open so the holdback buffer keeps it
/// until the structure settles. Pure function of the input text.
/// </summary>
internal static class MarkdownBlockParser
{
    public static List<MdBlock> Parse(ReadOnlySpan<char> source)
    {
        var blocks = new List<MdBlock>(8);
        ParseInto(source, blocks);
        return blocks;
    }

    /// <summary>
    /// Allocation-free <see cref="Parse"/> for hot paths: fills and clears a
    /// caller-owned list so a per-frame parse costs no garbage (#463).
    /// </summary>
    public static void ParseInto(ReadOnlySpan<char> source, List<MdBlock> blocks)
    {
        blocks.Clear();
        ParseCore(source, blocks);
    }

    private static void ParseCore(ReadOnlySpan<char> source, List<MdBlock> blocks)
    {
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
                        int closePos = FindFenceClose(source, lineEnd, "```");
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
                            return;
                        }

                        break;
                    }

                case LineKind.MathFence:
                    {
                        int closePos = FindFenceClose(source, lineEnd, "$$");
                        if (closePos >= 0)
                        {
                            var (closeEnd, closeTerm) = LineBounds(source, closePos);
                            _ = closeTerm;
                            blocks.Add(new MdBlock(MdBlockKind.Math, pos, closeEnd, true, 0));
                            pos = closeEnd < source.Length ? closeEnd + 1 : source.Length;
                        }
                        else
                        {
                            blocks.Add(new MdBlock(MdBlockKind.Math, pos, source.Length, false, 0));
                            return;
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

                case LineKind.TableRow:
                    {
                        // ENG11 #283: gather the consecutive pipe-row run. A
                        // blank/foreign terminator closes it (frozen as plain
                        // text rows — the run can never grow again), while EOF
                        // stays incomplete so the holdback buffer keeps it
                        // until the structure settles (separator or more rows
                        // may still stream in).
                        int end = pos;
                        bool closed = false;
                        int cursor = pos;
                        while (cursor < source.Length)
                        {
                            var (le, term) = LineBounds(source, cursor);
                            var t = source.Slice(cursor, le - cursor).TrimStart(' ');
                            var k = Classify(t);

                            if (k != LineKind.TableRow)
                            {
                                closed = true;
                                end = k == LineKind.Blank
                                    ? (term ? le + 1 : le) // blank consumed as terminator
                                    : cursor; // foreign block starts here — exclude
                                break;
                            }

                            end = term ? le + 1 : le;
                            if (!term)
                            {
                                break; // EOF mid-line: structurally uncertain
                            }

                            cursor = le + 1;
                        }

                        // EOF-with-newline still leaves the run open: the next
                        // chunk may append rows (same strictness as lists).
                        blocks.Add(new MdBlock(MdBlockKind.Table, pos, end, closed, 0));
                        pos = Math.Max(end, pos + 1);
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

        // Any «```»-prefixed line opens (or closes) a fence; at top level we
        // always enter fence-seeking mode from it.
        if (trimmedLine.StartsWith("```"))
        {
            return LineKind.FenceOpen;
        }

        // «$$» display-math delimiter (ENG11 #283: fence-like holdback).
        if (trimmedLine.StartsWith("$$"))
        {
            return LineKind.MathFence;
        }

        if (HeadingLevel(trimmedLine) > 0)
        {
            return LineKind.Heading;
        }

        if (IsListItem(trimmedLine, out _))
        {
            return LineKind.ListItem;
        }

        // «| cell |» pipe-row candidate (ENG11 #283: table holdback).
        if (trimmedLine.StartsWith('|'))
        {
            return LineKind.TableRow;
        }

        return LineKind.Text;
    }

    /// <summary>Position of the line starting the closing marker, or -1 when open at EOF.</summary>
    private static int FindFenceClose(ReadOnlySpan<char> source, int searchFrom, string marker)
    {
        int cursor = searchFrom;
        while (cursor < source.Length)
        {
            var (le, term) = LineBounds(source, cursor);
            var t = source.Slice(cursor, le - cursor).TrimStart(' ');
            if (t.StartsWith(marker))
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
