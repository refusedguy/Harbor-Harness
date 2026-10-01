using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Rendering.Markdown;
using Harbor.Ui.Framework.Rendering.PerformanceContracts;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Finalized assistant message block: renders its immutable markdown source
/// through the same styled-line pipeline as the streaming tail (one-shot
/// render, width-keyed cache). Measure/Paint stay allocation-free in steady
/// state.
/// Collapse protocol lives on <c>ICollapsibleChatBlock</c> (PRIM1a #291,
/// parent #286): answers over <see cref="MaxBodyLines"/> (universal 10,
/// [UX2] #262) start collapsed with a <c>…</c> overflow marker; shorter
/// answers paint fully, so the default-collapsed state stays byte-identical
/// to the pre-collapse layout. Hosts expand via <see cref="SetExpanded"/>
/// (feed Enter/click/space path via <see cref="ToggleExpanded"/>).
/// </summary>
public sealed class AssistantMarkdownBlock : ICollapsibleChatBlock
{
    private readonly string _source;
    private readonly string? _header;
    private List<MdLine> _lines = [];
    private Dictionary<int, List<CodeSpan>>? _code;
    private int _width = -1;
    private Rect? _lastPaintRect;
    private int _lastSkipRows;

    public AssistantMarkdownBlock(string source, string? header = null)
    {
        _source = source ?? string.Empty;
        _header = string.IsNullOrWhiteSpace(header) ? null : header;
        MaxBodyLines = ICollapsibleChatBlock.DefaultUniversalBodyLines;
        IsExpanded = false;
    }

    public string Kind => "assistant";

    public bool IsStreamContinuation => false;

    public int BudgetBytes => 64 + (_source.Length * 2);

    /// <summary>
    /// Collapsed-body line budget ([UX2] #262: universal 10-line policy).
    /// Collapse protocol lives on <c>ICollapsibleChatBlock</c> (PRIM1a #291);
    /// this property satisfies the mixin contract.
    /// </summary>
    public int MaxBodyLines { get; set; }

    /// <summary>
    /// Whether the block is expanded. Satisfies the
    /// <c>ICollapsibleChatBlock</c> mixin contract; defaults to
    /// <c>false</c> so answers over the budget collapse structurally — no
    /// ingestion changes needed. Collapsed paint shows the header plus the
    /// first <see cref="MaxBodyLines"/> styled body rows with a <c>…</c>
    /// overflow marker; shorter answers paint fully either way.
    /// </summary>
    public bool IsExpanded { get; private set; }

    /// <summary>Flips <see cref="IsExpanded"/> (feed Enter/click/space path).</summary>
    public void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>Sets <see cref="IsExpanded"/> explicitly (host-driven focus path).</summary>
    public void SetExpanded(bool expanded) => IsExpanded = expanded;

    /// <summary>
    /// Click hit-test for the header/first row (unified expand gesture,
    /// [UX2] #262; mirrors <c>ToolCallBlock.TryHitHeader</c>).
    /// </summary>
    public bool TryHitHeader(int col, int row)
    {
        if (_lastPaintRect is not { } rect || _lastSkipRows != 0)
        {
            return false;
        }

        return row == rect.Y && col >= rect.X && col < rect.X + rect.Width;
    }

    public BlockMeasure Measure(int width)
    {
        EnsureRendered(width);
        int headerRows = _header is null ? 0 : 1;
        int bodyRows = IsExpanded ? _lines.Count : CollapsedBodyLineCount();
        return BlockMeasure.Exact(headerRows + bodyRows + 1);
    }

    public int CheapEstimate(int width)
    {
        int body = BlockMath.EstimateLines(_source, Math.Max(1, width));
        if (!IsExpanded)
        {
            // Mirror Measure: collapsed body is capped at MaxBodyLines plus
            // one overflow-marker row ([UX5] #265: zero budget = no body row).
            body = MaxBodyLines <= 0 ? 0 : Math.Min(body, MaxBodyLines + 1);
        }

        return (_header is null ? 0 : 1) + 1 + body;
    }

