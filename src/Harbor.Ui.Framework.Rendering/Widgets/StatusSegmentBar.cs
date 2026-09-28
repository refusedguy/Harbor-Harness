using System.Buffers;
using System.Text;
using Harbor.Ui.Framework.Rendering;

namespace Harbor.Ui.Framework.Rendering.Widgets;

/// <summary>Color accent of a status segment.</summary>
public enum StatusAccent : byte
{
    Neutral = 0,
    Dim,
    Accent,
    Success,
    Warning,
    Error,
}

/// <summary>
/// One typed piece of the status bar (widgets §3.7). <see cref="FixedPriority"/>
/// marks segments that must survive truncation (model, mode hint); flexible
/// segments are cut from the right edge inward — tokens/cost sit rightmost,
/// so they die first, the context bar lives leftmost of the flexible run.
/// </summary>
public record struct StatusSeg(string Text, StatusAccent Accent, bool FixedPriority);

/// <summary>Footer machine modes (codex footer): mode decides the hint segment and spinner rhythm.</summary>
public enum StatusBarMode : byte
{
    Idle = 0,
    Running,
    AwaitingApproval,
    Compacting,
}

/// <summary>Display width of segment texts (wide-rune aware, delegates to the core width table).</summary>
internal static class SegWidth
{
    public static int Of(ReadOnlySpan<char> text) => UnicodeWidth.Width(text);

    /// <summary>
    /// ENG5: run texts are immutable and re-measured every frame, so the
    /// string path memoizes per thread (#487 — the table used to be
    /// process-global behind a lock). Same value, fewer rune decodes, and no
    /// cross-thread serialization on a per-frame path.
    /// Null mirrors the legacy span path (a null run measures 0).
    /// </summary>
    public static int Of(string? text) => text is null ? 0 : UnicodeWidth.WidthCached(text);
}

/// <summary>
/// Width-aware truncation over a segment span (widgets §3.7): drop flexible
/// segments right-to-left until the row fits, then hard-cut characters from
/// the widest surviving segment. Operates in place — zero allocations.
/// </summary>
public static class StatusBarLayout
{
    /// <summary>
    /// Segments up to this count pack on the stack. A status row is a handful of
    /// cells wide (CellForge composes at most 12), so the stack path is the only
    /// one the renderers ever take and the scratch table stays off the heap; the
    /// fallback exists so a caller passing a pathological span cannot ask for an
    /// unbounded <c>stackalloc</c>.
    /// </summary>
    private const int StackSegmentCapacity = 32;

