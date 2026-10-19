using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// PRIM2c seating: <see cref="ToastOverlay"/> as an <see cref="IOverlayLayer"/>.
/// Non-opaque (never occludes layers beneath) and hit-transparent (pointer
/// events fall through). The host pushes one instance onto
/// <c>LayoutTree.Overlays</c> above the dialog layer and calls
/// <see cref="Sync"/> per frame; hidden paint stays a no-op. Skeleton
/// (viewport, <see cref="Sync"/>, the <see cref="Paint"/> guard chain, flags)
/// lives in <see cref="OverlayLayerBase{TOverlay}"/> (#1537).
/// </summary>
public sealed class ToastOverlayLayer : OverlayLayerBase<ToastOverlay>
{
    /// <summary>Stable layer id (push replaces by id, moving to top).</summary>
    public const string LayerId = "toast";

    public ToastOverlayLayer(ToastOverlay overlay)
        : base(overlay, LayerId, opaque: false, hitTransparent: true)
    {
    }

    /// <summary>The top-right strip the toasts are laid out in.</summary>
    protected override Rect ComputeBoundsCore(Rect viewport) => Overlay.ComputeBounds(viewport);

    protected override bool IsShownCore => !Overlay.IsEmpty;

    /// <summary>
    /// The strip paints its own box (the only seat that does): the overlay
    /// already clips each toast cell into it.
    /// </summary>
    protected override void PaintCore(ScreenBuffer buffer, Rect box) => Overlay.Paint(buffer, box);
}
