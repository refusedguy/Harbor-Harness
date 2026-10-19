using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Seats the setup-guide checklist (issue #383) on the PRIM2a z-stack
/// (<see cref="LayoutTree.Overlays" />). Same contract as the other overlay
/// layers: <see cref="Visible" /> gates the paint (hidden layers never enter
/// the stack, so quiet frames stay byte-identical) and the box is opaque, so it
/// occludes the panels beneath. Deliberately not modal: the guide is read-only
/// and leaves the composer reachable, so it claims no key barrier —
/// <see cref="OnKey" /> still forwards a decoded key for hosts that route
/// through <see cref="OverlayStack.RouteKey" />. Skeleton (viewport,
/// <see cref="Sync"/>, the <see cref="Paint"/> guard chain, flags) lives in
/// <see cref="OverlayLayerBase{TOverlay}"/> (#1537).
/// </summary>
public sealed class SetupChecklistOverlayLayer : OverlayLayerBase<SetupChecklistOverlay>
{
    /// <summary>Stable layer id (push replaces by id, moving to top).</summary>
    public const string LayerId = "setup-checklist";

    public SetupChecklistOverlayLayer(SetupChecklistOverlay overlay)
        : base(overlay, LayerId, opaque: true, hitTransparent: false, isModal: false)
    {
    }

    /// <summary>The centered box is computed from the pushed viewport.</summary>
    protected override Rect ComputeBoundsCore(Rect viewport) => Overlay.ComputeBox(viewport);

    protected override bool IsShownCore => Overlay.Visible;

    /// <summary>Routes a decoded key to the checklist (Esc/q/? dismiss, Enter closes through).</summary>
    public override bool OnKey(in KeyEvent key) => Overlay.HandleKey(key);

    protected override void PaintCore(ScreenBuffer buffer, Rect box) => Overlay.Paint(buffer, Viewport);
}
