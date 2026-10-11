namespace Harbor.Ui.Framework.Projection;

/// <summary>
///     Single home for status-segment ordering: left → center → right, each
///     group importance-ascending except right (descending). Moved out of the
///     per-renderer views (Termina / TerminalGui <c>StatusBarView</c>,
///     SpectreTui <c>ChatChromeView</c>) and
///     <see cref="StatusProjector.ProjectFooter"/> so every backend sorts
///     identically and cannot drift apart.
/// </summary>
public static class StatusSegmentOrdering
{
    /// <summary>Ordered (left→center→right) status segments.</summary>
    /// <remarks>
    ///     Runs on every status projection (<see cref="StatusProjector.ProjectFooter"/>),
    ///     so it allocates exactly one list: a single stable insertion pass over the
    ///     input instead of three per-group LINQ snapshots. The comparison mirrors the
    ///     in-place row sort in <c>ChatScreenLayout.SortRow</c>; stability preserves the
    ///     LINQ <c>OrderBy</c> contract (ties keep declaration order).
    /// </remarks>
    public static IReadOnlyList<UiStatusSegment> Ordered(IReadOnlyList<UiStatusSegment> segments)
    {
        var result = new List<UiStatusSegment>(segments.Count);
        for (int i = 0; i < segments.Count; i++)
        {
            result.Add(segments[i]);
        }

        for (int i = 1; i < result.Count; i++)
        {
            UiStatusSegment cell = result[i];
            int j = i - 1;
            while (j >= 0 && Compare(result[j], cell) > 0)
            {
                result[j + 1] = result[j];
                j--;
            }

            result[j + 1] = cell;
        }

        return result;
    }

    /// <summary>Negative when <paramref name="a"/> paints before <paramref name="b"/>: group rank first, then importance (the right group descends).</summary>
    private static int Compare(in UiStatusSegment a, in UiStatusSegment b)
    {
        int ra = Rank(a.Align);
        int rb = Rank(b.Align);
        if (ra != rb)
        {
            return ra - rb;
        }

        return a.Align == Alignment.Right
            ? b.Importance.CompareTo(a.Importance)
            : a.Importance.CompareTo(b.Importance);
    }

    private static int Rank(Alignment align) =>
        align == Alignment.Left ? 0 : align == Alignment.Center ? 1 : 2;
}