    /// <summary>Mutates <paramref name="segs"/>, packing survivors left-to-right with single-space gaps.</summary>
    /// <returns>Number of surviving segments at the front of the span.</returns>
    public static int Fit(Span<StatusSeg> segs, int width)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);

        int count = segs.Length;
        if (count == 0)
        {
            return 0;
        }

        // #487: measure every segment exactly once, then carry the running total
        // and the per-segment widths. The old shape re-summed the whole row —
        // TotalWidth → SegWidth.Of → UnicodeWidth.WidthCached — once per dropped
        // victim, and each of those lookups was a process-global monitor
        // acquisition, so one painted status frame paid O(n²) of them under a
        // lock. Width lookups are now n whatever the outcome, and the table they
        // read is per-thread.
        Span<int> widths = count <= StackSegmentCapacity ? stackalloc int[count] : new int[count];
        int total = Measure(segs, widths);

        // Pass 1: drop the rightmost flexible segment while over budget. The
        // scan cursor only ever walks left — everything right of the victim was
        // fixed before the shift and stays fixed after it — so the pass costs
        // one visit per segment for the whole call, not one full scan per victim.
        int cursor = count - 1;
        while (total > width)
        {
            while (cursor >= 0 && segs[cursor].FixedPriority)
            {
                cursor--;
            }

            if (cursor < 0)
            {
                break;
            }

            int victim = cursor;
            total -= widths[victim] + (segs.Length > 1 ? 1 : 0);
            RemoveAt(ref segs, ref widths, victim);

            // Index `victim` now holds what used to be `victim + 1` — a fixed
            // segment, or past the end of a shorter span — so the rightmost
            // flexible one is strictly to its left. Walking the cursor back is
            // what makes this O(n) for the pass; leaving it put would re-test a
            // fixed segment and, at the right edge, read past the span.
            cursor = victim - 1;
        }

        // Pass 2: still over budget → character-cut the widest survivor by
        // exactly the excess; fixed segments are clamped to one cell minimum,
        // flexible ones disappear once their budget is gone. Truncation reports
        // the width it produced, so this loop re-measures nothing.
        while (total > width && segs.Length > 0)
        {
            int target = WidestIndex(widths);
            var s = segs[target];
            int keep = widths[target] - (total - width);
            if (keep <= 0)
            {
                if (!s.FixedPriority)
                {
                    total -= widths[target] + (segs.Length > 1 ? 1 : 0);
                    RemoveAt(ref segs, ref widths, target);
                    continue;
                }

                // Fixed: it keeps one cell of itself. A segment that is already
                // one cell wide has nothing left to give, and the old code
                // re-clamped it in place forever — two one-cell fixed segments at
                // width 1 spun here until the process was killed. #487 found it
                // while pinning the narrow-width sweep.
                keep = 1;
            }

            s.Text = Truncate(s.Text, keep, out int cut);
            if (cut >= widths[target])
            {
                // Only the clamp above lands here, and only when the row is
                // irreducible at this width: every survivor is now at most one
                // cell wide. Stop with the bar intact rather than loop.
                break;
            }

            segs[target] = s;
            total -= widths[target] - cut;
            widths[target] = cut;
        }

        return segs.Length;
    }

    /// <summary>Total row width including single-space gaps between segments.</summary>
    public static int TotalWidth(ReadOnlySpan<StatusSeg> segs)
    {
        if (segs.Length == 0)
        {
            return 0;
        }

        int total = SegWidth.Of(segs[0].Text);
        for (int i = 1; i < segs.Length; i++)
        {
            total += 1 + SegWidth.Of(segs[i].Text);
        }

        return total;
    }

    /// <summary>
    /// Fills <paramref name="widths"/> with each segment's display width and
    /// returns the row total (segments plus single-space gaps). This is the one
    /// place <see cref="Fit"/> measures; everything after it is integer work.
    /// </summary>
    private static int Measure(ReadOnlySpan<StatusSeg> segs, Span<int> widths)
    {
        int total = SegWidth.Of(segs[0].Text);
        widths[0] = total;
        for (int i = 1; i < segs.Length; i++)
        {
            int w = SegWidth.Of(segs[i].Text);
            widths[i] = w;
            total += 1 + w;
        }

        return total;
    }

    private static void RemoveAt(ref Span<StatusSeg> segs, ref Span<int> widths, int index)
    {
        for (int i = index; i < segs.Length - 1; i++)
        {
            segs[i] = segs[i + 1];
            widths[i] = widths[i + 1];
        }

        segs = segs[..^1];
        widths = widths[..^1];
    }

    /// <summary>
    /// Index of the widest survivor, read off the width table. This is the only
    /// per-victim scan left, and it runs over ints in a stack buffer — no
    /// measurement, no lock, no string compare.
    /// </summary>
    private static int WidestIndex(ReadOnlySpan<int> widths)
    {
        int best = 0;
        int bestWidth = -1;
        for (int i = 0; i < widths.Length; i++)
        {
            if (widths[i] > bestWidth)
            {
                bestWidth = widths[i];
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// Longest prefix of <paramref name="text"/> that fits in
    /// <paramref name="maxCells"/>, with its exact cell width in
    /// <paramref name="cells"/>. Reporting that width is what lets
    /// <see cref="Fit"/> keep its running total without measuring the cut text
    /// a second time: the loop already knows every rune it kept.
    /// </summary>
    private static string Truncate(string text, int maxCells, out int cells)
    {
        cells = 0;
        var slice = text.AsSpan();
        while (!slice.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(slice, out var rune, out int consumed) == OperationStatus.Done)
            {
                int w = UnicodeWidth.Width(rune);
                if (cells + w > maxCells)
                {
                    return text[..^slice.Length];
                }

                cells += w;
                slice = slice[consumed..];
            }
            else
            {
                // An unpaired surrogate: DecodeFromUtf16 reports 0 consumed, so
                // the original loop could never advance past one. Step over it
                // the way UnicodeWidth.Width does — the width table gives the
                // default rune 0 cells, so the total is unchanged.
                slice = slice[1..];
            }
        }

        return text;
    }
}
