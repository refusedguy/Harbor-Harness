using System.Text;

namespace Harbor.Tui.ConsoleEx.Widgets.Markdown;

/// <summary>Checkpoint into the frozen prefix (grok streaming.rs).</summary>
public readonly record struct MdCheckpoint(int OutputLines, int SourceChars);

/// <summary>
/// Frozen-tail streaming markdown renderer (widgets §3.2, simplified CE-3
/// dialect): complete blocks freeze into immutable styled lines; the unstable
/// tail (open paragraph/list, open fence, unterminated last line) re-renders
/// on every <see cref="RenderTail"/>. Cost: O(tail) per push, O(N) total.
///
/// Main invariant, pinned by tests: token-by-token pushes produce styled
/// lines identical to a one-shot render of the final document.
///
/// Width changes invalidate frozen geometry (grok set_max_table_width
/// policy): the next render rebuilds everything from source. Wrapping is a
/// greedy hard cut at the width cell — wide-rune safe; word-boundary
/// preference stays in <c>TextWrap</c> for plain-text paths.
/// </summary>
public sealed class StreamingMarkdownRenderer
{
    private readonly StringBuilder _source = new();
    private readonly List<MdLine> _frozenLines = [];
    private readonly List<MdLine> _tailLines = [];
    private int _frozenSourceChars;
    private int _width = -1;
    private bool _complete;

    public int LineCount => _frozenLines.Count + _tailLines.Count;

    public int FrozenLineCount => _frozenLines.Count;

    public MdCheckpoint Checkpoint => new(_frozenLines.Count, _frozenSourceChars);

    public int Width => _width;

    public bool IsComplete => _complete;

    public void Push(ReadOnlySpan<char> chunk)
    {
        if (_complete || chunk.IsEmpty)
        {
            return;
        }

        _source.Append(chunk);
    }

    /// <summary>No more deltas will arrive; trailing partial content becomes final.</summary>
    public void Complete() => _complete = true;

    /// <summary>Combined view for callers that want a plain list (tests/paint).</summary>
    public IReadOnlyList<MdLine> GetLines()
    {
        var all = new List<MdLine>(_frozenLines.Count + _tailLines.Count);
        all.AddRange(_frozenLines);
        all.AddRange(_tailLines);
        return all;
    }

    public MdLine LineAt(int index) =>
        index < _frozenLines.Count ? _frozenLines[index] : _tailLines[index - _frozenLines.Count];

