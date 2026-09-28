using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// PRIM12 seating (#308): <see cref="DiffViewerOverlay"/> as an
/// <see cref="IOverlayLayer"/> on the PRIM2a z-stack. Modal and opaque — the
/// fullscreen viewer occludes layers beneath, wins hit-tests over the full
/// viewport, and forms a TGui input barrier (<see cref="IOverlayLayer.IsModal"/>)
/// so panels beneath starve while it is visible. Keys route top-down through
/// <see cref="OnKey"/> (btea message-routing pattern). The host pushes one
/// instance onto <c>LayoutTree.Overlays</c> and calls <see cref="Sync"/> per
/// frame (or on resize); hidden paint stays a no-op so goldens are
/// byte-identical while the viewer is closed.
/// </summary>
public sealed class DiffViewerOverlayLayer : IOverlayLayer
{
    /// <summary>Stable layer id (push replaces by id, moving to top).</summary>
    public const string LayerId = "diff";

    private readonly DiffViewerOverlay _overlay;
    private Rect _viewport = new(0, 0, 80, 24);

    public DiffViewerOverlayLayer(DiffViewerOverlay overlay)
    {
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
    }

    public string Id => LayerId;

    public Rect Bounds => _overlay.ComputeBox(_viewport);

    public bool Visible =>
        _overlay.Visible &&
        Bounds.Width >= DiffViewerOverlay.MinWidth &&
        Bounds.Height >= DiffViewerOverlay.MinHeight;

    public bool Opaque => true;

    public bool HitTransparent => false;

    public bool IsModal => true;

    /// <summary>Routes a decoded key to the viewer (hunk nav, toggles, dismiss).</summary>
    public bool OnKey(in KeyEvent key) => _overlay.HandleKey(in key);

    /// <summary>Refreshes the viewport the fullscreen box is computed from.</summary>
    public void Sync(Rect viewport) => _viewport = viewport;

    /// <summary>
    /// Paints the viewer for one <paramref name="clip"/> fragment. The layer
    /// is fullscreen and opaque, so the clip is the viewport minus the
    /// transparent toasts above it in practice; fragmented clips repaint the
    /// box and rely on bottom-to-top order — the opaque layer above wins, so
    /// output stays correct.
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
