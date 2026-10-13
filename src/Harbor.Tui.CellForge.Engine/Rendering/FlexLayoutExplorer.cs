namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Headless constraint-explorer for <see cref="FlexLayout"/> (ratatui
/// constraint-explorer analogue, epic #1155, issue #1169): renders one
/// horizontal run as a single-line ASCII diagram so layout docs and
/// doc-tests share one visual spec.
/// <example>
/// <code>
/// string frame = FlexLayoutExplorer.Render(
///     20,
///     [FlexTrack.Fixed(6), FlexTrack.Fill(1), FlexTrack.Fixed(4)],
///     gap: 1);
/// // frame == "[AAAAAA BBBBBBBB CCCC]"
/// </code>
/// </example>
/// </summary>
/// <remarks>
/// Glyphs: track <c>i</c> paints <c>'A' + (i % 26)</c>; every non-track cell
/// (base gaps and <see cref="FlexJustify"/> leftover alike) paints
/// <c>' '</c>, wrapped in <c>'['</c> / <c>']</c>. Zero-width tracks paint
/// nothing. Vertical runs are the same math transposed — the explorer only
/// renders the horizontal projection.
/// </remarks>
public static class FlexLayoutExplorer
{
    /// <summary>
    /// Renders <paramref name="tracks"/> inside a zero-based run of
    /// <paramref name="extent"/> cells as <c>"[" + row + "]"</c>
    /// (<c>row.Length == extent</c>).
    /// </summary>
    /// <param name="extent">Run width in cells. Negative counts as zero.</param>
    /// <param name="tracks">One constraint per child.</param>
    /// <param name="gap">Base cells between adjacent tracks.</param>
    /// <param name="justify">Placement of leftover space on underfill.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="gap"/> is negative.</exception>
    public static string Render(
        int extent,
        FlexTrack[] tracks,
        int gap = 0,
        FlexJustify justify = FlexJustify.Start)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        return RenderCore(Math.Max(0, extent), tracks, Array.Empty<int>(), gap, justify);
    }

    /// <summary>
    /// Content-sized variant: <paramref name="intrinsic"/> carries the
    /// caller-measured main-axis extent per track, read for <c>Auto</c>
    /// tracks only (missing entries count as zero).
    /// </summary>
    /// <param name="extent">Run width in cells. Negative counts as zero.</param>
    /// <param name="tracks">One constraint per child.</param>
    /// <param name="intrinsic">Content-measured extent per track.</param>
    /// <param name="gap">Base cells between adjacent tracks.</param>
    /// <param name="justify">Placement of leftover space on underfill.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="gap"/> is negative.</exception>
    public static string Render(
        int extent,
        FlexTrack[] tracks,
        int[] intrinsic,
        int gap = 0,
        FlexJustify justify = FlexJustify.Start)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(intrinsic);
        return RenderCore(Math.Max(0, extent), tracks, intrinsic, gap, justify);
    }

    private static string RenderCore(
        int extent,
        FlexTrack[] tracks,
        int[] intrinsic,
        int gap,
        FlexJustify justify)
    {
        char[] row = new char[extent];
        Array.Fill(row, ' ');
        if (extent == 0 || tracks.Length == 0)
        {
            return "[" + new string(row) + "]";
        }

        Rect[] rects = intrinsic.Length == 0
            ? FlexLayout.Solve(SplitDir.Horizontal, new Rect(0, 0, extent, 1), tracks, gap, justify)
            : FlexLayout.Solve(SplitDir.Horizontal, new Rect(0, 0, extent, 1), tracks, intrinsic, gap, justify);
        for (int i = 0; i < rects.Length; i++)
        {
            char glyph = (char)('A' + (i % 26));
            int end = Math.Min(extent, rects[i].Right);
            for (int x = Math.Max(0, rects[i].X); x < end; x++)
            {
                row[x] = glyph;
            }
        }

        return "[" + new string(row) + "]";
    }
}
