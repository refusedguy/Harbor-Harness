using Harbor.Tui.ConsoleEx.Rendering;

namespace Harbor.Tui.ConsoleEx.Widgets;

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
}

/// <summary>
/// Width-aware truncation over a segment span (widgets §3.7): drop flexible
/// segments right-to-left until the row fits, then hard-cut characters from
/// the widest surviving segment. Operates in place — zero allocations.
/// </summary>
public static class StatusBarLayout
{
    /// <summary>Mutates <paramref name="segs"/>, packing survivors left-to-right with single-space gaps.</summary>
    /// <returns>Number of surviving segments at the front of the span.</returns>
    public static int Fit(Span<StatusSeg> segs, int width)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);

        // Pass 1: drop the rightmost flexible segment while over budget.
        while (TotalWidth(segs) > width)
        {
            int victim = LastFlexibleIndex(segs);
            if (victim < 0)
            {
                break;
            }

            RemoveAt(ref segs, victim);
        }

        // Pass 2: still over budget → character-cut the widest survivor by
        // exactly the excess; fixed segments are clamped to one cell minimum,
        // flexible ones disappear once their budget is gone.
        while (TotalWidth(segs) > width && segs.Length > 0)
        {
            int target = WidestIndex(segs);
            var s = segs[target];
            int excess = TotalWidth(segs) - width;
            int keep = SegWidth.Of(s.Text) - excess;
            if (keep <= 0)
            {
                if (s.FixedPriority)
                {
                    s.Text = Truncate(s.Text, 1);
                    segs[target] = s;
                }
                else
                {
                    RemoveAt(ref segs, target);
                }

                continue;
            }

            s.Text = Truncate(s.Text, keep);
            segs[target] = s;
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

    private static int LastFlexibleIndex(ReadOnlySpan<StatusSeg> segs)
    {
        for (int i = segs.Length - 1; i >= 0; i--)
        {
            if (!segs[i].FixedPriority)
            {
                return i;
            }
        }

        return -1;
    }

    private static void RemoveAt(ref Span<StatusSeg> segs, int index)
    {
        for (int i = index; i < segs.Length - 1; i++)
        {
            segs[i] = segs[i + 1];
        }

        segs = segs[..^1];
    }

    private static int WidestIndex(ReadOnlySpan<StatusSeg> segs)
    {
        int best = 0;
        int bestW = -1;
        for (int i = 0; i < segs.Length; i++)
        {
            int w = SegWidth.Of(segs[i].Text);
            if (w > bestW)
            {
                bestW = w;
                best = i;
            }
        }

        return best;
    }

    private static string Truncate(string text, int maxCells)
    {
        int cells = 0;
        var slice = text.AsSpan();
        while (!slice.IsEmpty)
        {
            System.Text.Rune.DecodeFromUtf16(slice, out var rune, out int consumed);
            int w = UnicodeWidth.Width(rune);
            if (cells + w > maxCells)
            {
                return text[..^slice.Length];
            }

            cells += w;
            slice = slice[consumed..];
        }

        return text;
    }
}
