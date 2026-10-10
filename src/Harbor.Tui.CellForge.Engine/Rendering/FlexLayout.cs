
namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Main-axis distribution of leftover space in a <see cref="FlexLayout"/>
/// row/column (ratatui <c>Flex</c> analogue, Textual align analogue).
/// Applies only when tracks plus gaps underfill the available extent —
/// overflow always shrinks tracks instead (flexible first, then fixed).
/// </summary>
public enum FlexJustify : byte
{
    /// <summary>Pack from the leading edge; leftover trails at the far end.</summary>
    Start = 0,

    /// <summary>Center the packed run; leftover splits evenly on both sides.</summary>
    Center = 1,

    /// <summary>Pack against the far edge; leftover leads at the start.</summary>
    End = 2,

    /// <summary>Pin first/last track to the edges; leftover spreads evenly over
    /// the inter-item gaps. A single track packs at the leading edge.</summary>
    SpaceBetween = 3,

    /// <summary>Even space around every track: edges take a half share, gaps a
    /// full share. Integer remainders distribute leading-to-trailing.</summary>
    SpaceAround = 4,
}

/// <summary>
/// One child's size constraint along a <see cref="FlexLayout"/> main axis
/// (ratatui <c>Constraint</c> analogue, content-agnostic: no content measuring,
/// layout math only). Construct via the <c>Fixed</c> / <c>Percent</c> /
/// <c>Fill</c> / <c>Min</c> / <c>Max</c> factories.
/// </summary>
public readonly record struct FlexTrack
{
    private readonly byte _kind;
    private readonly float _amount;

    private FlexTrack(byte kind, float amount)
    {
        _kind = kind;
        _amount = amount;
    }

    /// <summary>Exact cell count, clamped at zero.</summary>
    public static FlexTrack Fixed(int cells) => new(0, Math.Max(0, cells));

    /// <summary>
    /// Share of the gap-subtracted extent, clamped to 0..1 (NaN maps to 0).
    /// Truncation remainders stay in the leftover pool for
    /// <see cref="FlexJustify"/> to place.
    /// </summary>
    public static FlexTrack Percent(float fraction) =>
        new(1, float.IsNaN(fraction) ? 0f : Math.Clamp(fraction, 0f, 1f));

    /// <summary>
    /// Flexible share of the post-fixed remainder, proportional to
    /// <paramref name="weight"/> (ratatui <c>Fill</c>). Non-finite or negative
    /// weights count as zero; an all-zero weight set splits the remainder evenly.
    /// </summary>
    public static FlexTrack Fill(float weight = 1f) =>
        new(2, float.IsFinite(weight) ? Math.Max(0f, weight) : 0f);

    /// <summary>
    /// Flexible like <see cref="Fill(float)"/> (unit weight) but never below
    /// <paramref name="cells"/> unless the whole run overflows — then flexible
    /// tracks collapse (trailing first) before fixed ones shrink.
    /// </summary>
    public static FlexTrack Min(int cells) => new(3, Math.Max(0, cells));

    /// <summary>
    /// Flexible like <see cref="Fill(float)"/> (unit weight) but never above
    /// <paramref name="cells"/>; uncapped headroom stays in the leftover pool
    /// for <see cref="FlexJustify"/> to place.
    /// </summary>
    public static FlexTrack Max(int cells) => new(4, Math.Max(0, cells));

    internal bool IsFlex => _kind >= 2;
    internal bool IsFixedLike => _kind <= 1;
    internal bool IsPercent => _kind == 1;
    internal bool IsMin => _kind == 3;
    internal bool IsMax => _kind == 4;
    internal int FixedCells => (int)_amount;
    internal float Fraction => _amount;
    internal float FlexWeight => _kind == 2 ? _amount : 1f;
    internal int FlexFloor => _kind == 3 ? (int)_amount : 0;
    internal int FlexCap => _kind == 4 ? (int)_amount : int.MaxValue;
}

