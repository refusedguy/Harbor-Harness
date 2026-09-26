using System.Text;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>Layout-tree leaf hosting the chat feed (vertical split over the composer).</summary>
public sealed class ChatTimelinePanel : Rendering.Panel
{
    public ChatTimelinePanel(string id, int minWidth, int minHeight, int priority = 10)
        : base(id, new Size(minWidth, minHeight), priority)
    {
    }

    public VirtualizedChatTimeline Timeline { get; } = new();

    public override void Paint(ScreenBuffer buffer)
    {
        Timeline.CurrentTick++;
        Timeline.Paint(buffer, Rect);
    }
}

/// <summary>Live streaming thinking block: accumulates reasoning text and
/// re-renders it with dim+italic styling on every layout pass.</summary>
public sealed class StreamingThinkingBlock : IChatBlock
{
    private readonly StringBuilder _text = new();
    private int _width = -1;
    private string[] _lines = [];

    public string Kind => "thinking";

    public bool IsStreamContinuation => true;

    public int BudgetBytes => 48 + (_text.Length * 2);

    public void Append(string delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        _text.Append(delta);
        _width = -1;
    }

    public BlockMeasure Measure(int width)
    {
        EnsureWrapped(width);
        return BlockMeasure.Exact(Math.Max(1, _lines.Length));
    }

    public int CheapEstimate(int width) => BlockMath.EstimateLines(_text.ToString(), Math.Max(1, width));

    public void Paint(in BlockPaintContext ctx)
    {
        EnsureWrapped(ctx.Rect.Width);
        var buffer = ctx.Buffer;
        int rows = ctx.Rect.Height;
        int skip = ctx.SkipRows;
        for (int i = 0; i < _lines.Length && (skip + i) < _lines.Length && i < rows; i++)
        {
            buffer.SetText(ctx.Rect.X, ctx.Rect.Y + i, _lines[skip + i], new CellStyle(attrs: StyleAttr.Dim | StyleAttr.Italic));
        }
    }

    public string RawText() => _text.ToString();

    private void EnsureWrapped(int width)
    {
        if (_width != width || _lines.Length == 0 && _text.Length > 0)
        {
            _width = width;
            var list = new List<string>(Math.Max(1, _lines.Length));
            TextWrap.WrapDocument(_text.ToString(), Math.Max(1, width), list);
            _lines = [.. list];
        }
    }
}

/// <summary>Finalized thinking block: renders committed reasoning text with
/// dim+italic styling, wrapped to the available width.</summary>
public sealed class ThinkingBlock : IChatBlock
{
    private readonly WrappedText _text;

    public ThinkingBlock(string text) => _text = new WrappedText(text ?? string.Empty);

    public string Kind => "thinking";

    public bool IsStreamContinuation => false;

    public int BudgetBytes => 48 + (_text.SourceLength * 2);

    public BlockMeasure Measure(int width) =>
        BlockMeasure.Exact(Math.Max(1, _text.GetLines(Math.Max(1, width)).Length));

    public int CheapEstimate(int width) => BlockMath.EstimateLines(_text.Source, Math.Max(1, width));

    public void Paint(in BlockPaintContext ctx)
    {
        var buffer = ctx.Buffer;
        var lines = _text.GetLines(Math.Max(1, ctx.Rect.Width));
        int rows = ctx.Rect.Height;
        int skip = ctx.SkipRows;
        for (int i = 0; i < lines.Length && (skip + i) < lines.Length && i < rows; i++)
        {
            buffer.SetText(ctx.Rect.X, ctx.Rect.Y + i, lines.Span[skip + i], new CellStyle(attrs: StyleAttr.Dim | StyleAttr.Italic));
        }
    }

    public string RawText() => _text.Source;
}
