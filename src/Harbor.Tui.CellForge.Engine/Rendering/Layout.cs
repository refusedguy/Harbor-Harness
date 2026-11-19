// #3088 (slice S7, epic #3073): public Split facade over the engine-owned
// constraint vocabulary. Callers used to hand-roll this arithmetic in every
// screen; this is the one canonical "carve a Rect into N panels" entry point.
//
// BCL-only, AOT-clean: pure integer/floating arithmetic over the public
// SizeConstraint dictionary, no reflection, no solver dependency. The 16-byte
// Cell and the cell-diff write path are untouched. The general four-phase
// INode solver (FlexLayout/FlexTrack) stays the owner of intrinsic sizing;
// this facade is the small, discoverable split primitive screen authors want.

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Public layout split primitive: carves a <see cref="Rect"/> into one panel
/// rectangle per share along a main axis. Horizontal splits lay panels
/// side-by-side (they share the width); vertical splits stack them (they share
/// the height). The cross axis always fills the supplied area.
/// </summary>
/// <remarks>
/// <para>
/// Share kinds come from <see cref="SizeConstraint"/>, the engine's public
/// constraint dictionary: <see cref="SizeConstraint.Fixed"/> and
/// <see cref="SizeConstraint.Auto"/> claim their cells up front,
/// <see cref="SizeConstraint.Percent"/> and <see cref="SizeConstraint.Ratio"/>
/// claim a fraction of the full extent, and <see cref="SizeConstraint.Fill"/>
/// panels split what is left by weight.
/// </para>
/// <para>
/// A global <c>minPanelSize</c> floor keeps every panel at least that many
/// cells. When the floor cannot fit (<c>panelCount * minPanelSize &gt; extent</c>,
/// or a zero/negative extent) it is dropped and the split degrades to pure
/// proportional distribution. Integer dust from truncation always goes to the
/// leftmost eligible panel, so results are deterministic.
/// </para>
/// <para>
/// Overflow never grows past the supplied area. When claims exceed the extent,
/// every panel is scaled down proportionally while keeping the floor where it
/// fits. Underfill stays trailing at the far edge, matching
/// <see cref="FlexJustify.Start"/>.
/// </para>
/// </remarks>
public static class Layout
{
    /// <summary>
    /// Allocating convenience form: splits <paramref name="area"/> into one
    /// <see cref="Rect"/> per entry in <paramref name="shares"/>. Use the span
    /// overload on steady-state paths that already own a result buffer.
    /// </summary>
    /// <param name="area">Box to carve. Negative extents count as zero.</param>
    /// <param name="dir">Main axis: <see cref="SplitDir.Horizontal"/> shares the
    /// width, <see cref="SplitDir.Vertical"/> shares the height.</param>
    /// <param name="shares">One constraint per panel, in panel order.</param>
    /// <param name="minPanelSize">Minimum cells for every panel. Negative values
    /// count as zero; the floor is dropped when it cannot fit the extent.</param>
    /// <returns>One rectangle per share, in the same order.</returns>
    public static Rect[] Split(Rect area, SplitDir dir, SizeConstraint[] shares, int minPanelSize = 0)
    {
        ArgumentNullException.ThrowIfNull(shares);
        var rects = new Rect[shares.Length];
        Split(area, dir, shares, rects, minPanelSize);
        return rects;
    }

