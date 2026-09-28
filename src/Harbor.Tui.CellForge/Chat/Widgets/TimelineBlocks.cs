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
/// re-renders it with dim+italic styling on every layout pass.
/// ENG11 #283 (crush stable-prefix): only newline-terminated logical lines
/// are stable — the final partial line is the sole re-wrap per frame — and an
/// incremental FNV-1a content hash short-circuits frames with no new text, so
/// steady-state Measure/Paint is O(1) instead of O(document).
/// Collapsible per the <c>ICollapsibleChatBlock</c> mixin (PRIM1c #293):
/// collapsed paint shows the first <see cref="MaxBodyLines"/> wrapped lines
/// plus a <c>…</c> overflow marker.</summary>
public sealed class StreamingThinkingBlock : ICollapsibleChatBlock
{
    private const ulong FnvOffsetBasis = 14695981039346656037ul;
    private const ulong FnvPrime = 1099511628211ul;

    private readonly StringBuilder _text = new();
    private readonly List<string> _stable = [];
    private string[] _tail = [];
    private int _width = -1;
    private int _stableSourceChars;
    private int _lastNewlinePos = -1;
    private ulong _contentHash = FnvOffsetBasis;
    private ulong _wrappedHash = FnvOffsetBasis;
    private int _wrappedLength;

    public string Kind => "thinking";

    public bool IsStreamContinuation => true;

    public int BudgetBytes => 48 + (_text.Length * 2);

    /// <summary>Incremental FNV-1a hash of the accumulated text (crush pattern).</summary>
    public ulong ContentHash => _contentHash;

    /// <summary>
    /// Collapsed-body line budget (continuation marker when exceeded).
    /// Collapse protocol lives on <c>ICollapsibleChatBlock</c> (PRIM1a #291);
    /// this property satisfies the mixin contract.
    /// </summary>
    public int MaxBodyLines { get; set; } = CollapsedBodyLines;

    /// <summary>
    /// [UX4] #264: default collapsed budget for thinking — the crush
    /// 10-line box. Short blocks (≤10 wrapped lines) stay byte-identical
    /// to the pre-collapse layout.
    /// </summary>
    public const int CollapsedBodyLines = 10;

    /// <summary>
    /// Expanded body line budget (overflow marker when exceeded).
    /// Mirrors <c>ICollapsibleChatBlock.DefaultExpandedBodyLines</c>.
    /// </summary>
    public const int ExpandedBodyLines = 20;

    /// <summary>
    /// Whether the block is expanded (feed Enter/click toggles via
    /// <see cref="ToggleExpanded"/>). Satisfies the <c>ICollapsibleChatBlock</c>
    /// mixin contract. Defaults to <c>false</c> ([UX4] #264
    /// default-collapsed policy: thinking never steals feed height).
    /// </summary>
    public bool IsExpanded { get; private set; }

    /// <summary>
    /// [UX4] #264: third expand stage (collapsed → expanded → full).
    /// True only after <see cref="CycleExpand"/> passes the 20-line
    /// expanded budget; <see cref="ToggleExpanded"/> /
    /// <see cref="SetExpanded"/> never set it (2-stage gesture compat —
    /// gesture policy itself is UX2 scope and untouched).
    /// </summary>
    public bool IsFullyExpanded { get; private set; }

