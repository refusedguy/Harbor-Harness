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
/// <c>OnKey</c> default, exactly like the dialog and toast layers.
/// </summary>
public sealed class MarkupOverlayLayer : IOverlayLayer
{
    /// <summary>Stable layer id (push replaces by id, moving to top).</summary>
    public const string LayerId = "markup";

    private readonly MarkupOverlay _overlay;
    private Rect _viewport = new(0, 0, 80, 24);

    public MarkupOverlayLayer(MarkupOverlay overlay)
    {
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
    }

    public string Id => LayerId;

    public Rect Bounds => _overlay.ComputeBox(_viewport);

    public bool Visible => _overlay.Visible && Bounds.Width > 0 && Bounds.Height > 0;

    public bool Opaque => true;

    public bool HitTransparent => false;

    /// <summary>
    /// Modal: while the markup session is up, keys stop at this layer and the
    /// panels beneath starve — an annotated screenshot must not be typed into.
    /// </summary>
    public bool IsModal => true;

    /// <summary>Refreshes the viewport the fullscreen box is computed from.</summary>
    public void Sync(Rect viewport) => _viewport = viewport;

    /// <summary>
    /// Paints the overlay for one <paramref name="clip" /> fragment. The layer
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
