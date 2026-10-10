namespace Harbor.Ui.Framework.State;

/// <summary>
///     In-progress mouse gesture: press anchored at <c>Anchor</c>, currently
///     at <c>Current</c>. <c>Moving</c> drags the selected annotation (the
///     press hit it and recorded a <see cref="MarkupAnnotationModel.Checkpoint" />);
///     otherwise the release commits a new primitive of the active tool.
/// </summary>
public readonly record struct MarkupDraft(NormalizedPoint Anchor, NormalizedPoint Current, bool Moving);

/// <summary>
///     Screenshot-markup overlay session (KILLER_FEATURES §2.7 Feature 14,
///     issue #400 slice 1/2): which image is being annotated and the full
///     editing session over it. Lives in <see cref="ChatDomainState.Markup" />,
///     so open/close/edit are reducer transitions and the CellForge overlay
///     only paints the snapshot the store hands it each frame.
///     <para>
///         <b>Restore contract.</b> <see cref="SavedScrollOffset" /> snapshots
///         the feed scroll at open; the host restores it on close, so
///         Esc/second activation hands the feed back exactly as it was — the
///         overlay itself owns no scroll and no selection.
///     </para>
///     <para>
///         <b>Failure contract.</b> <see cref="Error" /> carries the last
///         bake/save failure as display text (unreadable source, unwritable
///         target, non-PNG input). It is set by <c>MarkupFailed</c>, cleared
///         by open/save/any successful edit, and never throws mid-render.
///     </para>
/// </summary>
public sealed record MarkupOverlayState(
    bool IsOpen,
    string SourcePath,
    string SourceName,
    int SourceWidth,
    int SourceHeight,
    MarkupAnnotationModel Model,
    MarkupKind ActiveTool,
    NormalizedPoint Cursor,
    string PendingText,
    MarkupDraft? Draft,
    int SavedScrollOffset,
    string Error,
    string SavedPath)
{
    /// <summary>Closed overlay: nothing to paint, nothing to route.</summary>
    public static readonly MarkupOverlayState Closed = new(
        false, string.Empty, string.Empty, 0, 0,
        MarkupAnnotationModel.Empty, MarkupKind.Arrow,
        NormalizedPoint.Create(0.5, 0.5), string.Empty, null, 0, string.Empty, string.Empty);

    /// <summary>Maximum characters kept in the pending text buffer.</summary>
    public const int MaxPendingTextLength = 64;

    /// <summary>Hit-test tolerance in unit space (a cell-ancestor of "a few pixels").</summary>
    public const double HitTolerance = 0.03;

    /// <summary>Opens the overlay over an image, snapshotting the feed scroll for restore-on-close.</summary>
    public static MarkupOverlayState Open(string sourcePath, string sourceName, int width, int height, int scrollOffset) => new(
        true, sourcePath ?? string.Empty, string.IsNullOrWhiteSpace(sourceName) ? "image" : sourceName,
        Math.Max(0, width), Math.Max(0, height),
        MarkupAnnotationModel.Empty, MarkupKind.Arrow,
        NormalizedPoint.Create(0.5, 0.5), string.Empty, null, Math.Max(0, scrollOffset), string.Empty, string.Empty);

    /// <summary>Topmost annotation within tolerance of <paramref name="point" />, or null.</summary>
    /// <remarks>
    /// A rectangle is hit by its BORDER, not its diagonal: clicking inside a
    /// box selects what is under the cursor there (or nothing), the same way
    /// the painted wireframe reads.
    /// </remarks>
    public static int? HitTest(MarkupAnnotationModel model, NormalizedPoint point)
    {
        for (int i = model.Items.Length - 1; i >= 0; i--)
        {
            var item = model.Items[i];
            bool hit = item.Kind switch
            {
                MarkupKind.Rectangle => NearRectBorder(item.From, item.To, point, HitTolerance),
                MarkupKind.Text => Distance(item.From, point) <= HitTolerance,
                _ => NearSegment(item.From, item.To, point, HitTolerance),
            };
            if (hit)
            {
                return item.Id;
            }
        }

        return null;
    }

    private static double Distance(NormalizedPoint a, NormalizedPoint b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static bool NearRectBorder(NormalizedPoint a, NormalizedPoint b, NormalizedPoint point, double tolerance)
    {
        var topLeft = NormalizedPoint.Create(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y));
        var bottomRight = NormalizedPoint.Create(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        var topRight = NormalizedPoint.Create(bottomRight.X, topLeft.Y);
        var bottomLeft = NormalizedPoint.Create(topLeft.X, bottomRight.Y);
        return NearSegment(topLeft, topRight, point, tolerance) ||
            NearSegment(topRight, bottomRight, point, tolerance) ||
            NearSegment(bottomRight, bottomLeft, point, tolerance) ||
            NearSegment(bottomLeft, topLeft, point, tolerance);
    }

    private static bool NearSegment(NormalizedPoint a, NormalizedPoint b, NormalizedPoint p, double tolerance)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lenSq = (dx * dx) + (dy * dy);
        if (lenSq <= double.Epsilon)
        {
            return Distance(a, p) <= tolerance;
        }

        double t = (((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / lenSq;
        t = t < 0 ? 0 : t > 1 ? 1 : t;
        return Distance(NormalizedPoint.Create(a.X + (t * dx), a.Y + (t * dy)), p) <= tolerance;
    }
}