    /// <summary>Flips <see cref="IsExpanded"/> (feed Enter/click path).</summary>
    public void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
        IsFullyExpanded = false;
    }

    /// <summary>Sets <see cref="IsExpanded"/> explicitly (host-driven focus path).</summary>
    public void SetExpanded(bool expanded)
    {
        IsExpanded = expanded;
        if (!expanded)
        {
            IsFullyExpanded = false;
        }
    }

    /// <summary>
    /// [UX4] #264: 3-stage expand cycle for thinking (crush pattern) —
    /// collapsed (10 + <c>…</c>) → expanded (20 + <c>…</c>) → full →
    /// collapsed. Block-side state machine only; the feed gesture keeps
    /// calling <see cref="ToggleExpanded"/> (UX2 scope).
    /// </summary>
    public void CycleExpand()
    {
        if (!IsExpanded)
        {
            IsExpanded = true;
            IsFullyExpanded = false;
        }
        else if (!IsFullyExpanded)
        {
            IsFullyExpanded = true;
        }
        else
        {
            IsExpanded = false;
            IsFullyExpanded = false;
        }
    }

    /// <summary>
    /// Effective body budget for the current expand stage:
    /// <see cref="MaxBodyLines"/> collapsed, <see cref="ExpandedBodyLines"/>
    /// expanded, unbounded when fully expanded.
    /// </summary>
    private int EffectiveBudget =>
        !IsExpanded ? MaxBodyLines : IsFullyExpanded ? int.MaxValue : ExpandedBodyLines;

    private Rect? _lastPaintRect;
    private int _lastSkipRows;

    /// <summary>
    /// Click hit-test for the first row (unified expand gesture, [UX2] #262;
    /// mirrors <c>ToolCallBlock.TryHitHeader</c> — thinking blocks have no
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

    public void Append(string delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        int baseLen = _text.Length;
        _text.Append(delta);
        for (int i = 0; i < delta.Length; i++)
        {
            char c = delta[i];
            _contentHash = (_contentHash ^ (ulong)c) * FnvPrime;
            if (c == '\n')
            {
                _lastNewlinePos = baseLen + i;
            }
        }
    }

    public BlockMeasure Measure(int width)
    {
        EnsureWrapped(width);
        int total = _stable.Count + _tail.Length;
        int budget = EffectiveBudget;
        int shown = budget == int.MaxValue ? total : budget <= 0 ? 0 : Math.Min(total, budget);
        int visible = shown + (budget != int.MaxValue && budget > 0 && total > budget ? 1 : 0);
        return BlockMeasure.Exact(Math.Max(1, visible));
    }

    public int CheapEstimate(int width)
    {
        width = Math.Max(1, width);
        int total = 0;
        int run = 0;
        foreach (var chunk in _text.GetChunks())
        {
            var span = chunk.Span;
            for (int i = 0; i < span.Length; i++)
            {
                if (span[i] == '\n')
                {
                    total += Math.Max(1, (run + width - 1) / width);
                    run = 0;
                    continue;
                }

                run++;
            }
        }

        total += Math.Max(1, (run + width - 1) / width);

        // [UX4] #264: mirror Measure — clamp the off-screen estimate to the
        // current expand-stage budget (plus the overflow-marker row), so the
        // layout cache agrees with the painted height.
        return Math.Max(1, ClampLineCount(total));
    }

    public void Paint(in BlockPaintContext ctx)
    {
        _lastPaintRect = ctx.Rect;
        _lastSkipRows = ctx.SkipRows;

        EnsureWrapped(ctx.Rect.Width);
        var buffer = ctx.Buffer;
        var style = new CellStyle(attrs: StyleAttr.Dim | StyleAttr.Italic);
        int total = _stable.Count + _tail.Length;
        int budget = EffectiveBudget;
        int shown = budget == int.MaxValue ? total : budget <= 0 ? 0 : Math.Min(total, budget);
        int visible = shown + (budget != int.MaxValue && budget > 0 && total > budget ? 1 : 0);
        int rows = ctx.Rect.Height;
        int skip = ctx.SkipRows;
        for (int i = 0; i < visible && (skip + i) < visible && i < rows; i++)
        {
            int idx = skip + i;
            string line = idx < shown
                ? (idx < _stable.Count ? _stable[idx] : _tail[idx - _stable.Count])
                : "…";
            buffer.SetText(ctx.Rect.X, ctx.Rect.Y + i, line, style);
        }
    }

    public string RawText() => _text.ToString();

    private void EnsureWrapped(int width)
    {
        width = Math.Max(1, width);
        if (width == _width && _wrappedLength == _text.Length && _wrappedHash == _contentHash)
        {
            return; // steady state: hash proves nothing changed — O(1)
        }

        if (width != _width)
        {
            _stable.Clear();
            _stableSourceChars = 0;
            _width = width;
        }

        int stableEnd = _lastNewlinePos + 1;
        if (stableEnd > _text.Length)
        {
            stableEnd = _text.Length;
        }

        // Wrap only newly-completed logical lines; earlier rows are immutable.
        int segStart = _stableSourceChars;
        for (int i = _stableSourceChars; i < stableEnd; i++)
        {
            if (_text[i] == '\n')
            {
                WrapLogical(segStart, i - segStart);
                segStart = i + 1;
            }
        }

        _stableSourceChars = stableEnd;

        if (stableEnd < _text.Length)
        {
            var tail = new List<string>(Math.Max(1, _tail.Length));
            TextWrap.WrapTo(_text.ToString(stableEnd, _text.Length - stableEnd).AsSpan(), width, tail);
            _tail = [.. tail];
        }
        else
        {
            // Mirror WrapDocument exactly: the segment after a trailing
            // newline is one empty row; empty text paints no rows (Measure
            // still reports MinLines=1).
            _tail = _text.Length > 0 ? [""] : [];
        }

        _wrappedLength = _text.Length;
        _wrappedHash = _contentHash;
    }

    private void WrapLogical(int start, int length)
    {
        if (length <= 0)
        {
            _stable.Add(string.Empty);
            return;
        }

        var rows = new List<string>(1);
        TextWrap.WrapTo(_text.ToString(start, length).AsSpan(), Math.Max(1, _width), rows);
        _stable.AddRange(rows);
    }

    /// <summary>
    /// Visible rows for <paramref name="total"/> wrapped lines under the
    /// current collapse budget: capped lines plus one continuation-marker row
    /// on overflow ([UX5] #265: zero budget shows nothing, not even a marker).
    /// </summary>
    private int VisibleLineCount(int total) => ClampLineCount(total);

    private int ClampLineCount(int total)
    {
        int budget = EffectiveBudget;
        if (budget == int.MaxValue)
        {
            return total;
        }

        if (budget <= 0)
        {
            return 0;
        }

        return Math.Min(total, budget) + (total > budget ? 1 : 0);
    }
}

