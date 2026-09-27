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
    public static IReadOnlyList<UiStatusSegment> Ordered(IReadOnlyList<UiStatusSegment> segments)
    {
        var left = segments.Where(s => s.Align == Alignment.Left).OrderBy(s => s.Importance).ToList();
        var center = segments.Where(s => s.Align == Alignment.Center).OrderBy(s => s.Importance).ToList();
        var right = segments.Where(s => s.Align == Alignment.Right).OrderByDescending(s => s.Importance).ToList();

        var result = new List<UiStatusSegment>(segments.Count);
        result.AddRange(left);
        result.AddRange(center);
        result.AddRange(right);
        return result;
    }
}
