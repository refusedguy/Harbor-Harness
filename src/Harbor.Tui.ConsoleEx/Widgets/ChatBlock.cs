using Harbor.Tui.ConsoleEx.Rendering;

namespace Harbor.Tui.ConsoleEx.Widgets;

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
    public BlockPaintContext(ScreenBuffer buffer, Rect rect, long tick)
    {
        Buffer = buffer;
        Rect = rect;
        Tick = tick;
    }

    public ScreenBuffer Buffer { get; }

    /// <summary>Clip region inside the buffer; X/Width give the text column span.</summary>
    public Rect Rect { get; }

    /// <summary>Monotonic frame tick from the render pipeline — no timers.</summary>
    public long Tick { get; }
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
