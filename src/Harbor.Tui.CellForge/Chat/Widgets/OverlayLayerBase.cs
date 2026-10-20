using Harbor.Tui.CellForge.Rendering;
using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Shared <see cref="IOverlayLayer"/> skeleton (#1537): the viewport field +
/// <see cref="Sync"/>, the guard chain of <see cref="Paint"/> and the
/// <see cref="Id"/> / <see cref="Opaque"/> / <see cref="HitTransparent"/> /
/// <see cref="IsModal"/> bookkeeping are written once here; an overlay seat is
/// reduced to its deltas — how its box is computed
/// (<see cref="ComputeBoundsCore"/>), when it has content
/// (<see cref="IsShownCore"/>) and how the guarded box is painted
/// (<see cref="PaintCore"/>). The seat flags arrive as constructor arguments;
/// the id stays a <c>public const</c> on the seat, so hosts and tests can keep
/// spelling <c>SeatLayer.LayerId</c>.
/// </summary>
/// <remarks>
///     No new extension point: the TemplateMethod only collapses the eight
///     byte-identical copies of the skeleton inside the existing
///     <see cref="IOverlayLayer"/> seam — the seam itself is untouched, so
///     nothing is declared in <c>PluginAxisRegistry</c>. Per-seat <c>OnKey</c>
///     declarations deliberately stay on the seats — the interface ships a
///     default (<c>=&gt; false</c>) and the key-plane census guard
///     (tests/Harbor.Architecture.Tests) derives each layer's fact row from the
///     seat file that declares it.
/// </remarks>
public abstract class OverlayLayerBase<TOverlay> : IOverlayLayer
    where TOverlay : class
{
    private readonly TOverlay _overlay;
    private readonly string _id;
    private readonly bool _opaque;
    private readonly bool _hitTransparent;
    private readonly bool _isModal;
    private Rect _viewport = new(0, 0, 80, 24);

    /// <summary>
    ///     <paramref name="overlay"/> is the model the seat paints (the host owns
    ///     the instance); the flags are the seat's constants from its PRIM2a/2c
    ///     seating contract (toast: non-opaque, hit-transparent; the box-shaped
    ///     seats: opaque, capturing).
    /// </summary>
    protected OverlayLayerBase(
        TOverlay overlay,
        string id,
        bool opaque,
        bool hitTransparent,
        bool isModal = false)
    {
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _id = id ?? throw new ArgumentNullException(nameof(id));
        _opaque = opaque;
        _hitTransparent = hitTransparent;
        _isModal = isModal;
    }

    /// <summary>The overlay/model this seat wraps and paints.</summary>
    protected TOverlay Overlay => _overlay;

    /// <summary>The last viewport pushed by <see cref="Sync"/>.</summary>
    protected Rect Viewport => _viewport;

    /// <inheritdoc />
    public string Id => _id;

    /// <inheritdoc />
    public Rect Bounds => ComputeBoundsCore(_viewport);

    /// <summary>
    ///     Screen-space box the layer may paint within, computed from the pushed
    ///     viewport (each seat decides the shape: top-right strip, centred box,
    ///     fullscreen takeover, centred palette).
    /// </summary>
    protected abstract Rect ComputeBoundsCore(Rect viewport);

    /// <summary>
    ///     The state half of <see cref="Visible"/>: "does this layer have content
    ///     to draw right now". Geometry (size, room) is applied on top of it,
    ///     which is what keeps paint guards byte-identical with the copies this
    ///     base replaced — a seat may pass the state check and still be skipped
    ///     by the size guard below it.
    /// </summary>
    protected abstract bool IsShownCore { get; }

    /// <inheritdoc />
    public virtual bool Visible => IsShownCore && Bounds.Width > 0 && Bounds.Height > 0;

    /// <inheritdoc />
    public bool Opaque => _opaque;

    /// <inheritdoc />
    public bool HitTransparent => _hitTransparent;

    /// <inheritdoc />
    public bool IsModal => _isModal;

    /// <summary>
    ///     Offered a decoded key, top-down by <see cref="OverlayStack.RouteKey"/>
    ///     (ENG4 #275). The interface ships an observe-only default
    ///     (<c>=&gt; false</c>); a <c>virtual</c> here maps IOverlayLayer.OnKey
    ///     to class dispatch, so a seat that overrides it is reachable through
    ///     the stack: a derived class's own member does NOT implicitly
    ///     re-implement an interface it only inherits — with <see cref="IOverlayLayer"/>
    ///     listed only here, the three seats that used to declare
    ///     <c>public bool OnKey</c> routed to the interface default and the
    ///     RouteKey tests failed (#1537 CI, three seats). Key-reading seats
    ///     (diff, image, setup-checklist, jump palette) therefore declare
    ///     <c>public override bool OnKey</c>; observe-only seats (dialog, toast,
    ///     markup, whichkey) inherit this <c>false</c> and stay unable to read
    ///     a key — the same fact-count the key-plane census asserts.
    /// </summary>
    public virtual bool OnKey(in KeyEvent key) => false;

    /// <summary>
    ///     Refreshes the viewport the seat's box is computed from. The host calls
    ///     this per frame (or on resize); hidden paint stays a no-op, so quiet
    ///     frames and goldens stay byte-identical.
    /// </summary>
    public void Sync(Rect viewport) => _viewport = viewport;

    /// <summary>
    ///     TemplateMethod — the guard chain every seat used to spell by hand:
    ///     null-check → shown-check (<see cref="IsShownCore"/>) → zero-size
    ///     (<see cref="Bounds"/>) → <c>Intersect(clip).Area == 0</c>, then the
    ///     seat's delegate (<see cref="PaintCore"/>) with the guarded box.
    ///     Fragmented clips repaint the fragment and rely on the stack's
    ///     bottom-to-top paint order, so the chain itself is seat-independent.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect clip)
    {
        Ensure.NotNull(buffer);
        if (!IsShownCore)
        {
            return;
        }

        Rect box = Bounds;
        if (box.Width <= 0 || box.Height <= 0)
        {
            return;
        }

        if (box.Intersect(clip).Area == 0)
        {
            return;
        }

        PaintCore(buffer, box);
    }

    /// <summary>
    ///     Paints the layer into the already-guarded fragment.
    ///     <paramref name="box"/> is the layer's <see cref="Bounds"/>; each seat
    ///     paints from whatever coordinates it already has — the strip paints its
    ///     own box, the box-shaped overlays repaint from the pushed viewport.
    /// </summary>
    protected abstract void PaintCore(ScreenBuffer buffer, Rect box);
}
