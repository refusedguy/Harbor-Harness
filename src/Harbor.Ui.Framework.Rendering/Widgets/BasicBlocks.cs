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
    /// <summary>
    /// Sum of per-logical-line ceil(length/width) — allocation-free estimate.
    /// <paramref name="width"/> is floored at 1 internally (#481): a zero/negative
    /// layout width degenerates to "one column per char" instead of throwing
    /// <see cref="DivideByZeroException"/> on the render thread, so callers no
    /// longer have to wrap every call site in their own guard. Mirrors the
    /// literal copy in <c>TimelineBlocks.CheapEstimate</c>.
    /// </summary>
    public static int EstimateLines(string source, int width)
    {
        width = Math.Max(1, width);
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
/// (2-cell gutter, same as the old «› » prefix).
/// Collapsible per the <c>ICollapsibleChatBlock</c> mixin ([UX2] #262):
/// bodies over <see cref="MaxBodyLines"/> (universal 10) render collapsed
/// with a <c>"... (N hidden)"</c> tail; shorter bodies paint fully, so the
/// default-collapsed state stays byte-identical to the pre-collapse layout.
/// Feed Enter/click/space toggles via <see cref="ToggleExpanded"/>.</summary>
public sealed class UserBlock : ICollapsibleChatBlock
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

    /// <summary>
    /// Collapsed-body line budget ([UX2] #262: universal 10-line policy).
    /// Collapse protocol lives on <c>ICollapsibleChatBlock</c> (PRIM1a #291);
    /// this property satisfies the mixin contract.
    /// </summary>
    public int MaxBodyLines { get; set; } = ICollapsibleChatBlock.DefaultUniversalBodyLines;

    /// <summary>
    /// Whether the block is expanded (feed Enter/click/space toggles via
    /// <see cref="ToggleExpanded"/>). Defaults to <c>false</c> so bodies over
    /// the budget collapse structurally — no ingestion changes needed; short
    /// bodies paint fully either way, keeping existing goldens byte-identical.
    /// </summary>
    public bool IsExpanded { get; private set; }

    /// <summary>Flips <see cref="IsExpanded"/> (feed Enter/click/space path).</summary>
    public void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>Sets <see cref="IsExpanded"/> explicitly (host-driven focus path).</summary>
    public void SetExpanded(bool expanded) => IsExpanded = expanded;

    private Rect? _lastPaintRect;
    private int _lastSkipRows;

    /// <summary>
    /// Click hit-test for the header row (unified expand gesture, [UX2] #262;
    /// mirrors <c>ToolCallBlock.TryHitHeader</c>).
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
        var lines = _text.GetLines(BodyWidth(width));
        if (!IsExpanded && lines.Length > MaxBodyLines)
        {
            // Collapsed overflow: header + first MaxBodyLines body rows +
            // one overflow-marker row + trailing gap row ([UX5] #265:
            // zero budget = no body, no marker).
            return BlockMeasure.Exact(2 + Math.Max(0, MaxBodyLines) + (MaxBodyLines > 0 ? 1 : 0));
        }

        return BlockMeasure.Exact(2 + Math.Max(1, lines.Length));
    }

    public int CheapEstimate(int width)
    {
        int body = BlockMath.EstimateLines(_text.Source, Math.Max(1, BodyWidth(width)));
        if (!IsExpanded && body > MaxBodyLines)
        {
            body = Math.Max(0, MaxBodyLines) + (MaxBodyLines > 0 ? 1 : 0);
        }

        return 2 + body;
    }

    public void Paint(in BlockPaintContext ctx)
    {
        _lastPaintRect = ctx.Rect;
        _lastSkipRows = ctx.SkipRows;

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
        var bodyStyle = new CellStyle(ChatPalette.UserText.Fg, ChatPalette.Surface, ChatPalette.UserText.Attrs);
        var bgCell = Cell.From(new Rune(' '), new CellStyle(bg: ChatPalette.Surface));

        var lines = _text.GetLines(bodyWidth);
        bool overflow = !IsExpanded && MaxBodyLines > 0 && lines.Length > MaxBodyLines;
        int hidden = overflow ? lines.Length - MaxBodyLines : 0;
        int totalRows = overflow ? MaxBodyLines + 3 : lines.Length + 2;
        for (int i = 0; i < rows && (skip + i) < totalRows; i++)
        {
            int row = skip + i;
            int paintY = y + i;
            if (row == totalRows - 1)
            {
                buffer.Fill(new Rect(ctx.Rect.X, paintY, ctx.Rect.Width, 1), in bgCell);

                // Panel chrome: with separators enabled this breathing room
                // becomes a thin dim divider between answers (same tone as
                // the CellForge PanelChrome separator — ChatPalette.Dim).
                // Off by default: direct block paints stay byte-identical.
                if (ctx.ShowSeparators)
                {
                    PaintSeparator(buffer, ctx.Rect.X, paintY, ctx.Rect.Width);
                }

                continue; // trailing gap row: breathing room between bubbles
            }

            buffer.Fill(new Rect(ctx.Rect.X, paintY, ctx.Rect.Width, 1), in bgCell);
            if (row == 0)
            {
                buffer.SetText(ctx.Rect.X + 1, paintY, Header, headerStyle);
                continue;
            }

            if (overflow && row == totalRows - 2)
            {
                // Collapsed overflow tail ([UX2] #262): dim "... (N hidden)"
                // on the row after the last shown body line; the bar keeps
                // the bubble rhythm, the gap row below stays untouched.
                string tail = ICollapsibleChatBlock.OverflowTail(hidden);
                buffer.SetText(ctx.Rect.X, paintY, "│", barStyle);
                int avail = Math.Max(0, ctx.Rect.Width - Gutter);
                buffer.SetText(ctx.Rect.X + Gutter, paintY,
                    tail.AsSpan(0, Math.Min(avail, tail.Length)), ChatPalette.Dim);
                continue;
            }

            buffer.SetText(ctx.Rect.X, paintY, "│", barStyle);
            buffer.SetText(ctx.Rect.X + Gutter, paintY, lines.Span[row - 1], bodyStyle);
        }
    }

    public string RawText() => Prefix + _text.Source;

    private static int BodyWidth(int rectWidth) => rectWidth - Gutter;

    /// <summary>
    /// Thin dim inter-message divider (panel chrome): a <c>─</c> hairline in
    /// <see cref="ChatPalette.Dim"/> — the same tone
    /// <c>Harbor.Tui.CellForge.Widgets.PanelChrome.SeparatorStyle</c> exposes
    /// (named here, not referenced: this assembly must not depend on CellForge).
    /// Bounds-safe: clips to the buffer, no-op on non-positive widths.
    /// </summary>
    internal static void PaintSeparator(ScreenBuffer buffer, int x, int y, int width)
    {
        if (width <= 0)
        {
            return;
        }

        var style = ChatPalette.Dim;
        var glyph = new Rune('─');
        for (int i = 0; i < width; i++)
        {
            buffer.SetRune(x + i, y, glyph, in style);
        }
    }
}

