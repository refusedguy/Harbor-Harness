using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// PRIM2c seating: <see cref="DialogOverlay"/> as an <see cref="IOverlayLayer"/>.
/// Modal and opaque — occludes layers beneath and captures hit-tests. The host
/// pushes one instance onto <c>LayoutTree.Overlays</c> and calls
/// <see cref="Sync"/> per frame (or on resize); empty paint stays a no-op so
/// goldens are byte-identical while the dialog is hidden. Skeleton
/// (viewport, <see cref="Sync"/>, the <see cref="Paint"/> guard chain, flags)
/// lives in <see cref="OverlayLayerBase{TOverlay}"/> (#1537).
/// </summary>
public sealed class DialogOverlayLayer : OverlayLayerBase<DialogOverlay>
{
    /// <summary>Stable layer id (push replaces by id, moving to top).</summary>
    public const string LayerId = "dialog";

    public DialogOverlayLayer(DialogOverlay overlay)
        : base(overlay, LayerId, opaque: true, hitTransparent: false)
    {
    }

    /// <summary>The centred box is computed from the pushed viewport.</summary>
    protected override Rect ComputeBoundsCore(Rect viewport) => Overlay.ComputeBox(viewport);

    protected override bool IsShownCore => Overlay.Visible;

    protected override void PaintCore(ScreenBuffer buffer, Rect box) => Overlay.Paint(buffer, Viewport);
}
