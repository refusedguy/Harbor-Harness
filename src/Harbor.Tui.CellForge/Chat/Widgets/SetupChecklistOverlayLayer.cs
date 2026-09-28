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
/// through <see cref="OverlayStack.RouteKey" />.
/// </summary>
public sealed class SetupChecklistOverlayLayer : IOverlayLayer
{
    /// <summary>Stable layer id (push replaces by id, moving to top).</summary>
    public const string LayerId = "setup-checklist";

    private readonly SetupChecklistOverlay _overlay;
    private Rect _viewport = new(0, 0, 80, 24);

    public SetupChecklistOverlayLayer(SetupChecklistOverlay overlay)
    {
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
    }

    public string Id => LayerId;

    public Rect Bounds => _overlay.ComputeBox(_viewport);

    public bool Visible => _overlay.Visible && Bounds.Width > 0 && Bounds.Height > 0;

    public bool Opaque => true;

    public bool HitTransparent => false;

    /// <summary>
    ///     Read-only guide — no input barrier (mirrors the other overlay
    ///     layers, which declare it explicitly rather than leaning on the
    ///     <c>IOverlayLayer.IsModal</c> default member: a default interface
    ///     member is not reachable through the concrete layer type).
    /// </summary>
    public bool IsModal => false;

    /// <summary>Routes a decoded key to the checklist (Esc/q/? dismiss, Enter closes through).</summary>
    public bool OnKey(in KeyEvent key) => _overlay.HandleKey(key);

    /// <summary>Refreshes the viewport the centered box is computed from.</summary>
    public void Sync(Rect viewport) => _viewport = viewport;

    /// <summary>
    /// Paints the checklist for one <paramref name="clip" /> fragment. The
    /// layer sits above the base layout, so the clip is the full box in
    /// practice; fragmented clips repaint the box and rely on bottom-to-top
    /// order — the opaque layer above wins, so output stays correct.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect clip)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!_overlay.Visible)
        {
            return;
        }

        var box = Bounds;
        if (box.Width <= 0 || box.Height <= 0)
        {
            return;
        }

        if (box.Intersect(clip).Area == 0)
        {
            return;
        }

        _overlay.Paint(buffer, _viewport);
    }
}
