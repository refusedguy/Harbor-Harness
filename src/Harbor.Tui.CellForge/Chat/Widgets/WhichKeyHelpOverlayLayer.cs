using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// PRIM11 seating (#306): <see cref="WhichKeyHelpOverlay"/> as an
/// <see cref="IOverlayLayer"/> on the PRIM2a z-stack. Modal and opaque —
/// occludes layers beneath and captures hit-tests, like the dialog layer.
/// The host pushes one instance onto <c>LayoutTree.Overlays</c> and calls
/// <see cref="Sync"/> per frame (or on resize); hidden paint stays a no-op so
/// goldens are byte-identical while the overlay is hidden. Skeleton
/// (viewport, <see cref="Sync"/>, the <see cref="Paint"/> guard chain, flags)
/// lives in <see cref="OverlayLayerBase{TOverlay}"/> (#1537).
/// </summary>
public sealed class WhichKeyHelpOverlayLayer : OverlayLayerBase<WhichKeyHelpOverlay>
{
    /// <summary>Stable layer id (push replaces by id, moving to top).</summary>
    public const string LayerId = "whichkey";

    public WhichKeyHelpOverlayLayer(WhichKeyHelpOverlay overlay)
        : base(overlay, LayerId, opaque: true, hitTransparent: false)
    {
    }

    /// <summary>The centered box is computed from the pushed viewport.</summary>
    protected override Rect ComputeBoundsCore(Rect viewport) => Overlay.ComputeBox(viewport);

    protected override bool IsShownCore => Overlay.IsShown;

    /// <summary>
    ///     True when the box fits the viewport at the minimum dimensions. This is a
    ///     GEOMETRY answer, not a state answer: it was inlined into
    ///     <see cref="Visible" /> below, which made <c>Visible</c> a second and
    ///     independent definition of "is this overlay showing" — so a shown overlay
    ///     reported <c>Visible == false</c> on a small terminal and the host could not
    ///     tell a too-small box from a hidden overlay. Now the two questions are
    ///     separately named and the layer has one authority:
    ///     <see cref="IsShownCore" /> for state (from <see cref="WhichKeyHelpOverlay" />)
    ///     and this for room. Both must hold to paint, which is what the old
    ///     conjunction said — only now it is legible.
    /// </summary>
    public bool HasRoom
    {
        get
        {
            Rect box = Bounds;
            return box.Width >= WhichKeyHelpOverlay.MinWidth &&
                   box.Height >= WhichKeyHelpOverlay.MinHeight;
        }
    }

    public override bool Visible => Overlay.IsShown && HasRoom;

    protected override void PaintCore(ScreenBuffer buffer, Rect box) => Overlay.Paint(buffer, Viewport);
}
