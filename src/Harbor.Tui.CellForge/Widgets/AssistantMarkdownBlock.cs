using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Rendering.Markdown;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Finalized assistant message block: renders its immutable markdown source
/// through the same styled-line pipeline as the streaming tail (one-shot
/// render, width-keyed cache). Measure/Paint stay allocation-free in steady
/// state.
/// </summary>
public sealed class AssistantMarkdownBlock : IChatBlock
{
    private readonly string _source;
    private List<MdLine> _lines = [];
    private Dictionary<int, List<CodeSpan>>? _code;
    private int _width = -1;

    public AssistantMarkdownBlock(string source) => _source = source ?? string.Empty;

    public string Kind => "assistant";

    public bool IsStreamContinuation => false;

    public int BudgetBytes => 64 + (_source.Length * 2);

    public BlockMeasure Measure(int width)
    {
        EnsureRendered(width);
        return BlockMeasure.Exact(_lines.Count);
    }

    public int CheapEstimate(int width) => BlockMath.EstimateLines(_source, Math.Max(1, width));

    public void Paint(in BlockPaintContext ctx)
    {
        EnsureRendered(ctx.Rect.Width);
        var buffer = ctx.Buffer;
        int rows = ctx.Rect.Height;
        int skip = ctx.SkipRows;
        for (int i = 0; i < rows && (skip + i) < _lines.Count; i++)
        {
            int lineIdx = skip + i;
            if (_code is not null && _code.TryGetValue(lineIdx, out var codeSpans))
            {
                PaintCodeSpans(buffer, ctx.Rect.X, ctx.Rect.Y + i, codeSpans);
            }
            else
            {
                PaintLine(buffer, ctx.Rect.X, ctx.Rect.Y + i, _lines[lineIdx]);
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

    private void EnsureRendered(int width)
    {
        if (_width != width || _lines.Count == 0 && _source.Length > 0)
        {
            _width = width;
            _lines = StreamingMarkdownRenderer.RenderRange(_source, 0, _source.Length, Math.Max(1, width));
            _code = CodeTokenizer.HighlightFenceBodies(_lines);
        }
    }
}
