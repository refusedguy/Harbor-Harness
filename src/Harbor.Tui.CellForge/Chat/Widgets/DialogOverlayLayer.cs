using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// PRIM2c seating: <see cref="DialogOverlay"/> as an <see cref="IOverlayLayer"/>.
/// Modal and opaque — occludes layers beneath and captures hit-tests. The host
/// pushes one instance onto <c>LayoutTree.Overlays</c> and calls
/// <see cref="Sync"/> per frame (or on resize); empty paint stays a no-op so
/// goldens are byte-identical while the dialog is hidden.
/// </summary>
public sealed class DialogOverlayLayer : IOverlayLayer
{
    /// <summary>Stable layer id (push replaces by id, moving to top).</summary>
    public const string LayerId = "dialog";

    private readonly DialogOverlay _overlay;
    private Rect _viewport = new(0, 0, 80, 24);

    public DialogOverlayLayer(DialogOverlay overlay)
    {
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
    }

    public string Id => LayerId;

    public Rect Bounds => _overlay.ComputeBox(_viewport);

    public bool Visible => _overlay.Visible && Bounds.Width > 0 && Bounds.Height > 0;

    public bool Opaque => true;

    public bool HitTransparent => false;

    /// <summary>Refreshes the viewport the centered box is computed from.</summary>
    public void Sync(Rect viewport) => _viewport = viewport;

    /// <summary>
    /// Paints the dialog for one <paramref name="clip"/> fragment. The stack
    /// gives full-bounds clips in the single-opaque fast path (dialog bottom,
    /// toast transparent on top); fragmented clips repaint the box and rely on
    /// bottom-to-top order — the opaque layer above wins, so output stays correct.
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
