using Harbor.Ui.Framework.Rendering;

namespace Harbor.Ui.Framework.Rendering.Widgets;

/// <summary>
/// Height report for a chat block at a given width (widgets §3.1): exact when
/// the block measured its final wrapped lines, an estimate otherwise (stream
/// tails). <see cref="BestGuess"/> is what layout caches store before settle.
/// </summary>
public readonly record struct BlockMeasure(int MinLines, int MaxLines, bool IsExact)
{
    public static BlockMeasure Exact(int lines) => new(lines, lines, true);

    public static BlockMeasure Estimate(int min, int max) => new(min, max, false);

    /// <summary>Single-line floor guard; exact measures pass through.</summary>
    public int BestGuess => Math.Max(1, IsExact ? MinLines : (MinLines + MaxLines) / 2);
}

/// <summary>
/// Paint input for a chat block: a clip region of the BACK buffer plus the
/// frame tick (spinner blocks animate off it). Blocks own nothing outside the
/// rect and paint only rows they declared in <see cref="IChatBlock.Measure"/>.
/// </summary>
public readonly struct BlockPaintContext
{
    public BlockPaintContext(ScreenBuffer buffer, Rect rect, long tick, int skipRows = 0, bool showSeparators = false)
    {
        Buffer = buffer;
        Rect = rect;
        Tick = tick;
        SkipRows = Math.Max(0, skipRows);
        ShowSeparators = showSeparators;
    }

    public ScreenBuffer Buffer { get; }

    /// <summary>Clip region inside the buffer; X/Width give the text column span.</summary>
    public Rect Rect { get; }

    /// <summary>Monotonic frame tick from the render pipeline — no timers.</summary>
    public long Tick { get; }

    /// <summary>Rows to skip from the top of the block before painting (partial scroll).</summary>
    public int SkipRows { get; }

    /// <summary>
    /// Panel-chrome switch (feed zone separation): when true, message blocks
    /// with a trailing gap row paint a thin dim separator line in it instead
    /// of leaving it blank, so consecutive answers stop blending into each
    /// other. False by default — block-level goldens paint blocks directly
    /// and stay byte-identical; the host enables it per timeline (see
    /// <c>VirtualizedChatTimeline.ShowSeparators</c>).
    /// </summary>
    public bool ShowSeparators { get; }
}

/// <summary>
/// One typed cell of the chat timeline (widgets §3.1): measure + paint over
/// the cell grid instead of string concatenation. Blocks are immutable in
/// steady state except explicitly mutable cards (<see cref="ToolCallBlock"/>),
/// whose owner marks the timeline slot dirty after mutation.
/// </summary>
public interface IChatBlock
{
    /// <summary>Stable kind tag ("user", "assistant", "tool-call", "system", ...).</summary>
    string Kind { get; }

    /// <summary>True while the block is the live streaming tail (codex is_stream_continuation).</summary>
    bool IsStreamContinuation { get; }

    /// <summary>Rough resident size used by <see cref="TimelineRing"/> eviction (UTF-16 bytes + overhead).</summary>
    int BudgetBytes { get; }

    /// <summary>Height in rows for <paramref name="width"/> columns. Pure and cacheable.</summary>
    BlockMeasure Measure(int width);

    /// <summary>
    /// O(length) arithmetic guess used for off-screen layout (grok cheap
    /// estimate): never wraps, never renders, never allocates. Only
    /// <see cref="Measure"/> may produce authoritative heights.
    /// </summary>
    int CheapEstimate(int width);

    /// <summary>Paints into the clip rect. Must stay within previously measured bounds.</summary>
    void Paint(in BlockPaintContext ctx);

    /// <summary>Copy-friendly plain text (codex raw_lines).</summary>
    string RawText();
}

/// <summary>
/// Collapse mixin for <see cref="IChatBlock"/> implementers (PRIM1a, issue #291;
/// parent #286): the canonical home of the collapse protocol extracted verbatim
/// from <c>ToolCallBlock</c> — collapsed line budget (<see cref="MaxBodyLines"/>),
/// expand state (<see cref="IsExpanded"/> / <see cref="SetExpanded"/> /
/// <see cref="ToggleExpanded"/>) and truncated body paint
/// (<see cref="PaintBodyLines"/>).
/// Byte-identical semantics: <c>ToolCallBlock</c> delegates to these members,
/// so existing block goldens stay green. Foundation for the sibling collapse
/// slices (assistant markdown, timeline blocks, task card) — keep this API
/// stable and documented.
/// Geometry lives here; colors stay caller-side (renderer palette passed in),
/// so this assembly keeps its BCL-only / AOT-clean contract.
/// </summary>
public interface ICollapsibleChatBlock : IChatBlock
{
    /// <summary>Collapsed-body line budget (continuation marker when exceeded).</summary>
    int MaxBodyLines { get; set; }

