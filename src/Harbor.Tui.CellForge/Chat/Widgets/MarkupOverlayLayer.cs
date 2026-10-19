using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Seats the screenshot-markup overlay (KILLER_FEATURES §2.7 Feature 14,
/// issue #400 slice 1/2) on the PRIM2a z-stack (<see cref="LayoutTree.Overlays" />).
/// Same contract as the other overlay layers: <see cref="Visible" /> gates the
/// paint (a hidden layer never enters the stack, so quiet frames stay
/// byte-identical) and the box is opaque so it occludes the chat beneath.
/// <see cref="IsModal" /> declares the TGui input barrier that would keep keys
/// from reaching the agent while the markup session is open — and the product
/// gets that barrier anyway, but from the host's own hand-written cascade in
/// <c>ReplInputLoop.HandleKeyAsync</c> (which checks
/// <see cref="MarkupOverlay.Visible" /> and calls
/// <see cref="MarkupOverlay.HandleKey" /> directly) rather than from
/// <see cref="IsModal" />, since no product code calls
/// <see cref="OverlayStack.RouteKey" /> (#858). The layer takes the
/// <c>OnKey</c> default, exactly like the dialog and toast layers. Skeleton
/// (viewport, <see cref="Sync"/>, the <see cref="Paint"/> guard chain, flags)
/// lives in <see cref="OverlayLayerBase{TOverlay}" /> (#1537).
/// </summary>
public sealed class MarkupOverlayLayer : OverlayLayerBase<MarkupOverlay>
{
    /// <summary>Stable layer id (push replaces by id, moving to top).</summary>
    public const string LayerId = "markup";

    public MarkupOverlayLayer(MarkupOverlay overlay)
        : base(overlay, LayerId, opaque: true, hitTransparent: false, isModal: true)
    {
    }

    /// <summary>The fullscreen box is computed from the pushed viewport.</summary>
    protected override Rect ComputeBoundsCore(Rect viewport) => Overlay.ComputeBox(viewport);

    protected override bool IsShownCore => Overlay.Visible;

    protected override void PaintCore(ScreenBuffer buffer, Rect box) => Overlay.Paint(buffer, Viewport);
}