/// <summary>
/// Linear flex container solver (PRIM8 #303): n-ary Row
/// (<see cref="SplitDir.Horizontal"/>) / Column (<see cref="SplitDir.Vertical"/>)
/// beyond the binary split-ratio tree. Pure rect math — no painting, no
/// widgets, no viewport, no diff: UX1 fixed slots (Column of Fixed header +
/// Fill body + Fixed footer) and dialog frames consume this;
/// <see cref="LayoutTree"/> keeps owning the collapse-priority solver.
/// Children take the full cross extent; the main axis is water-filled honoring
/// fixed sizes, percent shares, fill weights and min/max clamps.
/// Not thread-safe: arrange on the render thread only.
/// </summary>
public static class FlexLayout
{
    /// <summary>
    /// Arranges <paramref name="tracks"/> inside <paramref name="avail"/> along
    /// <paramref name="dir"/>, writing one <see cref="Rect"/> per track into
    /// <paramref name="into"/> in track order. Zero-allocation steady-state path.
    /// </summary>
    /// <param name="dir">Main axis: <see cref="SplitDir.Horizontal"/> lays
    /// children side-by-side (Row), <see cref="SplitDir.Vertical"/> stacks them
    /// top-to-bottom (Column).</param>
    /// <param name="avail">Available area. Negative extents count as zero.</param>
    /// <param name="tracks">One constraint per child.</param>
    /// <param name="into">Destination span; must hold at least
    /// <c>tracks.Length</c> rects.</param>
    /// <param name="gap">Base cells between adjacent tracks; collapses to zero
    /// when the gaps alone exceed the extent (all tracks then read zero-size).</param>
    /// <param name="justify">Placement of leftover space when the run underfills.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="gap"/> is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="into"/> is shorter than
    /// <paramref name="tracks"/>.</exception>
    public static void Arrange(
        SplitDir dir,
        Rect avail,
        ReadOnlySpan<FlexTrack> tracks,
        Span<Rect> into,
        int gap = 0,
        FlexJustify justify = FlexJustify.Start)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(gap);
        if (into.Length < tracks.Length)
        {
            throw new ArgumentException("Destination span is shorter than the track list.", nameof(into));
        }

        int n = tracks.Length;
        if (n == 0)
        {
            return;
        }

        bool horizontal = dir == SplitDir.Horizontal;
        int extent = Math.Max(0, horizontal ? avail.Width : avail.Height);
        int origin = horizontal ? avail.X : avail.Y;
        int crossOrigin = horizontal ? avail.Y : avail.X;
        int cross = Math.Max(0, horizontal ? avail.Height : avail.Width);

        long gapTotal = (long)gap * (n - 1);
        int content = gapTotal >= extent ? 0 : extent - (int)gapTotal;
        int effGap = gapTotal >= extent ? 0 : gap;

        // Solver scratch: stack for sane child counts, heap past the threshold.
        Span<int> sizes = n <= 256 ? stackalloc int[n] : new int[n];
        Measure(tracks, content, sizes);

        int used = effGap * (n - 1);
        for (int i = 0; i < n; i++)
        {
            used += sizes[i];
        }

        int leftover = extent - used;
        int pos = origin;

        if (leftover > 0)
        {
            switch (justify)
            {
                case FlexJustify.Center:
                    pos = origin + leftover / 2;
                    break;
                case FlexJustify.End:
                    pos = origin + leftover;
                    break;
                case FlexJustify.SpaceBetween when n > 1:
                {
                    int q = leftover / (n - 1);
                    int r = leftover % (n - 1);
                    for (int i = 0; i < n; i++)
                    {
                        Emit(horizontal, into, i, pos, crossOrigin, cross, sizes[i]);
                        pos += sizes[i] + effGap + (i < n - 1 ? q + (i < r ? 1 : 0) : 0);
                    }

                    return;
                }

                case FlexJustify.SpaceAround:
                {
                    int half = leftover / (2 * n);
                    int rem = leftover - half * 2 * n;
                    pos = origin + half + (rem > 0 ? 1 : 0);
                    if (rem > 0)
                    {
                        rem--;
                    }

                    for (int i = 0; i < n; i++)
                    {
                        Emit(horizontal, into, i, pos, crossOrigin, cross, sizes[i]);
                        pos += sizes[i];
                        if (i < n - 1)
                        {
                            int extra = half * 2;
                            if (rem > 0)
                            {
                                extra++;
                                rem--;
                            }

                            if (rem > 0)
                            {
                                extra++;
                                rem--;
                            }

                            pos += effGap + extra;
                        }
                    }

                    return;
                }

                default:
                    break;
            }
        }