/// <summary>Finalized thinking block: renders committed reasoning text with
/// dim+italic styling, wrapped to the available width.
/// Collapsible per the <c>ICollapsibleChatBlock</c> mixin (PRIM1c #293,
/// parent #286): collapsed paint shows the first <see cref="MaxBodyLines"/>
/// wrapped lines plus a <c>…</c> overflow marker; short blocks stay
/// byte-identical to the pre-collapse layout.</summary>
public sealed class ThinkingBlock : ICollapsibleChatBlock
{
    private readonly WrappedText _text;

    public ThinkingBlock(string text) => _text = new WrappedText(text ?? string.Empty);

    public string Kind => "thinking";

    public bool IsStreamContinuation => false;

    public int BudgetBytes => 48 + (_text.SourceLength * 2);

    /// <summary>
    /// Collapsed-body line budget (continuation marker when exceeded).
    /// Collapse protocol lives on <c>ICollapsibleChatBlock</c> (PRIM1a #291);
    /// this property satisfies the mixin contract.
    /// </summary>
    public int MaxBodyLines { get; set; } = CollapsedBodyLines;

    /// <summary>
    /// [UX4] #264: default collapsed budget for thinking — the crush
    /// 10-line box. Short blocks (≤10 wrapped lines) stay byte-identical
    /// to the pre-collapse layout.
    /// </summary>
    public const int CollapsedBodyLines = 10;

    /// <summary>
    /// Expanded body line budget (overflow marker when exceeded).
    /// Mirrors <c>ICollapsibleChatBlock.DefaultExpandedBodyLines</c>.
    /// </summary>
    public const int ExpandedBodyLines = 20;

    /// <summary>
    /// Whether the block is expanded (feed Enter/click toggles via
    /// <see cref="ToggleExpanded"/>). Satisfies the <c>ICollapsibleChatBlock</c>
    /// mixin contract. Defaults to <c>false</c> ([UX4] #264
    /// default-collapsed policy: finalized reasoning never steals feed height).
    /// </summary>
    public bool IsExpanded { get; private set; }

