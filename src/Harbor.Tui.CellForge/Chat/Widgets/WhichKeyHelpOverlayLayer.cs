using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// PRIM11 seating (#306): <see cref="WhichKeyHelpOverlay"/> as an
/// <see cref="IOverlayLayer"/> on the PRIM2a z-stack. Modal and opaque —
/// occludes layers beneath and captures hit-tests, like the dialog layer.
/// The host pushes one instance onto <c>LayoutTree.Overlays</c> and calls
/// <see cref="Sync"/> per frame (or on resize); hidden paint stays a no-op so
/// goldens are byte-identical while the overlay is hidden.
/// </summary>
public sealed class WhichKeyHelpOverlayLayer : IOverlayLayer
{
    /// <summary>Stable layer id (push replaces by id, moving to top).</summary>
    public const string LayerId = "whichkey";

    private readonly WhichKeyHelpOverlay _overlay;
    private Rect _viewport = new(0, 0, 80, 24);

    public WhichKeyHelpOverlayLayer(WhichKeyHelpOverlay overlay)
    {
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
    }

    public string Id => LayerId;

    public Rect Bounds => _overlay.ComputeBox(_viewport);

    /// <summary>
    ///     True when the box fits the viewport at the minimum dimensions. This is a
    ///     GEOMETRY answer, not a state answer: it was inlined into
    ///     <see cref="Visible" /> below, which made <c>Visible</c> a second and
    ///     independent definition of "is this overlay showing" — so a shown overlay
    ///     reported <c>Visible == false</c> on a small terminal and the host could not
    ///     tell a too-small box from a hidden overlay. Now the two questions are
    ///     separately named and the layer has one authority:
    ///     <c>IsShown</c> for state (from <see cref="WhichKeyHelpOverlay" />) and this
    ///     for room. Both must hold to paint, which is what the old conjunction said —
    ///     only now it is legible.
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

    public bool Visible => _overlay.IsShown && HasRoom;

    public bool Opaque => true;

    public bool HitTransparent => false;

    /// <summary>Refreshes the viewport the centered box is computed from.</summary>
    public void Sync(Rect viewport) => _viewport = viewport;

    /// <summary>
    /// Paints the overlay for one <paramref name="clip"/> fragment. The layer
    /// sits above the base layout, so the clip is the full box in practice;
    /// fragmented clips repaint the box and rely on bottom-to-top order — the
    /// opaque layer above wins, so output stays correct.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect clip)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!_overlay.IsShown)
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
