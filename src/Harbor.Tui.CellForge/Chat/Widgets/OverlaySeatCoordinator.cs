using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
///     Owns the chat overlay state objects, their layers and the order in which
///     those layers are seated on a <see cref="LayoutTree"/>'s overlay stack.
///     Extracted from <see cref="ChatScreen"/> so the screen stays a record of
///     tree and panels.
///     <para>
///         The ordered <see cref="_seats"/> array is the only declaration of the
///         z-order: a new overlay is one row here plus its forwarding property on
///         <see cref="ChatScreen"/>. The order is pinned by the golden frames —
///         dialog, diff, setup, image, markup, floating pane, sheet, toasts.
///     </para>
/// </summary>
internal sealed class OverlaySeatCoordinator
{
    private readonly LayoutTree _tree;
    private readonly Seat[] _seats;

    private DialogOverlay? _dialog;
    private ToastOverlay? _toasts;
    private DiffViewerOverlay? _diffViewer;
    private SetupChecklistOverlay? _setupChecklist;
    private ImageViewerOverlay? _imageViewer;
    private MarkupOverlay? _markup;
    private FloatingTerminalController? _floatingTerminal;
    private SheetOverlay? _sheet;

    public OverlaySeatCoordinator(LayoutTree tree)
    {
        _tree = tree;

        var dialogLayer = new DialogOverlayLayer(Dialog);
        var diffLayer = new DiffViewerOverlayLayer(DiffViewer);
        var setupLayer = new SetupChecklistOverlayLayer(SetupChecklist);
        var imageLayer = new ImageViewerOverlayLayer(ImageViewer);
        var markupLayer = new MarkupOverlayLayer(Markup);
        var floatingLayer = new FloatingTerminalOverlayLayer(FloatingTerminal);
        var sheetLayer = new SheetOverlayLayer(Sheet);
        var toastLayer = new ToastOverlayLayer(Toasts);

        // Sync lives on OverlayLayerBase<T>, not on the IOverlayLayer seam, so
        // each seat carries its layer's method group alongside the layer itself.
        _seats =
        [
            new(DialogOverlayLayer.LayerId, dialogLayer, dialogLayer.Sync),
            new(DiffViewerOverlayLayer.LayerId, diffLayer, diffLayer.Sync),
            new(SetupChecklistOverlayLayer.LayerId, setupLayer, setupLayer.Sync),
            new(ImageViewerOverlayLayer.LayerId, imageLayer, imageLayer.Sync),
            new(MarkupOverlayLayer.LayerId, markupLayer, markupLayer.Sync),
            new(FloatingTerminalOverlayLayer.LayerId, floatingLayer, floatingLayer.Sync),
            new(SheetOverlayLayer.LayerId, sheetLayer, sheetLayer.Sync),
            new(ToastOverlayLayer.LayerId, toastLayer, toastLayer.Sync),
        ];
    }

    /// <summary>Modal dialog state (PRIM2c).</summary>
    public DialogOverlay Dialog => _dialog ??= new();

    /// <summary>Toast queue (PRIM2c).</summary>
    public ToastOverlay Toasts => _toasts ??= new();

    /// <summary>Fullscreen diff viewer state (PRIM12 #308).</summary>
    public DiffViewerOverlay DiffViewer => _diffViewer ??= new();

    /// <summary>Setup-guide checklist state (issue #383).</summary>
    public SetupChecklistOverlay SetupChecklist => _setupChecklist ??= new();

    /// <summary>Fullscreen image zoom viewer state (issue #387).</summary>
    public ImageViewerOverlay ImageViewer => _imageViewer ??= new();

    /// <summary>Screenshot-markup overlay state (issue #400).</summary>
    public MarkupOverlay Markup => _markup ??= new();

    /// <summary>Floating terminal pane state (issue #1491).</summary>
    public FloatingTerminalController FloatingTerminal => _floatingTerminal ??= new();

    /// <summary>Edge sheet state (issue #2376).</summary>
    public SheetOverlay Sheet => _sheet ??= new();

    /// <summary>
    ///     Reconciles the seated layers with <paramref name="viewport"/>.
    ///     Idempotent — safe to call every frame before
    ///     <see cref="LayoutTree.PaintAll"/>: every layer receives the viewport,
    ///     hidden layers are removed, visible ones are re-pushed in seat order.
    /// </summary>
    public void Sync(Rect viewport)
    {
        foreach (Seat seat in _seats)
        {
            seat.Sync(viewport);
        }

        foreach (Seat seat in _seats)
        {
            if (seat.Layer.Visible)
            {
                _tree.Overlays.Push(seat.Layer);
            }
            else
            {
                _tree.Overlays.Remove(seat.Id);
            }
        }
    }

    /// <summary>One z-order slot: stable id, the layer and its viewport sync.</summary>
    private sealed record Seat(string Id, IOverlayLayer Layer, Action<Rect> Sync);
}