    /// <summary>Whether the card is expanded (feed Enter/click toggles).</summary>
    bool IsExpanded { get; }

    /// <summary>Sets <see cref="IsExpanded"/> explicitly (host-driven focus path).</summary>
    void SetExpanded(bool expanded);

    /// <summary>Flips <see cref="IsExpanded"/> (feed Enter/click path).</summary>
    void ToggleExpanded() => SetExpanded(!IsExpanded);

    /// <summary>Default collapsed-body budget (the pre-mixin ToolCallBlock value).</summary>
    static int DefaultCollapsedBodyLines => 4;

    /// <summary>
    /// Universal collapsed-body budget ([UX2] #262): every body over this many
    /// lines renders collapsed with an overflow tail; shorter bodies paint
    /// fully (no marker), so default-collapsed blocks stay byte-identical
    /// until they exceed the budget. Tool/thinking cards keep their tighter
    /// <see cref="DefaultCollapsedBodyLines"/> (4) from the pre-mixin layout.
    /// </summary>
    static int DefaultUniversalBodyLines => 10;

    /// <summary>Default expanded-result budget (the pre-mixin ToolCallBlock value).</summary>
    static int DefaultExpandedBodyLines => 20;

    /// <summary>
    /// Overflow tail for newly-collapsible blocks ([UX2] #262):
    /// <c>"... (N hidden)"</c> carrying the cut line count. Blocks collapsed
    /// by the earlier slices (tool/thinking/assistant) keep their single
    /// <c>…</c> marker (pinned by their tests) — unifying the two tails is a
    /// later-slice decision, not this one.
    /// </summary>
    static string OverflowTail(int hiddenLines) => $"... ({Math.Max(0, hiddenLines)} hidden)";

    /// <summary>
    /// Click hit-test for the unified expand gesture ([UX2] #262): true when
    /// the block has painted and (<paramref name="col"/>, <paramref name="row"/>)
    /// lands on its header row. Screen-cell coordinates. Default false —
    /// blocks that never painted never claim clicks.
    /// </summary>
    bool TryHitHeader(int col, int row) => false;

    /// <summary>Counts logical (<c>'\n'</c>-separated) lines in <paramref name="text"/>.</summary>
    static int CountLogicalLines(ReadOnlySpan<char> text)
    {
        int count = 1;
        foreach (char c in text)
        {
            if (c == '\n')
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Visible body rows for <paramref name="trimmedOutput"/> (already
    /// <c>TrimEnd('\n')</c>'d) under <paramref name="budget"/>: capped lines
    /// plus one continuation-marker row on overflow.
    /// </summary>
    static int ClampedBodyLineCount(ReadOnlySpan<char> trimmedOutput, int budget)
    {
        // [UX5] #265: zero collapse budget = pure one-liner, not even a marker.
        if (budget <= 0)
        {
            return 0;
        }

        if (trimmedOutput.IsEmpty)
        {
            return 0;
        }

        int logical = CountLogicalLines(trimmedOutput);
        return Math.Min(logical, budget) + (logical > budget ? 1 : 0);
    }

    /// <summary>
    /// Paints up to <paramref name="maxLines"/> (and <paramref name="rows"/>
    /// viewport rows) of <paramref name="output"/> starting at
    /// (<paramref name="x"/>, <paramref name="y"/>), then a trailing
    /// <c>…</c> overflow marker when the budget or the viewport cut content.
    /// Long lines are hard-truncated to the buffer width (no wrapping).
    /// </summary>
    static void PaintBodyLines(
        ScreenBuffer buffer,
        int x,
        int y,
        int rows,
        ReadOnlySpan<char> output,
        int maxLines,
        CellStyle bodyStyle,
        CellStyle overflowStyle)
    {
        // [UX5] #265: zero collapse budget = pure one-liner, not even a marker.
        if (maxLines <= 0)
        {
            return;
        }

        var trimmed = output.TrimEnd('\n');
        if (trimmed.IsEmpty || rows <= 0)
        {
            return;
        }

        int shown = 0;
        int cursorY = y;
        var rest = trimmed;
        while (!rest.IsEmpty && shown < maxLines && shown < rows)
        {
            int nl = rest.IndexOf('\n');
            var line = nl < 0 ? rest : rest[..nl];
            rest = nl < 0 ? default : rest[(nl + 1)..];

            int avail = Math.Max(0, buffer.Cols - x);
            if (line.Length > avail)
            {
                line = line[..avail];
            }

            buffer.SetText(x, cursorY, line, bodyStyle);
            cursorY++;
            shown++;
        }

        // Continuation marker when both the collapse budget and truncation cut content.
        bool moreLines = !rest.IsEmpty;
        bool moreCols = trimmed.Length > 0 && CountLogicalLines(trimmed) > shown;
        if ((moreLines || moreCols) && shown < rows)
        {
            buffer.SetText(x, cursorY, "…", overflowStyle);
        }
    }
}
