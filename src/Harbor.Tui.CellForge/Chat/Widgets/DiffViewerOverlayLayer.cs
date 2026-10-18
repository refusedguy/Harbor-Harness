using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// PRIM12 seating (#308): <see cref="DiffViewerOverlay"/> as an
/// <see cref="IOverlayLayer"/> on the PRIM2a z-stack. Modal and opaque — the
/// fullscreen viewer occludes layers beneath and wins hit-tests over the full
/// viewport. The host pushes one instance onto <c>LayoutTree.Overlays</c> and
/// calls <see cref="Sync"/> per frame (or on resize); hidden paint stays a
/// no-op so goldens are byte-identical while the viewer is closed. Skeleton
/// (viewport, <see cref="Sync"/>, the <see cref="Paint"/> guard chain, flags)
/// lives in <see cref="OverlayLayerBase{TOverlay}"/> (#1537).
///
/// <para><b>Key ingress is NOT the stack.</b> (#858) <see cref="OnKey"/> still
/// forwards a decoded key for any host that routes through
/// <see cref="OverlayStack.RouteKey"/>, and <see cref="IsModal"/> still declares
/// the barrier such a host would enforce — but no product code calls
/// <c>RouteKey</c>, and no product code calls <c>DiffViewerOverlay.Show</c>, so
/// this layer is never visible in the product and claims no key. The live
/// overlays are keyed by hand in <c>ReplInputLoop.HandleKeyAsync</c>.</para>
/// </summary>
public sealed class DiffViewerOverlayLayer : OverlayLayerBase<DiffViewerOverlay>
{
    /// <summary>Stable layer id (push replaces by id, moving to top).</summary>
    public const string LayerId = "diff";

    public DiffViewerOverlayLayer(DiffViewerOverlay overlay)
        : base(overlay, LayerId, opaque: true, hitTransparent: false, isModal: true)
    {
    }

    /// <summary>The fullscreen box is computed from the pushed viewport.</summary>
    protected override Rect ComputeBoundsCore(Rect viewport) => Overlay.ComputeBox(viewport);

    protected override bool IsShownCore => Overlay.Visible;

    /// <summary>
    /// The viewer additionally demands the box to fit its minimum dimensions —
    /// a shrunken fullscreen viewer on a tiny terminal stays hidden rather than
    /// painting a clipped skeleton. GEOMETRY, on top of the state-only
    /// <see cref="IsShownCore"/>.
    /// </summary>
    public override bool Visible =>
        Overlay.Visible &&
        Bounds.Width >= DiffViewerOverlay.MinWidth &&
        Bounds.Height >= DiffViewerOverlay.MinHeight;

    /// <summary>Routes a decoded key to the viewer (hunk nav, toggles, dismiss).</summary>
    public bool OnKey(in KeyEvent key) => Overlay.HandleKey(in key);

    protected override void PaintCore(ScreenBuffer buffer, Rect box) => Overlay.Paint(buffer, Viewport);
}
