using System.Text;
using Harbor.Ui.Framework.Rendering;

namespace Harbor.Ui.Framework.Rendering.Widgets;

/// <summary>
/// Width-keyed wrap cache for an immutable text: the wrapped line list is
/// rebuilt only when the layout width changes, so repeated Measure/Paint at a
/// stable width allocate nothing. Lines are stored pre-trimmed, ready to be
/// blitted with <see cref="ScreenBuffer.SetText"/>.
/// </summary>
public sealed class WrappedText
{
    private string[] _lines = [];
    private int _width = -1;
    private readonly string _source;

    public WrappedText(string source) => _source = source;

    public int SourceLength => _source.Length;

    /// <summary>The unwrapped source text.</summary>
    public string Source => _source;

    /// <summary>Wrapped lines at <paramref name="width"/>; rebuilds on width change.</summary>
    public ReadOnlyMemory<string> GetLines(int width)
    {
        if (width != _width)
        {
            var rebuilt = new List<string>(Math.Max(1, _lines.Length));
            Rendering.TextWrap.WrapDocument(_source, Math.Max(1, width), rebuilt);
            _lines = [.. rebuilt];
            _width = width;
        }

        return _lines;
    }
}

/// <summary>Shared arithmetic helpers for text blocks.</summary>
internal static class BlockMath
{
    /// <summary>Sum of per-logical-line ceil(length/width) — allocation-free estimate.</summary>
    public static int EstimateLines(string source, int width)
    {
        int total = 0;
        int run = 0;
        foreach (char c in source)
        {
            if (c == '\n')
            {
                total += Math.Max(1, (run + width - 1) / width);
                run = 0;
                continue;
            }

            run++;
        }

        total += Math.Max(1, (run + width - 1) / width);
        return Math.Max(1, total);
    }
}

/// <summary>User prompt bubble: accent header («YOU»), accent bar and tinted
/// body (widgets §3.1). The header costs one row; wrap width is unchanged
/// (2-cell gutter, same as the old «› » prefix).</summary>
public sealed class UserBlock : IChatBlock
{
    private const string Header = "YOU";
    private const int Gutter = 2;

    /// <summary>Legacy text prefix kept for <see cref="RawText" /> (selection copy + history matching).</summary>
    private const string Prefix = "› ";
    private readonly WrappedText _text;

    public UserBlock(string text) => _text = new WrappedText(text ?? string.Empty);

    public string Kind => "user";

    public bool IsStreamContinuation => false;

    public int BudgetBytes => 64 + (_text.SourceLength * 2);

    public BlockMeasure Measure(int width) =>
        BlockMeasure.Exact(1 + Math.Max(1, _text.GetLines(BodyWidth(width)).Length));

    public int CheapEstimate(int width) => 1 + BlockMath.EstimateLines(_text.Source, Math.Max(1, BodyWidth(width)));

    public void Paint(in BlockPaintContext ctx)
    {
        var buffer = ctx.Buffer;
        int bodyWidth = BodyWidth(ctx.Rect.Width);
        if (bodyWidth <= 0)
        {
            return;
        }

        int y = ctx.Rect.Y;
        int rows = ctx.Rect.Bottom - y;
        int skip = ctx.SkipRows;
        if (rows <= 0)
        {
            return;
        }

        var headerStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        var barStyle = new CellStyle(ChatPalette.Accent);
        var bodyStyle = new CellStyle(ChatPalette.UserText.Fg, ChatPalette.Panel, ChatPalette.UserText.Attrs);
        var bgCell = Cell.From(new Rune(' '), new CellStyle(bg: ChatPalette.Panel));

        var lines = _text.GetLines(bodyWidth);
        for (int i = 0; i < rows && (skip + i) < lines.Length + 1; i++)
        {
            int row = skip + i;
            int paintY = y + i;
            buffer.Fill(new Rect(ctx.Rect.X, paintY, ctx.Rect.Width, 1), in bgCell);
            if (row == 0)
            {
                buffer.SetText(ctx.Rect.X + 1, paintY, Header, headerStyle);
                continue;
            }

            buffer.SetText(ctx.Rect.X, paintY, "│", barStyle);
            buffer.SetText(ctx.Rect.X + Gutter, paintY, lines.Span[row - 1], bodyStyle);
        }
    }

    public string RawText() => Prefix + _text.Source;

    private static int BodyWidth(int rectWidth) => rectWidth - Gutter;
}

/// <summary>Dim italic system notice (session events, compaction, errors).</summary>
public sealed class SystemBlock : IChatBlock
{
    private readonly WrappedText _text;

    public SystemBlock(string text) => _text = new WrappedText(text ?? string.Empty);

    public string Kind => "system";

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
        for (int i = 0; i < rows && (skip + i) < lines.Length; i++)
        {
            buffer.SetText(ctx.Rect.X, ctx.Rect.Y + i, lines.Span[skip + i], ChatPalette.System);
        }
    }

    public string RawText() => _text.Source;
}