        for (int i = 0; i < n; i++)
        {
            Emit(horizontal, into, i, pos, crossOrigin, cross, sizes[i]);
            pos += sizes[i] + effGap;
        }
    }

    /// <summary>
    /// Allocating convenience over <see cref="Arrange(SplitDir, Rect,
    /// ReadOnlySpan{FlexTrack}, Span{Rect}, int, FlexJustify)"/> for cold paths
    /// (dialog frames, tests). Steady-state frames should reuse a buffer via
    /// <see cref="Arrange(SplitDir, Rect, ReadOnlySpan{FlexTrack}, Span{Rect},
    /// int, FlexJustify)"/>.
    /// </summary>
    /// <param name="dir">Main axis (Row = <see cref="SplitDir.Horizontal"/>,
    /// Column = <see cref="SplitDir.Vertical"/>).</param>
    /// <param name="avail">Available area.</param>
    /// <param name="tracks">One constraint per child.</param>
    /// <param name="gap">Base cells between adjacent tracks.</param>
    /// <param name="justify">Placement of leftover space.</param>
    public static Rect[] Solve(
        SplitDir dir,
        Rect avail,
        FlexTrack[] tracks,
        int gap = 0,
        FlexJustify justify = FlexJustify.Start)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var rects = new Rect[tracks.Length];
        Arrange(dir, avail, tracks, rects, gap, justify);
        return rects;
    }

    private static void Emit(bool horizontal, Span<Rect> into, int i, int pos, int crossOrigin, int cross, int size)
    {
        into[i] = horizontal
            ? new Rect(pos, crossOrigin, size, cross)
            : new Rect(crossOrigin, pos, cross, size);
    }

    private static void Measure(ReadOnlySpan<FlexTrack> tracks, int content, Span<int> sizes)
    {
        int n = tracks.Length;
        int fixedSum = 0;
        int flexCount = 0;
        float weightTotal = 0f;
        for (int i = 0; i < n; i++)
        {
            var t = tracks[i];
            if (t.IsFlex)
            {
                flexCount++;
                weightTotal += t.FlexWeight;
            }
            else if (t.IsPercent)
            {
                int size = (int)(content * t.Fraction);
                sizes[i] = size;
                fixedSum += size;
            }
            else
            {
                sizes[i] = t.FixedCells;
                fixedSum += sizes[i];
            }
        }

        int rest = content - fixedSum;
        if (rest < 0)
        {
            ZeroFlexAndScaleFixed(tracks, sizes, content);
            return;
        }

        if (flexCount == 0)
        {
            return;
        }

        // Proportional split, floor + running-divisor absorption (deterministic:
        // the last nonzero-weight track absorbs truncation dust exactly).
        bool evenSplit = weightTotal <= 0f;
        float running = evenSplit ? flexCount : weightTotal;
        int remaining = rest;
        for (int i = 0; i < n; i++)
        {
            if (!tracks[i].IsFlex)
            {
                continue;
            }

            float w = evenSplit ? 1f : tracks[i].FlexWeight;
            int share = running > 0f ? (int)(remaining * (w / running)) : 0;
            running -= w;
            remaining -= share;
            sizes[i] = ClampFlex(tracks[i], share);
        }

        int total = 0;
        for (int i = 0; i < n; i++)
        {
            total += sizes[i];
        }

        if (total <= content)
        {
            // Max caps may have underfilled: headroom stays in the leftover pool.
            return;
        }

        // Min floors overfilled: collapse flex to floors (trailing first), then
        // to zero, then scale the fixed-like shares into the remaining budget.
        int over = total - content;
        for (int i = n - 1; i >= 0 && over > 0; i--)
        {
            if (!tracks[i].IsFlex)
            {
                continue;
            }

            int cut = Math.Min(over, sizes[i] - tracks[i].FlexFloor);
            if (cut > 0)
            {
                sizes[i] -= cut;
                over -= cut;
            }
        }

        for (int i = n - 1; i >= 0 && over > 0; i--)
        {
            if (!tracks[i].IsFlex)
            {
                continue;
            }

            int cut = Math.Min(over, sizes[i]);
            sizes[i] -= cut;
            over -= cut;
        }

        if (over > 0)
        {
            ZeroFlexAndScaleFixed(tracks, sizes, content);
        }
    }

    private static int ClampFlex(FlexTrack track, int share)
    {
        if (track.IsMin)
        {
            return Math.Max(share, track.FlexFloor);
        }

        if (track.IsMax)
        {
            return Math.Min(share, track.FlexCap);
        }

        return Math.Max(0, share);
    }

    /// <summary>
    /// Overflow landing: flexible tracks to zero, fixed-like shares scaled
    /// proportionally into <paramref name="budget"/> (floor + left-to-right
    /// remainder; all-zero fixed input stays zero).
    /// </summary>
    private static void ZeroFlexAndScaleFixed(ReadOnlySpan<FlexTrack> tracks, Span<int> sizes, int budget)
    {
        int n = tracks.Length;
        int sum = 0;
        for (int i = 0; i < n; i++)
        {
            if (tracks[i].IsFlex)
            {
                sizes[i] = 0;
            }
            else
            {
                sum += sizes[i];
            }
        }

        if (sum <= 0)
        {
            return;
        }

        int remaining = budget;
        int running = sum;
        for (int i = 0; i < n; i++)
        {
            if (!tracks[i].IsFixedLike)
            {
                continue;
            }

            int orig = sizes[i];
            int share = running > 0 ? (int)(remaining * ((double)orig / running)) : 0;
            running -= orig;
            remaining -= share;
            sizes[i] = share;
        }

        for (int i = 0; i < n && remaining > 0; i++)
        {
            if (tracks[i].IsFixedLike)
            {
                sizes[i]++;
                remaining--;
            }
        }
    }
}

