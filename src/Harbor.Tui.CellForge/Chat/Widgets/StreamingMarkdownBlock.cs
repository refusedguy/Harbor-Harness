using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Rendering.Markdown;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Live streaming assistant block (codex stream-cell): wraps a
/// <see cref="StreamingMarkdownRenderer"/>, re-renders its tail on every
/// layout pass and paints styled markdown lines into the grid. Committed by
/// swapping this slot for an <see cref="AssistantMarkdownBlock"/>.
/// </summary>
public sealed class StreamingMarkdownBlock : IChatBlock
{
    private readonly StreamingMarkdownRenderer _renderer = new();
    private int _lastRenderWidth;

    public StreamingMarkdownBlock() { }

    /// <summary>Test seam: start from pre-accumulated text.</summary>
    public StreamingMarkdownBlock(StreamingMarkdownRenderer renderer) =>
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));

    private int EffectiveWidth(int requested)
    {
        if (requested > 0)
        {
            return requested;
        }

        return _lastRenderWidth > 0 ? _lastRenderWidth : 80;
    }

    private void EnsureRendered(int width)
    {
        _lastRenderWidth = EffectiveWidth(width);
        _ = _renderer.RenderTail(_lastRenderWidth);
    }

    public string Kind => "stream";

    public bool IsStreamContinuation => true;

    public int BudgetBytes => 96 + (_renderer.Checkpoint.SourceChars * 2);

    public bool IsLive => !_renderer.IsComplete;

    public void Push(ReadOnlySpan<char> chunk) => _renderer.Push(chunk);

    public void Complete() => _renderer.Complete();

    public override string ToString() => $"stream({_renderer.LineCount} lines)";

    public BlockMeasure Measure(int width)
    {
        EnsureRendered(width);
        return BlockMeasure.Exact(_renderer.LineCount);
    }

    public int CheapEstimate(int width) => Math.Max(1, _renderer.LineCount);

    public void Paint(in BlockPaintContext ctx)
    {
        EnsureRendered(ctx.Rect.Width);
        int rows = ctx.Rect.Height;
        int skip = ctx.SkipRows;
        for (int i = 0; i < rows && (skip + i) < _renderer.LineCount; i++)
        {
            AssistantMarkdownBlock.PaintLine(ctx.Buffer, ctx.Rect.X, ctx.Rect.Y + i, _renderer.LineAt(skip + i));
        }
    }

    public string RawText()
    {
        EnsureRendered(_lastRenderWidth);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _renderer.LineCount; i++)
        {
            foreach (var s in _renderer.LineAt(i).Spans)
            {
                sb.Append(s.Text);
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }
}
