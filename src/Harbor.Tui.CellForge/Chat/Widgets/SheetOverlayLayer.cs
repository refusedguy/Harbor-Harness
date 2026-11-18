using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Seats a <see cref="SheetOverlay"/> on the existing <see cref="OverlayStack"/>
/// z-plane with the shared <see cref="OverlayLayerBase{TOverlay}"/> skeleton.
/// Modality is a declaration, not a second mechanism: <c>IsModal</c> comes from
/// the base, so the stack's barrier and the host's key cascade treat the sheet
/// exactly like the other modal overlays. Nothing paints while the sheet is
/// closed, so quiet frames and existing goldens stay byte-identical.
/// </summary>
public sealed class SheetOverlayLayer : OverlayLayerBase<SheetOverlay>
{
    /// <summary>Stable layer id (push replaces by id, moving to top).</summary>
    public const string LayerId = "sheet";

    public SheetOverlayLayer(SheetOverlay overlay)
        : base(overlay, LayerId, opaque: true, hitTransparent: false, isModal: true)
    {
    }

    /// <summary>Edge-anchored box translated by the host's slide progress.</summary>
    protected override Rect ComputeBoundsCore(Rect viewport) =>
        SheetPainter.ComputeBox(viewport, Overlay.Edge, Overlay.Extent, Overlay.Progress);

    protected override bool IsShownCore => Overlay.IsOpen;

    protected override void PaintCore(ScreenBuffer buffer, Rect box) => SheetPainter.Paint(buffer, box, Overlay);
}

/// <summary>
/// Pure geometry + paint for the sheet. Kept separate from the seat so the
/// slide contract and the pinned art are exercisable in a headless buffer test.
/// The rounded frame is the shared <see cref="PanelChrome.PaintBorderBox"/> the
/// dialog uses; there is no second box painter here.
/// </summary>
internal static class SheetPainter
{
    internal const int MinExtent = 8;

    /// <summary>
    /// Computes the sheet box for <paramref name="progress"/>: 0 is one full
    /// extent past the anchor edge (nothing visible), 1 is flush with it. The
    /// perpendicular axis fills the viewport. Tiny viewports yield an empty
    /// rect so the layer hides instead of painting a clipped skeleton.
    /// </summary>
    internal static Rect ComputeBox(Rect viewport, SheetEdge edge, int extent, double progress)
    {
        bool horizontal = edge is SheetEdge.Left or SheetEdge.Right;
        int along = horizontal ? viewport.Width : viewport.Height;
        int across = horizontal ? viewport.Height : viewport.Width;
        if (along < MinExtent || across < MinExtent)
        {
            return default;
        }

        int span = Math.Clamp(extent, MinExtent, along);
        double p = Math.Clamp(progress, 0.0, 1.0);
        int inset = (int)Math.Round((1.0 - p) * span);
        return edge switch
        {
            SheetEdge.Left => new Rect(viewport.X - inset, viewport.Y, span, across),
            SheetEdge.Right => new Rect(viewport.Right - span + inset, viewport.Y, span, across),
            SheetEdge.Top => new Rect(viewport.X, viewport.Y - inset, across, span),
            _ => new Rect(viewport.X, viewport.Bottom - span + inset, across, span),
        };
    }

    /// <summary>Paints the title, focus rows and body inside the shared frame.</summary>
    internal static void Paint(ScreenBuffer buffer, Rect box, SheetOverlay sheet)
    {
        PanelChrome.PaintBorderBox(buffer, box, BoxStyle.RoundedPanel);
        int innerW = box.Width - 2;
        if (innerW <= 0 || box.Height <= 2)
        {
            return;
        }

        var focused = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        var plain = new CellStyle(ChatPalette.Muted);
        var body = ChatPalette.Dim;

        int x = box.X + 1;
        int row = box.Y + 1;
        int last = box.Bottom - 2;
        buffer.SetText(x, row++, Fit(sheet.Title, innerW), PanelChrome.TitleStyle);

        for (int i = 0; i < sheet.Items.Count && row <= last; i++, row++)
        {
            SheetFocusItem item = sheet.Items[i];
            string label = Fit(item.Label, Math.Max(0, innerW - 2));
            buffer.SetText(x, row, (item.IsFocused ? ">" : " ") + " " + label,
                item.IsFocused ? focused : plain);
        }

        for (int i = 0; i < sheet.Lines.Count && row <= last; i++, row++)
        {
            buffer.SetText(x, row, Fit(sheet.Lines[i], innerW), body);
        }
    }

    private static string Fit(string text, int width) =>
        width <= 0 ? string.Empty : text.Length <= width ? text : text[..width];
}