/// <summary>Dim italic system notice (session events, compaction, errors).
/// Collapsible per the <c>ICollapsibleChatBlock</c> mixin ([UX2] #262):
/// bodies over <see cref="MaxBodyLines"/> (universal 10) render collapsed
/// with a <c>"... (N hidden)"</c> tail; shorter notices paint fully, so the
/// default-collapsed state stays byte-identical to the pre-collapse layout.
/// Feed Enter/click/space toggles via <see cref="ToggleExpanded"/>.</summary>
public sealed class SystemBlock : ICollapsibleChatBlock
{
    private readonly WrappedText _text;

    public SystemBlock(string text) => _text = new WrappedText(text ?? string.Empty);

    public string Kind => "system";

    public bool IsStreamContinuation => false;

    public int BudgetBytes => 48 + (_text.SourceLength * 2);

    /// <summary>
    /// Collapsed-body line budget ([UX2] #262: universal 10-line policy).
    /// Collapse protocol lives on <c>ICollapsibleChatBlock</c> (PRIM1a #291);
    /// this property satisfies the mixin contract.
    /// </summary>
    public int MaxBodyLines { get; set; } = ICollapsibleChatBlock.DefaultUniversalBodyLines;

    /// <summary>
    /// Whether the block is expanded (feed Enter/click/space toggles via
    /// <see cref="ToggleExpanded"/>). Defaults to <c>false</c> so bodies over
    /// the budget collapse structurally — no ingestion changes needed; short
    /// notices paint fully either way, keeping existing goldens byte-identical.
    /// </summary>
    public bool IsExpanded { get; private set; }

    /// <summary>Flips <see cref="IsExpanded"/> (feed Enter/click/space path).</summary>
    public void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>Sets <see cref="IsExpanded"/> explicitly (host-driven focus path).</summary>
    public void SetExpanded(bool expanded) => IsExpanded = expanded;

    private Rect? _lastPaintRect;
    private int _lastSkipRows;

    /// <summary>
    /// Click hit-test for the first row (unified expand gesture, [UX2] #262;
    /// mirrors <c>ToolCallBlock.TryHitHeader</c> — system notices have no
    /// header, so the first body row claims the click).
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
        var lines = _text.GetLines(Math.Max(1, width));
        if (!IsExpanded && lines.Length > MaxBodyLines)
        {
            // Collapsed overflow: first MaxBodyLines rows + one
            // overflow-marker row ([UX5] #265: zero budget = no rows at all).
            return BlockMeasure.Exact(Math.Max(0, MaxBodyLines) + (MaxBodyLines > 0 ? 1 : 0));
        }

        return BlockMeasure.Exact(Math.Max(1, lines.Length));
    }

    public int CheapEstimate(int width)
    {
        int body = BlockMath.EstimateLines(_text.Source, Math.Max(1, width));
        if (!IsExpanded && body > MaxBodyLines)
        {
            body = Math.Max(0, MaxBodyLines) + (MaxBodyLines > 0 ? 1 : 0);
        }

        return body;
    }

    public void Paint(in BlockPaintContext ctx)
    {
        _lastPaintRect = ctx.Rect;
        _lastSkipRows = ctx.SkipRows;

        var buffer = ctx.Buffer;
        var lines = _text.GetLines(Math.Max(1, ctx.Rect.Width));
        bool overflow = !IsExpanded && MaxBodyLines > 0 && lines.Length > MaxBodyLines;
        int hidden = overflow ? lines.Length - MaxBodyLines : 0;
        int total = overflow ? MaxBodyLines + 1 : lines.Length;
        int rows = ctx.Rect.Height;
        int skip = ctx.SkipRows;
        for (int i = 0; i < rows && (skip + i) < total; i++)
        {
            int lineIdx = skip + i;
            if (overflow && lineIdx == MaxBodyLines)
            {
                string tail = ICollapsibleChatBlock.OverflowTail(hidden);
                buffer.SetText(ctx.Rect.X, ctx.Rect.Y + i,
                    tail.AsSpan(0, Math.Min(ctx.Rect.Width, tail.Length)), ChatPalette.Dim);
                continue;
            }

            buffer.SetText(ctx.Rect.X, ctx.Rect.Y + i, lines.Span[lineIdx], ChatPalette.System);
        }
    }

    public string RawText() => _text.Source;
}