    /// <summary>
    /// Zero-allocation form: writes one <see cref="Rect"/> per share into
    /// <paramref name="into"/> in share order. Scratch buffers stack-allocate
    /// for up to 256 panels.
    /// </summary>
    /// <param name="area">Box to carve. Negative extents count as zero.</param>
    /// <param name="dir">Main axis.</param>
    /// <param name="shares">One constraint per panel, in panel order.</param>
    /// <param name="into">Destination span; must hold at least
    /// <c>shares.Length</c> rectangles.</param>
    /// <param name="minPanelSize">Minimum cells for every panel; dropped when
    /// it cannot fit the extent.</param>
    /// <exception cref="ArgumentException"><paramref name="into"/> is shorter
    /// than <paramref name="shares"/>.</exception>
    public static void Split(
        Rect area,
        SplitDir dir,
        ReadOnlySpan<SizeConstraint> shares,
        Span<Rect> into,
        int minPanelSize = 0)
    {
        if (into.Length < shares.Length)
        {
            throw new ArgumentException("Destination span is shorter than the share list.", nameof(into));
        }

        int n = shares.Length;
        if (n == 0)
        {
            return;
        }

        bool horizontal = dir == SplitDir.Horizontal;
        int extent = Math.Max(0, horizontal ? area.Width : area.Height);
        int origin = horizontal ? area.X : area.Y;
        int crossOrigin = horizontal ? area.Y : area.X;
        int cross = Math.Max(0, horizontal ? area.Height : area.Width);

        int floor = Math.Max(0, minPanelSize);
        if ((long)n * floor > extent)
        {
            // The floor cannot fit even with every panel at its minimum.
            // Drop it rather than overflowing the supplied area.
            floor = 0;
        }

        Span<int> sizes = n <= 256 ? stackalloc int[n] : new int[n];
        Span<bool> isFiller = n <= 256 ? stackalloc bool[n] : new bool[n];
        Span<double> weights = n <= 256 ? stackalloc double[n] : new double[n];

        // Phase A — fixed-like claims are resolved up front, fillers wait for
        // the leftover. This mirrors SizeConstraint.Compute's own split of
        // `available` vs `remaining`.
        int fixedSum = 0;
        double weightTotal = 0;
        for (int i = 0; i < n; i++)
        {
            switch (shares[i])
            {
                case SizeConstraint.Fill fill:
                    isFiller[i] = true;
                    weights[i] = Math.Max(1, fill.Weight);
                    weightTotal += weights[i];
                    break;

                case SizeConstraint.Fixed fixedShare:
                    sizes[i] = Math.Max(0, fixedShare.Value);
                    fixedSum += sizes[i];
                    break;

                case SizeConstraint.Auto auto:
                    sizes[i] = Math.Max(0, auto.Min);
                    fixedSum += sizes[i];
                    break;

                case SizeConstraint.Percent percent:
                    sizes[i] = (int)((long)extent * Math.Clamp(percent.Value, 0, 100) / 100);
                    fixedSum += sizes[i];
                    break;

                case SizeConstraint.Ratio ratio:
                    sizes[i] = ratio.Denominator > 0 && ratio.Numerator > 0
                        ? (int)((long)extent * Math.Min(ratio.Numerator, ratio.Denominator) / ratio.Denominator)
                        : 0;
                    fixedSum += sizes[i];
                    break;

                default:
                    sizes[i] = 0;
                    break;
            }
        }

        // Phase B — fillers split the leftover by weight. A running divisor
        // makes the last nonzero-weight panel absorb truncation dust exactly.
        int leftover = Math.Max(0, extent - fixedSum);
        double running = weightTotal;
        int remaining = leftover;
        for (int i = 0; i < n; i++)
        {
            if (!isFiller[i])
            {
                continue;
            }

            int share = running > 0 ? (int)(remaining * (weights[i] / running)) : 0;
            running -= weights[i];
            remaining -= share;
            sizes[i] = share;
        }

        for (int i = 0; i < n && remaining > 0; i++)
        {
            if (isFiller[i])
            {
                sizes[i]++;
                remaining--;
            }
        }

        // Phase C — enforce the floor and the area bound in one pass. Floor
        // first, then scale the over-floor excess into the remaining budget;
        // the sum can never exceed the extent and no panel drops below floor.
        Span<int> finalSizes = n <= 256 ? stackalloc int[n] : new int[n];
        Fit(sizes, floor, extent, finalSizes);

        int pos = origin;
        for (int i = 0; i < n; i++)
        {
            into[i] = horizontal
                ? new Rect(pos, crossOrigin, finalSizes[i], cross)
                : new Rect(crossOrigin, pos, cross, finalSizes[i]);
            pos += finalSizes[i];
        }
    }

    /// <summary>
    /// Rewrites <paramref name="result"/> with each entry at least
    /// <paramref name="floor"/> and the total at most <paramref name="extent"/>.
    /// Underfill is left trailing; only overflow scales panels down.
    /// </summary>
    private static void Fit(ReadOnlySpan<int> sizes, int floor, int extent, Span<int> result)
    {
        int n = sizes.Length;
        int sum = 0;
        for (int i = 0; i < n; i++)
        {
            int size = Math.Max(sizes[i], floor);
            result[i] = size;
            sum += size;
        }

        if (sum <= extent)
        {
            return;
        }

        // Overflow: keep the floor, scale only the excess into the budget.
        long totalOver = 0;
        for (int i = 0; i < n; i++)
        {
            int over = result[i] - floor;
            result[i] = over;
            totalOver += over;
        }

        int budget = extent - floor * n; // >= 0 because the floor was checked feasible
        if (budget <= 0 || totalOver <= 0)
        {
            for (int i = 0; i < n; i++)
            {
                result[i] = floor;
            }

            return;
        }

        long running = totalOver;
        int remaining = budget;
        for (int i = 0; i < n; i++)
        {
            int over = result[i];
            int share = running > 0 ? (int)(remaining * over / running) : 0;
            running -= over;
            remaining -= share;
            result[i] = floor + share;
        }

        for (int i = 0; i < n && remaining > 0; i++)
        {
            // Only panels that had excess take dust; a panel pinned at the
            // floor keeps it. sizes[i] still holds the pre-fit desired value.
            if (sizes[i] > floor)
            {
                result[i]++;
                remaining--;
            }
        }
    }
}
