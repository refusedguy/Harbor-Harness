using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Seats the fullscreen image zoom viewer (KILLER_FEATURES §2.7 Feature 12,
/// issue #387) on the PRIM2a z-stack (<see cref="LayoutTree.Overlays" />).
/// Same contract as the other overlay layers: <see cref="Visible" /> gates the
/// paint (a hidden layer never enters the stack, so quiet frames stay
/// byte-identical), the box is opaque so it occludes the chat beneath, and
/// <see cref="IsModal" /> forms the TGui input barrier that keeps keys from
/// reaching the agent while the viewer is open.
/// </summary>
public sealed class ImageViewerOverlayLayer : IOverlayLayer
{
    /// <summary>Stable layer id (push replaces by id, moving to top).</summary>
    public const string LayerId = "image-viewer";

    private readonly ImageViewerOverlay _overlay;
    private Rect _viewport = new(0, 0, 80, 24);

    public ImageViewerOverlayLayer(ImageViewerOverlay overlay)
    {
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
    }

    public string Id => LayerId;

    public Rect Bounds => _overlay.ComputeBox(_viewport);

    public bool Visible => _overlay.Visible && Bounds.Width > 0 && Bounds.Height > 0;

    public bool Opaque => true;

    public bool HitTransparent => false;

    /// <summary>
    /// Modal: while the viewer is up, keys stop at this layer and the panels
    /// beneath starve — a zoomed screenshot must not be typed into.
    /// </summary>
    public bool IsModal => true;

    /// <summary>Routes a decoded key to the viewer (zoom, dismiss).</summary>
    public bool OnKey(in KeyEvent key) => _overlay.HandleKey(key);

    /// <summary>Refreshes the viewport the fullscreen box is computed from.</summary>
    public void Sync(Rect viewport) => _viewport = viewport;

    /// <summary>
    /// Paints the viewer for one <paramref name="clip" /> fragment. The layer
    /// is fullscreen and opaque; fragmented clips repaint the box and rely on
    /// bottom-to-top order — the opaque layer above wins.
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