    /// <summary>Freezes newly-complete blocks, re-renders the open tail. True when output may have changed.</summary>
    public bool RenderTail(int width)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);

        bool rebuiltAll = false;
        if (width != _width)
        {
            _width = width;
            _frozenLines.Clear();
            _frozenSourceChars = 0;
            rebuiltAll = true;
        }

        if (_source.Length == _frozenSourceChars)
        {
            if (rebuiltAll)
            {
                RenderFreshTail(string.Empty, 0);
            }

            return rebuiltAll;
        }

        string tail = _source.ToString(_frozenSourceChars, _source.Length - _frozenSourceChars);
        var blocks = MarkdownBlockParser.Parse(tail);

        int frozenInThisTail = 0;
        bool frozeAny = false;
        foreach (var b in blocks)
        {
            if (!b.Freezable)
            {
                break;
            }

            _frozenLines.AddRange(RenderRange(tail, b.Start, b.End, _width));
            if ((b.Kind == MdBlockKind.Paragraph || b.Kind == MdBlockKind.ListItem) && HasBlankTerminator(tail, b.End))
            {
                _frozenLines.Add(MdLine.Empty); // breathing room between blocks
            }

            // b.End values are cumulative within THIS tail — remember the
            // last one and advance the absolute checkpoint once.
            frozenInThisTail = b.End;
            frozeAny = true;
        }

        if (frozeAny)
        {
            _frozenSourceChars += frozenInThisTail;
        }

        // A completed document freezes its trailing region wholesale — the
        // source can never grow again, so the last (possibly unterminated)
        // block becomes immutable too and steady-state renders are free.
        if (_complete && frozenInThisTail < tail.Length)
        {
            _frozenLines.AddRange(RenderRange(tail, frozenInThisTail, tail.Length, _width));
            _frozenSourceChars += tail.Length - frozenInThisTail;
            frozenInThisTail = tail.Length;
        }

        RenderFreshTail(tail, frozenInThisTail);
        return rebuiltAll || frozeAny || _complete;
    }

    private void RenderFreshTail(string tail, int from)
    {
        _tailLines.Clear();
        if (from >= tail.Length)
        {
            return;
        }

        _tailLines.AddRange(RenderRange(tail, from, tail.Length, _width));
    }

    /// <summary>The block ended right before a blank separator line («\n\n» boundary).</summary>
    private static bool HasBlankTerminator(string tail, int blockEnd)
    {
        if (blockEnd < 2 || tail[blockEnd - 1] != '\n')
        {
            return false;
        }

        int i = blockEnd - 2;
        while (i >= 0 && tail[i] != '\n')
        {
            i--;
        }

        return tail.AsSpan(i + 1, blockEnd - 1 - (i + 1)).IsWhiteSpace();
    }

    /// <summary>Renders source[start,end) into wrapped styled display lines.</summary>
    internal static List<MdLine> RenderRange(string sourceText, int start, int end, int width)
    {
        var region = sourceText.AsSpan(start, Math.Clamp(end - start, 0, sourceText.Length - start));
        var lines = new List<MdLine>(4);
        int pos = 0;

        while (pos < region.Length)
        {
            int nl = region.Slice(pos).IndexOf('\n');
            bool terminated = nl >= 0;
            int lineEnd = terminated ? pos + nl : region.Length;
            var raw = region.Slice(pos, lineEnd - pos);
            var trimmed = raw.TrimStart(' ');
            var kind = MarkdownBlockParser.Classify(trimmed);

            switch (kind)
            {
                case LineKind.Blank:
                    break;

                case LineKind.FenceOpen:
                    AddWrapped(lines, [new MdSpan(trimmed.TrimEnd('\r').ToString(), MdStyle.Fence)], width);
                    break;

                case LineKind.Heading:
                    {
                        int level = MarkdownBlockParser.HeadingLevel(trimmed);
                        int textStart = level + (level < trimmed.Length ? 1 : 0);
                        var text = trimmed.Slice(textStart).TrimEnd('\r');
                        AddWrapped(lines, [new MdSpan(text.ToString(), MdStyle.Heading)], width);
                        break;
                    }

                case LineKind.ListItem:
                    {
                        _ = MarkdownBlockParser.IsListItem(trimmed, out int markerWidth);
                        var bodySpans = ScanInline(trimmed.Slice(markerWidth).TrimEnd('\r'));
                        var withBullet = new List<MdSpan>(bodySpans.Count + 1)
                        {
                            new(trimmed.Slice(0, markerWidth).ToString(), MdStyle.Bullet),
                        };
                        withBullet.AddRange(bodySpans);
                        AddWrapped(lines, withBullet, width);
                        break;
                    }

                default:
                    AddWrapped(lines, ScanInline(trimmed.TrimEnd('\r')), width);
                    break;
            }

            pos = terminated ? lineEnd + 1 : region.Length;
        }

        return lines;
    }

    /// <summary>Single-pass inline scanner: **bold**, *italic*, `code`.</summary>
    public static List<MdSpan> ScanInline(ReadOnlySpan<char> line)
    {
        var spans = new List<MdSpan>(4);
        var text = new StringBuilder(line.Length);
        bool bold = false, italic = false, code = false;

        MdStyle Current() => code ? MdStyle.Code
            : bold && italic ? MdStyle.BoldItalic
            : bold ? MdStyle.Bold
            : italic ? MdStyle.Italic
            : MdStyle.Normal;

        void EmitPending()
        {
            if (text.Length == 0)
            {
                return;
            }

            spans.Add(new MdSpan(text.ToString(), Current()));
            text.Clear();
        }

        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == '`')
            {
                EmitPending();
                code = !code;
                i++;
            }
            else if (c == '*' && i + 1 < line.Length && line[i + 1] == '*')
            {
                EmitPending();
                bold = !bold;
                i += 2;
            }
            else if (c == '*')
            {
                EmitPending();
                italic = !italic;
                i++;
            }
            else
            {
                text.Append(c);
                i++;
            }
        }

        EmitPending();

        if (spans.Count == 0)
        {
            spans.Add(new MdSpan(string.Empty, MdStyle.Normal));
        }

        return spans;
    }

    /// <summary>
    /// Greedy hard wrap of styled spans to <paramref name="width"/> cells.
    /// Wide runes never split; zero-width runes attach forward. An empty span
    /// list yields one empty line (keeps blank paragraphs representable).
    /// </summary>
    internal static void AddWrapped(List<MdLine> output, IReadOnlyList<MdSpan> spans, int width)
    {
        if (spans.Count == 0)
        {
            output.Add(MdLine.Empty);
            return;
        }

        var line = new List<MdSpan>(4);
        var work = new StringBuilder(Math.Min(width, 64));
        MdStyle workStyle = MdStyle.Normal;
        bool hasWork = false;
        int cells = 0;

        void FlushLine()
        {
            if (hasWork)
            {
                line.Add(new MdSpan(work.ToString(), workStyle));
                work.Clear();
                hasWork = false;
            }

            if (line.Count > 0)
            {
                output.Add(new MdLine(line));
                line = [];
            }

            cells = 0;
        }

        foreach (var s in spans)
        {
            var rest = s.Text.AsSpan();
            while (!rest.IsEmpty)
            {
                System.Text.Rune.DecodeFromUtf16(rest, out var rune, out int size);
                int rw = Rendering.UnicodeWidth.Width(rune);
                if (cells > 0 && cells + rw > width)
                {
                    FlushLine();
                }

                if (hasWork && workStyle != s.Style)
                {
                    // Style boundary: commit the accumulated run first.
                    line.Add(new MdSpan(work.ToString(), workStyle));
                    work.Clear();
                    hasWork = false;
                }

                if (!hasWork)
                {
                    workStyle = s.Style;
                    hasWork = true;
                }

                work.Append(rest[..size]);
                cells += rw;
                rest = rest[size..];
            }
        }

        FlushLine();
    }
}
