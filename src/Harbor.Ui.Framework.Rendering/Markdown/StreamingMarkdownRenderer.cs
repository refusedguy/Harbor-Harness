using System.Text;

namespace Harbor.Ui.Framework.Rendering.Markdown;

/// <summary>Checkpoint into the frozen prefix (grok streaming.rs).</summary>
public readonly record struct MdCheckpoint(int OutputLines, int SourceChars);

/// <summary>
/// Frozen-tail streaming markdown renderer (widgets §3.2, simplified CE-3
/// dialect): complete blocks freeze into immutable styled lines; the unstable
/// tail (open paragraph/list, open fence, unterminated last line) re-renders
/// on every <see cref="RenderTail"/>.
///
/// Cost is O(chars pushed since the last render), not O(document) — #463.
/// Three things buy that, and all three are load-bearing:
/// <list type="bullet">
/// <item>the source is a growable <c>char[]</c>, so the tail is handed to the
/// parser/renderer as a span instead of being copied into a fresh string
/// every call;</item>
/// <item>the painted tail keeps its newline-terminated prefix and re-renders
/// only the trailing partial line, so a long open paragraph costs its delta
/// rather than its whole length;</item>
/// <item>a repeat call at an unchanged (width, source, completion) triple
/// returns immediately — that is the Measure-then-Paint pair every single
/// frame issues.</item>
/// </list>
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
    /// <summary>
    /// Append-only source. A <see cref="StringBuilder"/> would force every
    /// render to materialise the tail as a new string; a <c>char[]</c> keeps
    /// the whole pipeline span-based (#463).
    /// </summary>
    private char[] _source = new char[256];

    private int _sourceLen;

    private readonly List<MdLine> _frozenLines = [];
    private readonly List<MdLine> _tailLines = [];

    /// <summary>Per-renderer scratch reused by every render — never escapes.</summary>
    private readonly MdScratch _scratch = new();

    /// <summary>Reused block-parse buffer — one per renderer, cleared per parse.</summary>
    private readonly List<MdBlock> _blocksScratch = new(8);

    /// <summary>Source offset the cached tail prefix starts at.</summary>
    private int _tailBase = -1;

    /// <summary>
    /// Source offset up to which <see cref="_tailLines"/> is final: every line
    /// in [_tailBase, _tailStableUpTo) is newline-terminated and can never be
    /// invalidated by a later push.
    /// </summary>
    private int _tailStableUpTo;

    /// <summary>How many of <see cref="_tailLines"/> come from the stable prefix.</summary>
    private int _tailStableLines;

    /// <summary>Repeat-render memo — see the class remarks.</summary>
    private int _lastWidth = -1;
    private int _lastSourceLen = -1;
    private bool _lastComplete;

    private int _frozenSourceChars;
    private int _lastPaintedChars = -1;
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

        int required = _sourceLen + chunk.Length;
        if (required > _source.Length)
        {
            int capacity = _source.Length;
            while (capacity < required)
            {
                capacity *= 2;
            }

            Array.Resize(ref _source, capacity);
        }

        chunk.CopyTo(_source.AsSpan(_sourceLen));
        _sourceLen = required;
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

    /// <summary>
    /// Freezes newly-complete blocks, re-renders the open tail. True when the
    /// painted output may have changed. ENG11 #283: deltas confined to a
    /// held-back structural region (open fence / growing table / unclosed
    /// math) report false — the painted prefix is byte-identical, so the
    /// frame loop can skip the repaint entirely.
    /// </summary>
    public bool RenderTail(int width)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);

        // Nothing to do: same width, same source, same completion state. A
        // frame issues this twice (Measure then Paint) — the second call must
        // be free (#463).
        if (width == _lastWidth && _sourceLen == _lastSourceLen && _complete == _lastComplete)
        {
            return false;
        }

        // Snapshot: a Push on the event thread mid-render must not be able to
        // make this call memoize a length it did not actually render (that
        // would let the next frame skip a needed repaint).
        int sourceLen = _sourceLen;
        var source = _source.AsSpan(0, sourceLen);

        bool rebuiltAll = false;
        if (width != _width)
        {
            _width = width;
            _frozenLines.Clear();
            _frozenSourceChars = 0;
            _lastPaintedChars = -1;
            InvalidateTailPrefix();
            rebuiltAll = true;
        }

        if (sourceLen == _frozenSourceChars)
        {
            if (rebuiltAll)
            {
                RenderFreshTail(source, _frozenSourceChars, _frozenSourceChars);
            }

            Memoize(sourceLen);
            return rebuiltAll;
        }

        int tailBase = _frozenSourceChars;
        var tail = source.Slice(tailBase);

        // TODO(principles)[PERF, low]: this scan is O(lines in the open block)
        // per frame, so a single huge paragraph is still O(N) CPU (now
        // allocation-free) rather than O(delta). It cannot be cached across
        // frames without making the parser resumable: a trailing unterminated
        // line can change its classification as more text arrives («`» is
        // Text, «```» is a FenceOpen), so any resume point would have to
        // re-scan that line anyway. Measured as noise against the allocation
        // win; revisit only with a profiler-backed profile above the frame
        // budget.
        MarkdownBlockParser.ParseInto(tail, _blocksScratch);

        int frozenInThisTail = 0;
        bool frozeAny = false;
        for (int i = 0; i < _blocksScratch.Count; i++)
        {
            var b = _blocksScratch[i];
            if (!b.Freezable)
            {
                break;
            }

            RenderRangeInto(_frozenLines, tail, b.Start, b.End, _width, _scratch);
            // Math/fence closers never absorb the separator blank (unlike
            // paragraphs), so Math also peeks at the following line — keeps
            // final output identical to the old paragraph treatment.
            if ((b.Kind is MdBlockKind.Paragraph or MdBlockKind.ListItem or MdBlockKind.Table or MdBlockKind.Math)
                && (HasBlankTerminator(tail, b.End) || (b.Kind == MdBlockKind.Math && StartsBlankLine(tail, b.End))))
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
            _frozenSourceChars = tailBase + frozenInThisTail;
        }

        // A completed document freezes its trailing region wholesale — the
        // source can never grow again, so the last (possibly unterminated)
        // block becomes immutable too and steady-state renders are free.
        if (_complete && frozenInThisTail < tail.Length)
        {
            RenderRangeInto(_frozenLines, tail, frozenInThisTail, tail.Length, _width, _scratch);
            _frozenSourceChars = tailBase + tail.Length;
            frozenInThisTail = tail.Length;
        }

        // ENG11 #283 holdback: an incomplete structural block runs to EOF, so
        // only its stable prefix (fence/math opener; nothing for tables)
        // joins the painted tail — the buffered interior repaints never reach
        // the cells.
        int paintEnd = HoldbackPaintEnd(tail, _blocksScratch, frozenInThisTail);
        RenderFreshTail(source, tailBase + frozenInThisTail, tailBase + paintEnd);
        int paintedAbs = tailBase + paintEnd;
        bool changed = rebuiltAll || frozeAny || _complete || paintedAbs != _lastPaintedChars;
        _lastPaintedChars = paintedAbs;
        Memoize(sourceLen);
        return changed;
    }

    /// <summary>Records the state a repeat call at the same inputs is a no-op.</summary>
    private void Memoize(int sourceLen)
    {
        _lastWidth = _width;
        _lastSourceLen = sourceLen;
        _lastComplete = _complete;
    }

    /// <summary>Drops the cached tail prefix (width change rebuilds all geometry).</summary>
    private void InvalidateTailPrefix()
    {
        _tailLines.Clear();
        _tailBase = -1;
        _tailStableUpTo = 0;
        _tailStableLines = 0;
    }

    /// <summary>
    /// End (tail-relative) of the paintable region: the first incomplete
    /// fence/table/math block truncates it to its stable prefix. Any other
    /// tail content paints in full, exactly as before.
    /// </summary>
    private static int HoldbackPaintEnd(ReadOnlySpan<char> tail, List<MdBlock> blocks, int from)
    {
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (b.Start < from || b.Complete)
            {
                continue;
            }

            if (b.Kind is MdBlockKind.Fence or MdBlockKind.Math)
            {
                int nl = tail.Slice(b.Start).IndexOf('\n');
                return nl < 0 ? tail.Length : b.Start + nl + 1;
            }

            if (b.Kind == MdBlockKind.Table)
            {
                return b.Start;
            }

            // Incomplete paragraph/list: keep scanning — a structural block
            // may follow further down the tail.
        }

        return tail.Length;
    }

    /// <summary>
    /// Rebuilds the painted tail over [_fromAbs, _paintEndAbs) source offsets.
    /// The newline-terminated prefix is kept from the previous render and only
    /// the trailing partial line is re-rendered — that is what turns a long
    /// open paragraph from O(N) per push into O(delta) (#463).
    /// </summary>
    private void RenderFreshTail(ReadOnlySpan<char> source, int fromAbs, int paintEndAbs)
    {
        if (_tailBase != fromAbs || _tailStableUpTo > paintEndAbs)
        {
            // Tail moved (a block froze) or shrank (holdback closed): the
            // cached prefix no longer describes the painted region.
            InvalidateTailPrefix();
            _tailBase = fromAbs;
            _tailStableUpTo = fromAbs;
        }
        else if (_tailLines.Count > _tailStableLines)
        {
            // Drop the lines of the still-growing last source line.
            _tailLines.RemoveRange(_tailStableLines, _tailLines.Count - _tailStableLines);
        }

        if (fromAbs >= paintEndAbs)
        {
            _tailLines.Clear();
            _tailStableLines = 0;
            _tailStableUpTo = fromAbs;
            return;
        }

        // Everything up to the last newline is immutable: those source lines
        // can never be extended, so their wrapped MdLines are reused verbatim
        // and only the trailing partial line is re-rendered. This is the
        // O(delta) half of #463.
        int lastNewline = source.Slice(_tailStableUpTo, paintEndAbs - _tailStableUpTo).LastIndexOf('\n');
        int stableEnd = lastNewline < 0 ? _tailStableUpTo : _tailStableUpTo + lastNewline + 1;

        if (stableEnd > _tailStableUpTo)
        {
            RenderRangeInto(_tailLines, source, _tailStableUpTo, stableEnd, _width, _scratch);
            _tailStableUpTo = stableEnd;
            _tailStableLines = _tailLines.Count;
        }

        if (stableEnd < paintEndAbs)
        {
            RenderRangeInto(_tailLines, source, stableEnd, paintEndAbs, _width, _scratch);
        }
    }

    /// <summary>The line starting at <paramref name="at"/> is blank (separator lookahead for math blocks).</summary>
    private static bool StartsBlankLine(ReadOnlySpan<char> tail, int at)
    {
        if ((uint)at >= (uint)tail.Length)
        {
            return false;
        }

        int nl = tail.Slice(at).IndexOf('\n');
        int end = nl < 0 ? tail.Length : at + nl;
        return tail.Slice(at, end - at).TrimEnd('\r').IsWhiteSpace();
    }

    /// <summary>The block ended right before a blank separator line («\n\n» boundary).</summary>
    private static bool HasBlankTerminator(ReadOnlySpan<char> tail, int blockEnd)
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

        return tail.Slice(i + 1, blockEnd - 1 - (i + 1)).IsWhiteSpace();
    }

    /// <summary>Renders source[start,end) into a caller-owned list of wrapped display lines.</summary>
    private static void RenderRangeInto(
        List<MdLine> output,
        ReadOnlySpan<char> source,
        int start,
        int end,
        int width,
        MdScratch scratch)
    {
        var region = source.Slice(start, Math.Clamp(end - start, 0, source.Length - start));
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
                    scratch.Single.Clear();
                    scratch.Single.Add(new MdSpan(trimmed.TrimEnd('\r').ToString(), MdStyle.Fence));
                    AddWrapped(output, scratch.Single, width, scratch);
                    break;

                case LineKind.MathFence:
                    // ENG11 #283: «$$» delimiters are structural markers —
                    // dimmed like fences; the body stays plain text.
                    scratch.Single.Clear();
                    scratch.Single.Add(new MdSpan(trimmed.TrimEnd('\r').ToString(), MdStyle.Fence));
                    AddWrapped(output, scratch.Single, width, scratch);
                    break;

                case LineKind.Heading:
                    {
                        int level = MarkdownBlockParser.HeadingLevel(trimmed);
                        int textStart = level + (level < trimmed.Length ? 1 : 0);
                        var text = trimmed.Slice(textStart).TrimEnd('\r');
                        scratch.Single.Clear();
                        scratch.Single.Add(new MdSpan(text.ToString(), MdStyle.Heading));
                        AddWrapped(output, scratch.Single, width, scratch);
                        break;
                    }

                case LineKind.ListItem:
                    {
                        _ = MarkdownBlockParser.IsListItem(trimmed, out int markerWidth);
                        ScanInlineInto(trimmed.Slice(markerWidth).TrimEnd('\r'), scratch.Spans, scratch);
                        scratch.Bullets.Clear();
                        scratch.Bullets.Add(new MdSpan(trimmed.Slice(0, markerWidth).ToString(), MdStyle.Bullet));
                        scratch.Bullets.AddRange(scratch.Spans);
                        AddWrapped(output, scratch.Bullets, width, scratch);
                        break;
                    }

                default:
                    ScanInlineInto(trimmed.TrimEnd('\r'), scratch.Spans, scratch);
                    AddWrapped(output, scratch.Spans, width, scratch);
                    break;
            }

            pos = terminated ? lineEnd + 1 : region.Length;
        }
    }

    /// <summary>
    /// Renders source[start,end) into wrapped styled display lines. Width-keyed
    /// cached caller side (AssistantMarkdownBlock), so a fresh scratch per call
    /// is not on any hot path; the streaming renderer never comes through here.
    /// </summary>
    internal static List<MdLine> RenderRange(string sourceText, int start, int end, int width)
    {
        var lines = new List<MdLine>(4);
        RenderRangeInto(lines, sourceText.AsSpan(), start, end, width, new MdScratch());
        return lines;
    }

    /// <summary>Single-pass inline scanner: **bold**, *italic*, `code`.</summary>
    public static List<MdSpan> ScanInline(ReadOnlySpan<char> line)
    {
        var spans = new List<MdSpan>(4);
        ScanInlineInto(line, spans, new MdScratch());
        return spans;
    }

    /// <summary>Fills a caller-owned span list — no allocation on the hot path.</summary>
    private static void ScanInlineInto(ReadOnlySpan<char> line, List<MdSpan> spans, MdScratch scratch)
    {
        spans.Clear();
        var text = scratch.Scan;
        text.Clear();
        bool bold = false, italic = false, code = false;

        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == '`')
            {
                EmitPending(text, spans, bold, italic, code);
                code = !code;
                i++;
            }
            else if (c == '*' && i + 1 < line.Length && line[i + 1] == '*')
            {
                EmitPending(text, spans, bold, italic, code);
                bold = !bold;
                i += 2;
            }
            else if (c == '*')
            {
                EmitPending(text, spans, bold, italic, code);
                italic = !italic;
                i++;
            }
            else
            {
                text.Append(c);
                i++;
            }
        }

        EmitPending(text, spans, bold, italic, code);

        if (spans.Count == 0)
        {
            spans.Add(new MdSpan(string.Empty, MdStyle.Normal));
        }
    }

    private static MdStyle StyleOf(bool bold, bool italic, bool code) =>
        code ? MdStyle.Code
        : bold && italic ? MdStyle.BoldItalic
        : bold ? MdStyle.Bold
        : italic ? MdStyle.Italic
        : MdStyle.Normal;

    private static void EmitPending(StringBuilder text, List<MdSpan> spans, bool bold, bool italic, bool code)
    {
        if (text.Length == 0)
        {
            return;
        }

        spans.Add(new MdSpan(text.ToString(), StyleOf(bold, italic, code)));
        text.Clear();
    }

    /// <summary>
    /// Greedy hard wrap of styled spans to <paramref name="width"/> cells.
    /// Wide runes never split; zero-width runes attach forward. An empty span
    /// list yields one empty line (keeps blank paragraphs representable).
    /// </summary>
    private static void AddWrapped(List<MdLine> output, List<MdSpan> spans, int width, MdScratch scratch)
    {
        if (spans.Count == 0)
        {
            output.Add(MdLine.Empty);
            return;
        }

        var state = new WrapState { Line = new List<MdSpan>(4), Work = scratch.Wrap };
        state.Work.Clear();

        for (int i = 0; i < spans.Count; i++)
        {
            MdSpan s = spans[i];
            var rest = s.Text.AsSpan();
            while (!rest.IsEmpty)
            {
                Rune.DecodeFromUtf16(rest, out var rune, out int size);
                int rw = Rendering.UnicodeWidth.Width(rune);
                if (state.Cells > 0 && state.Cells + rw > width)
                {
                    FlushLine(output, ref state);
                }

                if (state.HasWork && state.Style != s.Style)
                {
                    // Style boundary: commit the accumulated run first.
                    state.Line.Add(new MdSpan(state.Work.ToString(), state.Style));
                    state.Work.Clear();
                    state.HasWork = false;
                }

                if (!state.HasWork)
                {
                    state.Style = s.Style;
                    state.HasWork = true;
                }

                state.Work.Append(rest[..size]);
                state.Cells += rw;
                rest = rest[size..];
            }
        }

        FlushLine(output, ref state);
    }

    /// <summary>Wrap accumulator passed by ref — keeps the flush path closure-free.</summary>
    private struct WrapState
    {
        public List<MdSpan> Line;
        public StringBuilder Work;
        public MdStyle Style;
        public bool HasWork;
        public int Cells;
    }

    private static void FlushLine(List<MdLine> output, ref WrapState state)
    {
        if (state.HasWork)
        {
            state.Line.Add(new MdSpan(state.Work.ToString(), state.Style));
            state.Work.Clear();
            state.HasWork = false;
        }

        if (state.Line.Count > 0)
        {
            output.Add(new MdLine(state.Line));
            state.Line = [];
        }

        state.Cells = 0;
    }

    /// <summary>
    /// Per-render scratch buffers. They never escape into a rendered
    /// <see cref="MdLine"/> (each display line gets a fresh span list), so a
    /// single instance can serve every render of a renderer.
    /// </summary>
    private sealed class MdScratch
    {
        public List<MdSpan> Spans { get; } = new(4);

        public List<MdSpan> Bullets { get; } = new(4);

        public List<MdSpan> Single { get; } = new(1);

        public StringBuilder Scan { get; } = new();

        public StringBuilder Wrap { get; } = new(64);
    }
}