/// <summary>
/// Centered fixed-size box solver (PRIM8 #303): dialog/popover frames.
/// Clamps oversized content to the available area; empty availability yields an
/// empty rect at the area origin. Pure rect math, no painting.
/// </summary>
public static class CenterLayout
{
    /// <summary>Centers <paramref name="content"/> inside <paramref name="avail"/>.</summary>
    /// <param name="avail">Available area. Negative extents count as zero.</param>
    /// <param name="content">Desired box size. Negative extents count as zero.</param>
    public static Rect Arrange(Rect avail, Size content) =>
        Arrange(avail, content.Width, content.Height);

    /// <summary>
    /// Centers a <paramref name="contentWidth"/> × <paramref name="contentHeight"/>
    /// box inside <paramref name="avail"/>.
    /// </summary>
    /// <param name="avail">Available area. Negative extents count as zero.</param>
    /// <param name="contentWidth">Desired box width. Negative counts as zero.</param>
    /// <param name="contentHeight">Desired box height. Negative counts as zero.</param>
    public static Rect Arrange(Rect avail, int contentWidth, int contentHeight)
    {
        int aw = Math.Max(0, avail.Width);
        int ah = Math.Max(0, avail.Height);
        int w = Math.Min(Math.Max(0, contentWidth), aw);
        int h = Math.Min(Math.Max(0, contentHeight), ah);
        return new Rect(avail.X + (aw - w) / 2, avail.Y + (ah - h) / 2, w, h);
    }
}

/// <summary>
/// Full-area overlap stack solver (PRIM8 #303): every child takes the whole
/// available rect (z-order by index, lower first) — the layout half of tab
/// pages / stacked views. Painting order and hit-testing stay with the
/// consumers; this only hands out rects. Pure rect math, no painting.
/// </summary>
public static class StackLayout
{
    /// <summary>
    /// Fills <paramref name="into"/> (must hold <paramref name="count"/> rects)
    /// with the normalized <paramref name="avail"/>. Zero-allocation path.
    /// </summary>
    /// <param name="avail">Available area. Negative extents count as zero.</param>
    /// <param name="count">Child count.</param>
    /// <param name="into">Destination span.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="into"/> is shorter than
    /// <paramref name="count"/>.</exception>
    public static void Arrange(Rect avail, int count, Span<Rect> into)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (into.Length < count)
        {
            throw new ArgumentException("Destination span is shorter than the child count.", nameof(into));
        }

        var rect = new Rect(avail.X, avail.Y, Math.Max(0, avail.Width), Math.Max(0, avail.Height));
        for (int i = 0; i < count; i++)
        {
            into[i] = rect;
        }
    }

    /// <summary>Allocating convenience over <see cref="Arrange(Rect, int, Span{Rect})"/>.</summary>
    /// <param name="avail">Available area.</param>
    /// <param name="count">Child count.</param>
    public static Rect[] Solve(Rect avail, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var rects = new Rect[count];
        Arrange(avail, count, rects);
        return rects;
    }
}
