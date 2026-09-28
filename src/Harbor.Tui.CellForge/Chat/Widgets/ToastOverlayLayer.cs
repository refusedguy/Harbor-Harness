using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// PRIM2c seating: <see cref="ToastOverlay"/> as an <see cref="IOverlayLayer"/>.
/// Non-opaque (never occludes layers beneath) and hit-transparent (pointer
/// events fall through). The host pushes one instance onto
/// <c>LayoutTree.Overlays</c> above the dialog layer and calls
/// <see cref="Sync"/> per frame; hidden paint stays a no-op.
/// </summary>
public sealed class ToastOverlayLayer : IOverlayLayer
{
    /// <summary>Stable layer id (push replaces by id, moving to top).</summary>
    public const string LayerId = "toast";

    private readonly ToastOverlay _overlay;
    private Rect _viewport = new(0, 0, 80, 24);

    public ToastOverlayLayer(ToastOverlay overlay)
    {
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
    }

    public string Id => LayerId;

    public Rect Bounds => _overlay.ComputeBounds(_viewport);

    public bool Visible => !_overlay.IsEmpty && Bounds.Width > 0 && Bounds.Height > 0;

    public bool Opaque => false;

    public bool HitTransparent => true;

    /// <summary>Refreshes the viewport the top-right strip is computed from.</summary>
    public void Sync(Rect viewport) => _viewport = viewport;

    /// <summary>
    /// Paints the toasts for one <paramref name="clip"/> fragment. Toasts sit
    /// topmost, so the clip is the full strip in practice; fragmented clips
    /// repaint the strip and rely on bottom-to-top order for correctness.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect clip)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (_overlay.IsEmpty)
        {
            return;
        }

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        if (bounds.Intersect(clip).Area == 0)
        {
            return;
        }

        _overlay.Paint(buffer, bounds);
    }
}