    public void Paint(in BlockPaintContext ctx)
    {
        _lastPaintRect = ctx.Rect;
        _lastSkipRows = ctx.SkipRows;

        EnsureRendered(ctx.Rect.Width);
        var buffer = ctx.Buffer;
        int rows = ctx.Rect.Height;
        int skip = ctx.SkipRows;
        int headerRows = _header is null ? 0 : 1;
        int bodyRows = IsExpanded ? _lines.Count : CollapsedBodyLineCount();
        int totalRows = bodyRows + headerRows + 1;

        // Collapsed overflow marker row (mixin geometry, styled paint stays
        // local): when the collapse budget cuts styled body rows, the row
        // right after the last shown one carries the dim '…' marker.
        int budget = Math.Max(0, MaxBodyLines);
        bool collapsedOverflow = !IsExpanded && MaxBodyLines > 0 && _lines.Count > budget;
        int markerLineIdx = headerRows + budget;

        for (int i = 0; i < rows && (skip + i) < totalRows; i++)
        {
            int lineIdx = skip + i;
            if (headerRows == 1 && lineIdx == 0)
            {
                buffer.SetText(ctx.Rect.X, ctx.Rect.Y + i, _header!, ChatPalette.Dim);
                continue;
            }

            int bodyIdx = lineIdx - headerRows;
            if (bodyIdx >= bodyRows)
            {
                // Panel chrome: with separators enabled the trailing gap row
                // becomes a thin dim divider between answers (off by default —
                // direct block paints stay byte-identical, goldens unaffected).
                if (ctx.ShowSeparators)
                {
                    PanelChrome.PaintMessageSeparator(buffer, ctx.Rect.X, ctx.Rect.Y + i, ctx.Rect.Width);
                }

                continue; // trailing gap row: breathing room between bubbles
            }

            if (collapsedOverflow && lineIdx == markerLineIdx)
            {
                buffer.SetText(ctx.Rect.X, ctx.Rect.Y + i, "…", ChatPalette.Dim);
                continue;
            }

            int contentIdx = bodyIdx;
            if (_code is not null && _code.TryGetValue(contentIdx, out var codeSpans))
            {
                PaintCodeSpans(buffer, ctx.Rect.X, ctx.Rect.Y + i, codeSpans);
            }
            else
            {
                PaintLine(buffer, ctx.Rect.X, ctx.Rect.Y + i, _lines[contentIdx]);
            }
        }
    }

    public string RawText() => _source;

    internal static void PaintLine(ScreenBuffer buffer, int x, int y, MdLine line)
    {
        int cursor = x;
        for (int s = 0; s < line.Spans.Count; s++)
        {
            var span = line.Spans[s];
            buffer.SetText(cursor, y, span.Text, StyleFor(span.Style));
            cursor += UnicodeWidth.Width(span.Text);
        }
    }

    internal static void PaintCodeSpans(ScreenBuffer buffer, int x, int y, List<CodeSpan> spans)
    {
        int cursor = x;
        for (int s = 0; s < spans.Count; s++)
        {
            var span = spans[s];
            buffer.SetText(cursor, y, span.Text, span.Style);
            cursor += UnicodeWidth.Width(span.Text);
        }
    }

    internal static CellStyle StyleFor(MdStyle style) => style switch
    {
        MdStyle.Bold or MdStyle.Heading => new CellStyle(
            style == MdStyle.Heading ? PackedColor.Indexed(4) : default,
            attrs: StyleAttr.Bold),
        MdStyle.Italic => new CellStyle(attrs: StyleAttr.Italic),
        MdStyle.BoldItalic => new CellStyle(attrs: StyleAttr.Bold | StyleAttr.Italic),
        MdStyle.Code => new CellStyle(PackedColor.Indexed(3)),
        MdStyle.Fence => ChatPalette.Dim,
        MdStyle.Bullet => new CellStyle(PackedColor.Indexed(4)),
        _ => CellStyle.Plain,
    };

    /// <summary>
    /// Visible styled body rows under <see cref="MaxBodyLines"/>: capped rows
    /// plus one continuation-marker row on overflow. Geometry mirrors
    /// <c>ICollapsibleChatBlock.ClampedBodyLineCount</c> (PRIM1a #291) over
    /// rendered rows — styled paint can't delegate to the plain-text
    /// <c>PaintBodyLines</c>, so the cap lives here with identical overflow
    /// semantics (incl. the [UX5] #265 zero-budget law: no body, no marker).
    /// </summary>
    private int CollapsedBodyLineCount()
    {
        if (MaxBodyLines <= 0 || _lines.Count == 0)
        {
            return 0;
        }

        return Math.Min(_lines.Count, MaxBodyLines) + (_lines.Count > MaxBodyLines ? 1 : 0);
    }

    private void EnsureRendered(int width)
    {
        if (_width != width || _lines.Count == 0 && _source.Length > 0)
        {
            _width = width;
            // #409: counted here, INSIDE the memo, not at method entry. Measure
            // and Paint both call this every frame; counting at entry would
            // report the pair twice and inflate the stage that layout drives.
            UiStageCounters.CountMaterialization();
            _lines = StreamingMarkdownRenderer.RenderRange(_source, 0, _source.Length, Math.Max(1, width));
            _code = CodeTokenizer.HighlightFenceBodies(_lines);
        }
    }
}
