using System.Collections.Generic;
using System.Text;
using CSharpFunctionalExtensions;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Collapsed read-only context-gathering run ([UX3] #263, opencode pattern):
/// consecutive <c>read</c>/<c>glob</c>/<c>grep</c>/<c>ls</c>/<c>tree</c>/
/// <c>ripgrep</c> cards collapse into one feed line
/// (<c>◇ gathered context · N read · M search</c>). Enter/click expands to one
/// summary row per member card (tool + args + outcome). Member
/// <see cref="ToolCallBlock"/> paint is untouched — this block only owns the
/// group header and the per-member rows. Coalescing lives in
/// <c>ToolCardTracker</c> (card-creation path); completed member outputs stay
/// on the members, full text remains in session history.
/// </summary>
public sealed class ReadGroupBlock : ICollapsibleChatBlock
{
    private const char GroupGlyph = '◇';
    private const char RunningGlyph = '⚙';
    private const char OkGlyph = '✔';
    private const char ErrorGlyph = '✖';

    /// <summary>Expanded per-member row budget (mirrors ToolCallBlock.ExpandedBodyLines).</summary>
    public const int MaxExpandedMembers = 20;

    private readonly List<ToolCallBlock> _members = new();

    // ENG10 #282: transition-computed counts fragment (" · 2 read · 1 search").
    // Membership only grows via AddMember, so the text is rebuilt there —
    // Paint only slices a span over it (no per-frame StringBuilder).
    private string _countsText = string.Empty;

    public ReadGroupBlock(string id)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        MaxBodyLines = 0; // collapsed group is always a pure one-liner (no body rows)
    }

    /// <summary>Stable group id (<c>readgroup-N</c>) for ToggleToolCard routing.</summary>
    public string Id { get; }

    /// <summary>Member cards in arrival order (completed or still running).</summary>
    public IReadOnlyList<ToolCallBlock> Members => _members;

    /// <summary>Read-only context tools coalesced by this group (opencode gather set).</summary>
    public static bool IsReadOnlyTool(string toolName) =>
        toolName is "read" or "glob" or "grep" or "ls" or "list" or "tree" or "ripgrep";

    internal void AddMember(ToolCallBlock member)
    {
        ArgumentNullException.ThrowIfNull(member);
        _members.Add(member);
        RebuildCountsText();
    }

    private void RebuildCountsText()
    {
        int reads = CountReads();
        int search = _members.Count - reads;
        var sb = new StringBuilder();
        if (reads > 0)
        {
            sb.Append(" · ");
            sb.Append(reads);
            sb.Append(" read");
        }

        if (search > 0)
        {
            sb.Append(" · ");
            sb.Append(search);
            sb.Append(" search");
        }

        _countsText = sb.ToString();
    }

    internal bool ContainsMember(string toolCallId)
    {
        for (int i = 0; i < _members.Count; i++)
        {
            if (string.Equals(_members[i].Info.Id, toolCallId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public string Kind => "read-group";

    public bool IsStreamContinuation => false;

    public int BudgetBytes
    {
        get
        {
            long total = 96;
            for (int i = 0; i < _members.Count; i++)
            {
                total += Math.Max(0, _members[i].BudgetBytes);
            }

            return total > int.MaxValue ? int.MaxValue : (int)total;
        }
    }

    /// <summary>
    /// Collapsed-body line budget (mixin contract). The collapsed group never
    /// shows body rows — always exactly the header line.
    /// </summary>
    public int MaxBodyLines { get; set; }

    public bool IsExpanded { get; private set; }

    /// <summary>Flips <see cref="IsExpanded"/> (feed Enter/click path).</summary>
    public void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>Sets <see cref="IsExpanded"/> explicitly (host-driven focus path).</summary>
    public void SetExpanded(bool expanded) => IsExpanded = expanded;

    /// <summary>
    /// Collapsed header (<c>◇ gathered context · 2 read · 1 search</c>);
    /// zero-count parts are omitted.
    /// </summary>
    public string HeaderText() => "◇ gathered context" + _countsText;

    private int CountReads()
    {
        int reads = 0;
        for (int i = 0; i < _members.Count; i++)
        {
            if (string.Equals(_members[i].Info.ToolName, "read", StringComparison.Ordinal))
            {
                reads++;
            }
        }

        return reads;
    }

    private int ExpandedRowCount()
    {
        int rows = Math.Min(_members.Count, MaxExpandedMembers);
        if (_members.Count > MaxExpandedMembers)
        {
            rows += 1; // overflow marker
        }

        return rows;
    }

    public BlockMeasure Measure(int width) =>
        BlockMeasure.Exact(1 + (IsExpanded ? ExpandedRowCount() : 0));

    public int CheapEstimate(int width) =>
        Math.Max(1, 1 + (IsExpanded ? ExpandedRowCount() : 0));

    private Rect? _lastPaintRect;
    private int _lastSkipRows;

    /// <summary>
    /// Click hit-test for the group header (mirrors
    /// <see cref="ToolCallBlock.TryHitHeader"/>): true when the group has
    /// painted and (<paramref name="col"/>, <paramref name="row"/>) lands on
    /// its header row.
    /// </summary>
    public bool TryHitHeader(int col, int row)
    {
        if (_lastPaintRect is not { } rect || _lastSkipRows != 0)
        {
            return false;
        }

        return row == rect.Y && col >= rect.X && col < rect.X + rect.Width;
    }

    public void Paint(in BlockPaintContext ctx)
    {
        _lastPaintRect = ctx.Rect;
        _lastSkipRows = ctx.SkipRows;

        var buffer = ctx.Buffer;
        int width = ctx.Rect.Width;
        if (width <= 0 || ctx.Rect.Y >= buffer.Rows)
        {
            return;
        }

        PaintHeader(buffer, ctx.Rect.X, ctx.Rect.Y, width);
        if (!IsExpanded)
        {
            return;
        }

        int y = ctx.Rect.Y + 1;
        int rows = ctx.Rect.Bottom - y;
        int upTo = Math.Min(_members.Count, MaxExpandedMembers);
        int shown = 0;
        for (int i = 0; i < upTo && shown < rows; i++, shown++)
        {
            PaintMemberRow(buffer, ctx.Rect.X, y + shown, width, _members[i]);
        }

        if (_members.Count > MaxExpandedMembers && shown < rows)
        {
            // ENG10 #282: segment paints — identical cells to the former
            // $"… +{n} more" run, no per-frame interpolation or substring.
            PaintOverflowMarker(buffer, ctx.Rect.X, y + shown, _members.Count - MaxExpandedMembers);
        }
    }

    private static void PaintOverflowMarker(ScreenBuffer buffer, int x, int y, int overflow)
    {
        int avail = Math.Max(0, buffer.Cols - x);
        if (avail <= 0 || y >= buffer.Rows)
        {
            return;
        }

        Span<char> digits = stackalloc char[11];
        overflow.TryFormat(digits, out int written);
        var head = "… +".AsSpan();
        var tail = " more".AsSpan();
        int cursor = x;
        int end = x + avail;
        PaintRun(buffer, ref cursor, end, y, head, ChatPalette.Dim);
        PaintRun(buffer, ref cursor, end, y, digits.Slice(0, written), ChatPalette.Dim);
        PaintRun(buffer, ref cursor, end, y, tail, ChatPalette.Dim);
    }

    private static void PaintRun(ScreenBuffer buffer, ref int cursor, int end, int y, ReadOnlySpan<char> run, CellStyle style)
    {
        if (cursor >= end || run.IsEmpty)
        {
            return;
        }

        int shown = Math.Min(end - cursor, run.Length);
        buffer.SetText(cursor, y, run.Slice(0, shown), style);
        cursor += shown;
    }

    private void PaintHeader(ScreenBuffer buffer, int x, int y, int width)
    {
        bool anyRunning = false;
        for (int i = 0; i < _members.Count; i++)
        {
            if (_members[i].Body.HasNoValue)
            {
                anyRunning = true;
                break;
            }
        }

        var glyphStyle = anyRunning ? ChatPalette.ToolRunning : ChatPalette.ToolOk;
        buffer.SetText(x, y, [GroupGlyph], glyphStyle);
        int cursor = x + 1;
        if (cursor >= x + width)
        {
            return;
        }

        buffer.SetText(cursor, y, " ", CellStyle.Plain);
        cursor++;
        const string label = "gathered context";
        buffer.SetText(cursor, y, label, ChatPalette.ToolName);
        cursor += label.Length;

        // ENG10 #282: transition-cached counts fragment, span slice —
        // no per-frame StringBuilder. Identical cells to the former
        // counts.ToString() run.
        if (_countsText.Length > 0)
        {
            int avail = (x + width) - cursor;
            if (avail > 0)
            {
                var span = _countsText.AsSpan(0, Math.Min(avail, _countsText.Length));
                buffer.SetText(cursor, y, span, ChatPalette.Dim);
            }
        }
    }

    private static void PaintMemberRow(ScreenBuffer buffer, int x, int y, int width, ToolCallBlock member)
    {
        if (width <= 0 || y >= buffer.Rows)
        {
            return;
        }

        Maybe<ToolResultBody> body = member.Body;
        bool done = body.HasValue;
        char glyph = !done ? RunningGlyph : (body.Value.IsError ? ErrorGlyph : OkGlyph);
        var glyphStyle = !done
            ? ChatPalette.ToolRunning
            : (body.Value.IsError ? ChatPalette.ToolError : ChatPalette.ToolOk);

        buffer.SetText(x, y, [glyph], glyphStyle);
        int cursor = x + 1;
        if (cursor >= x + width)
        {
            return;
        }

        buffer.SetText(cursor, y, " ", CellStyle.Plain);
        cursor++;

        string tool = member.Info.ToolName;
        buffer.SetText(cursor, y, tool, ChatPalette.ToolName);
        cursor += tool.Length;

        // ENG10 #282: segment paints over member state + the transition-cached
        // OutcomeText — no per-frame StringBuilder. Identical cells to the
        // former ' ' + args + " → " + ("ok "/"error ") + FormatDuration run.
        int end = x + width;
        var detailStyle = done && body.Value.IsError ? ChatPalette.ToolError : ChatPalette.ToolArgs;
        if (!string.IsNullOrEmpty(member.Info.ArgsSummary))
        {
            PaintRun(buffer, ref cursor, end, y, " ", detailStyle);
            PaintRun(buffer, ref cursor, end, y, member.Info.ArgsSummary.AsSpan(), detailStyle);
        }

        if (done && member.OutcomeText is { Length: > 0 } outcome)
        {
            PaintRun(buffer, ref cursor, end, y, " → ", detailStyle);
            PaintRun(buffer, ref cursor, end, y, outcome.AsSpan(), detailStyle);
        }
    }

    public string RawText()
    {
        var sb = new StringBuilder(HeaderText());
        for (int i = 0; i < _members.Count; i++)
        {
            sb.Append('\n');
            sb.Append(_members[i].RawText());
        }

        return sb.ToString();
    }
}
