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

/// <summary>User prompt block: bold accent prefix «› » + bold body (widgets §3.1).</summary>
public sealed class UserBlock : IChatBlock
{
    private const string Prefix = "› ";
    private readonly WrappedText _text;

    public UserBlock(string text) => _text = new WrappedText(text ?? string.Empty);

    public string Kind => "user";

    public bool IsStreamContinuation => false;

    public int BudgetBytes => 64 + (_text.SourceLength * 2);

    public BlockMeasure Measure(int width) =>
        BlockMeasure.Exact(Math.Max(1, _text.GetLines(BodyWidth(width)).Length));

    public int CheapEstimate(int width) => BlockMath.EstimateLines(_text.Source, Math.Max(1, BodyWidth(width)));

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
        var lines = _text.GetLines(bodyWidth);
        for (int i = 0; i < lines.Length && i < rows; i++)
        {
            if (i == 0)
            {
                buffer.SetText(ctx.Rect.X, y, Prefix, ChatPalette.UserPrefix);
            }

            buffer.SetText(ctx.Rect.X + Prefix.Length, y + i, lines.Span[i], ChatPalette.UserText);
        }
    }

    public string RawText() => Prefix + _text.Source;

    private static int BodyWidth(int rectWidth) => rectWidth - Prefix.Length;
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
        for (int i = 0; i < lines.Length && i < rows; i++)
        {
            buffer.SetText(ctx.Rect.X, ctx.Rect.Y + i, lines.Span[i], ChatPalette.System);
        }
    }

    public string RawText() => _text.Source;
}