    /// <summary>
    /// [UX4] #264: third expand stage (collapsed → expanded → full).
    /// True only after <see cref="CycleExpand"/> passes the 20-line
    /// expanded budget; <see cref="ToggleExpanded"/> /
    /// <see cref="SetExpanded"/> never set it (2-stage gesture compat —
    /// gesture policy itself is UX2 scope and untouched).
    /// </summary>
    public bool IsFullyExpanded { get; private set; }

    /// <summary>Flips <see cref="IsExpanded"/> (feed Enter/click path).</summary>
    public void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
        IsFullyExpanded = false;
    }

    /// <summary>Sets <see cref="IsExpanded"/> explicitly (host-driven focus path).</summary>
    public void SetExpanded(bool expanded)
    {
        IsExpanded = expanded;
        if (!expanded)
        {
            IsFullyExpanded = false;
        }
    }

    /// <summary>
    /// [UX4] #264: 3-stage expand cycle for thinking (crush pattern) —
    /// collapsed (10 + <c>…</c>) → expanded (20 + <c>…</c>) → full →
    /// collapsed. Block-side state machine only; the feed gesture keeps
    /// calling <see cref="ToggleExpanded"/> (UX2 scope).
    /// </summary>
    public void CycleExpand()
    {
        if (!IsExpanded)
        {
            IsExpanded = true;
            IsFullyExpanded = false;
        }
        else if (!IsFullyExpanded)
        {
            IsFullyExpanded = true;
        }
        else
        {
            IsExpanded = false;
            IsFullyExpanded = false;
        }
    }

    /// <summary>
    /// Effective body budget for the current expand stage:
    /// <see cref="MaxBodyLines"/> collapsed, <see cref="ExpandedBodyLines"/>
    /// expanded, unbounded when fully expanded.
    /// </summary>
    private int EffectiveBudget =>
        !IsExpanded ? MaxBodyLines : IsFullyExpanded ? int.MaxValue : ExpandedBodyLines;

    private Rect? _lastPaintRect;
    private int _lastSkipRows;

    /// <summary>
    /// Click hit-test for the first row (unified expand gesture, [UX2] #262;
    /// mirrors <c>ToolCallBlock.TryHitHeader</c> — thinking blocks have no
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

    public BlockMeasure Measure(int width) =>
        BlockMeasure.Exact(Math.Max(1, ClampLineCount(_text.GetLines(Math.Max(1, width)).Length)));

    public int CheapEstimate(int width) =>
        Math.Max(1, ClampLineCount(BlockMath.EstimateLines(_text.Source, Math.Max(1, width))));

    public void Paint(in BlockPaintContext ctx)
    {
        _lastPaintRect = ctx.Rect;
        _lastSkipRows = ctx.SkipRows;

        var buffer = ctx.Buffer;
        var lines = _text.GetLines(Math.Max(1, ctx.Rect.Width));
        var style = new CellStyle(attrs: StyleAttr.Dim | StyleAttr.Italic);
        int total = lines.Length;
        int budget = EffectiveBudget;
        int shown = budget == int.MaxValue ? total : budget <= 0 ? 0 : Math.Min(total, budget);
        int visible = shown + (budget != int.MaxValue && budget > 0 && total > budget ? 1 : 0);
        int rows = ctx.Rect.Height;
        int skip = ctx.SkipRows;
        for (int i = 0; i < visible && (skip + i) < visible && i < rows; i++)
        {
            int index = skip + i;
            buffer.SetText(ctx.Rect.X, ctx.Rect.Y + i, index < shown ? lines.Span[index] : "…", style);
        }
    }

    public string RawText() => _text.Source;

    /// <summary>
    /// Visible rows for <paramref name="total"/> wrapped lines under the
    /// current collapse budget: capped lines plus one continuation-marker row
    /// on overflow ([UX5] #265: zero budget shows nothing, not even a marker).
    /// </summary>
    private int ClampLineCount(int total)
    {
        int budget = EffectiveBudget;
        if (budget == int.MaxValue)
        {
            return total;
        }

        if (budget <= 0)
        {
            return 0;
        }

        return Math.Min(total, budget) + (total > budget ? 1 : 0);
    }
}
