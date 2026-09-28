namespace Harbor.Ui.Framework.Rendering;

/// <summary>
/// Renderer seam for "paint a real image here" (KILLER_FEATURES §2.7 Feature 12).
/// A chat block that holds image bytes asks the sink to encode and place them
/// into a cell rect instead of writing a text card; the concrete sink (the
/// CellForge engine) owns protocol detection, encoding and emission, and
/// reports <see cref="Enabled"/> false in every environment that cannot draw
/// inline graphics — pipes, CI, tmux/screen, plain xterm — so the caller keeps
/// its text fallback and never branches on the protocol itself.
///
/// <para>Two operations rather than one, because the cache belongs to the
/// <em>block</em> and the protocol belongs to the <em>sink</em>:
/// <see cref="TryEncode" /> is called on a geometry change only (the block
/// memoizes its result and releases it with itself), while
/// <see cref="Place" /> is the zero-work per-frame call. Neither copies the
/// image bytes per frame — the encoded array is built once and borrowed.</para>
///
/// <para>Placements never touch <see cref="ScreenBuffer"/> cells, which is what
/// keeps the escape payloads out of dirty-rect and overlay-occlusion
/// accounting: the cell diff stays the single source of truth for repaints,
/// and the terminal erases an image exactly when the diff rewrites the cells
/// underneath it.</para>
/// </summary>
public interface IInlineImageSink
{
    /// <summary>
    /// True when this session can actually draw images. False ⇒ callers must
    /// keep their text card; this is the single fallback switch for the whole
    /// feature (tmux/screen included — the probe refuses there by design).
    /// </summary>
    bool Enabled { get; }

    /// <summary>
    /// Encodes <paramref name="data"/> for placement into a
    /// <paramref name="cols"/>×<paramref name="rows"/> cell box. Returns false
    /// when the protocol cannot carry this payload or the box is degenerate —
    /// the caller then keeps its text card. Callers are expected to memoize the
    /// result per geometry; this is not a per-frame call.
    /// </summary>
    /// <param name="name">File name shown by the terminal.</param>
    /// <param name="mimeType">Source MIME — some protocols carry PNG only.</param>
    /// <param name="data">Raw image bytes.</param>
    /// <param name="cols">Destination width in cells.</param>
    /// <param name="rows">Destination height in cells.</param>
    /// <param name="payload">Encoded escape sequence, owned by the caller.</param>
    bool TryEncode(string name, string mimeType, ReadOnlySpan<byte> data, int cols, int rows, out byte[]? payload);

    /// <summary>
    /// Clamps <paramref name="cellRect"/> to the current frame. Callers MUST
    /// run their destination box through this BEFORE
    /// <see cref="TryEncode" />: a payload encodes the box it is told to fill,
    /// so a rect clipped afterwards would ask the terminal to stretch the
    /// bitmap over rows the frame no longer has. An empty result means the
    /// placement is fully off-frame — skip it.
    /// </summary>
    Rect ClipToFrame(Rect cellRect);

    /// <summary>
    /// Places an already-encoded <paramref name="payload"/> into
    /// <paramref name="cellRect"/> for the current frame. The rect is clipped
    /// to the frame defensively; a placement entirely outside is dropped. The
    /// payload is borrowed for the frame only — implementations must not
    /// retain it.
    /// </summary>
    void Place(Rect cellRect, ReadOnlyMemory<byte> payload);
}
