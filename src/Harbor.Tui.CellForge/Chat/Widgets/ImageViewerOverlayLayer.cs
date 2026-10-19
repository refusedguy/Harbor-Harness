using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Seats the fullscreen image zoom viewer (KILLER_FEATURES §2.7 Feature 12,
/// issue #387) on the PRIM2a z-stack (<see cref="LayoutTree.Overlays" />).
/// Same contract as the other overlay layers: <see cref="Visible" /> gates the
/// paint (a hidden layer never enters the stack, so quiet frames stay
/// byte-identical) and the box is opaque so it occludes the chat beneath.
/// <see cref="IsModal" /> declares the TGui input barrier that would keep keys
/// from reaching the agent while the viewer is open — and the product gets that
/// barrier anyway, but from the host's own hand-written cascade in
/// <c>ReplInputLoop.HandleKeyAsync</c> rather than from <see cref="IsModal" />,
/// since no product code calls <see cref="OverlayStack.RouteKey" /> (#858).
/// Skeleton (viewport, <see cref="Sync"/>, the <see cref="Paint"/> guard
/// chain, flags) lives in <see cref="OverlayLayerBase{TOverlay}"/> (#1537).
/// </summary>
public sealed class ImageViewerOverlayLayer : OverlayLayerBase<ImageViewerOverlay>
{
    /// <summary>Stable layer id (push replaces by id, moving to top).</summary>
    public const string LayerId = "image-viewer";

    public ImageViewerOverlayLayer(ImageViewerOverlay overlay)
        : base(overlay, LayerId, opaque: true, hitTransparent: false, isModal: true)
    {
    }

    /// <summary>The fullscreen box is computed from the pushed viewport.</summary>
    protected override Rect ComputeBoundsCore(Rect viewport) => Overlay.ComputeBox(viewport);

    protected override bool IsShownCore => Overlay.Visible;

    /// <summary>Routes a decoded key to the viewer (zoom, dismiss).</summary>
    public override bool OnKey(in KeyEvent key) => Overlay.HandleKey(key);

    protected override void PaintCore(ScreenBuffer buffer, Rect box) => Overlay.Paint(buffer, Viewport);
}
