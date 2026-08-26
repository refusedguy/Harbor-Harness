using Harbor.Tui.ConsoleEx.Rendering;

namespace Harbor.Tui.ConsoleEx.Widgets;

/// <summary>
/// Finalized assistant message block. In CE-3 W1 it renders the markdown
/// source as plain wrapped lines (dim fence markers); W2.1 swaps the paint to
/// the frozen-tail <c>StreamingMarkdownRenderer</c> snapshot without touching
/// callers — measure/paint stay width-keyed and allocation-free in steady
/// state.
/// </summary>
public sealed class AssistantMarkdownBlock : IChatBlock
{
    private readonly WrappedText _source;

    public AssistantMarkdownBlock(string source) => _source = new WrappedText(source ?? string.Empty);

    public string Kind => "assistant";

    public bool IsStreamContinuation => false;

    public int BudgetBytes => 64 + (_source.SourceLength * 2);

    public BlockMeasure Measure(int width) =>
        BlockMeasure.Exact(Math.Max(1, _source.GetLines(Math.Max(1, width)).Length));

    public void Paint(in BlockPaintContext ctx)
    {
        var buffer = ctx.Buffer;
        var lines = _source.GetLines(Math.Max(1, ctx.Rect.Width));
        int rows = ctx.Rect.Height;
        for (int i = 0; i < lines.Length && i < rows; i++)
        {
            var line = lines.Span[i].AsSpan();
            var style = line.StartsWith("```") ? ChatPalette.Dim : CellStyle.Plain;
            buffer.SetText(ctx.Rect.X, ctx.Rect.Y + i, line, style);
        }
    }

    public string RawText() => _source.Source;
}
