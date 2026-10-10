using System.Runtime.InteropServices;
using Harbor.Tui.CellForge.Capabilities;
using Harbor.Ui.Framework.Rendering;

// #436: same-namespace capture as LayoutTree — the engine's verbatim Rect
// port shadows the Rendering vocabulary in this namespace, but this layer
// implements the Rendering IInlineImageSink, so Rect is pinned through UIR.
// InlineImageKind/InlineImageEncoder/AnsiWriter stay engine-typed: they have
// no Rendering twins and never cross into Rendering-typed APIs.
using UIR = Harbor.Ui.Framework.Rendering;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Engine implementation of <see cref="IInlineImageSink" /> (KILLER_FEATURES
/// §2.7 Feature 12, issue #387): collects the frame's image placements during
/// paint and emits them as escape sequences through the frame's
/// <see cref="AnsiWriter" />, i.e. INSIDE the composed frame byte span. There
/// is exactly one backend write per frame and no <c>Console.Write</c> path that
/// bypasses the cell-diff engine.
///
/// <para><b>Why the bytes stay out of dirty-rect accounting.</b> A placement
/// never writes cells — the block leaves its rect blank (or with its own
/// caption) and the terminal composites the bitmap there. So
/// <see cref="DiffEngine.Flush" /> sees no change, the row hashes stay stable,
/// and repaint/occlusion logic is unchanged by construction. The corollary is
/// that a *moved* image repaints because the cells under it changed, which is
/// the behaviour we want: the diff is the single source of truth for what the
/// screen holds.</para>
///
/// <para>Placement list is a preallocated ring with a hard cap
/// (<see cref="MaxPlacements" />) so a pathological frame cannot allocate; the
/// cap drops the surplus rather than growing, keeping the steady-state frame
/// allocation-free.</para>
///
/// <para>Render-thread only — the same discipline as
/// <see cref="OverlayStack" />. Not thread-safe.</para>
/// </summary>
public sealed class InlineImageLayer : IInlineImageSink
{
    /// <summary>Per-frame placement cap; the surplus is dropped, never queued.</summary>
    public const int MaxPlacements = 32;

    private readonly InlinePlacement[] _placements = new InlinePlacement[MaxPlacements];
    private int _count;

    // Last emitted frame, so a steady-state repaint re-sends nothing. Both
    // protocols draw INTO the cell grid (iTerm2 inline images and kitty's
    // a=T placements are grid content, erased by the next write to those
    // cells), so an unchanged placement keeps painting itself — re-transmitting
    // megabytes of base64 at frame rate would be the single most expensive
    // thing this feature could do.
    private readonly UIR.Rect[] _lastRects = new UIR.Rect[MaxPlacements];
    private readonly byte[]?[] _lastPayloads = new byte[]?[MaxPlacements];
    private UIR.Rect _lastFrame;
    private bool _hasLastFrame;

    /// <summary>
    /// Probe result for this session — the one fallback switch. Set once at
    /// startup from <c>InlineImageProbe.Detect()</c>; <see cref="None" /> (the
    /// default) keeps every block on its text card.
    /// </summary>
    public InlineImageKind Kind { get; set; } = InlineImageKind.None;

    /// <summary>Frame the placements are clipped to (set per frame by the host).</summary>
    public UIR.Rect FrameBounds { get; set; } = new(0, 0, 80, 24);

    /// <inheritdoc />
    public bool Enabled => Kind != InlineImageKind.None;

    /// <summary>Placements queued for the current frame (diagnostics/tests).</summary>
    public int Count => _count;

    /// <summary>Placements dropped because the cap was hit this frame.</summary>
    public int DroppedCount { get; private set; }

    /// <summary>Borrows the placement at <paramref name="index" /> (test seam).</summary>
    internal InlinePlacement PlacementAt(int index) => _placements[index];

    /// <summary>
    /// Drops the frame's placements and the drop counter. Called at frame start
    /// by the host (<see cref="ScreenSession.BeginFrame" />), so a placement
    /// never survives into a frame whose timeline no longer paints the block.
    /// </summary>
    public void BeginFrame()
    {
        _count = 0;
        DroppedCount = 0;
    }

    /// <summary>
    /// Forgets what was last emitted, so the next <see cref="Emit" /> sends
    /// everything again. Call when the terminal's own state may no longer hold
    /// the bitmaps (a resize reflow, a hot buffer swap, alt-screen re-entry).
    /// </summary>
    public void InvalidateEmitted()
    {
        _hasLastFrame = false;
        Array.Clear(_lastPayloads);
        Array.Clear(_lastRects);
    }

    /// <inheritdoc />
    public bool TryEncode(string name, string mimeType, ReadOnlySpan<byte> data, int cols, int rows, out byte[]? payload)
    {
        payload = null;
        if (!Enabled || !InlineImageEncoder.Supports(Kind, mimeType))
        {
            return false;
        }

        payload = InlineImageEncoder.Encode(Kind, name, data, cols, rows);
        return payload is { Length: > 0 };
    }

    /// <inheritdoc />
    public UIR.Rect ClipToFrame(UIR.Rect cellRect) => cellRect.Intersect(FrameBounds);

    /// <inheritdoc />
    public void Place(UIR.Rect cellRect, ReadOnlyMemory<byte> payload)
    {
        if (!Enabled || payload.IsEmpty)
        {
            return;
        }

        // Defensive clip — a block scrolled half off the timeline must not
        // position the bitmap at a row the frame does not have. Callers are
        // expected to have run ClipToFrame before encoding, so in practice this
        // is a no-op and the aspect ratio the payload asked for still holds.
        UIR.Rect clipped = cellRect.Intersect(FrameBounds);
        if (clipped.Area == 0)
        {
            return;
        }

        if (_count >= MaxPlacements)
        {
            DroppedCount++;
            return;
        }

        _placements[_count++] = new InlinePlacement(clipped, payload);
    }

    /// <summary>
    /// Emits the placements that CHANGED since the last frame, each preceded by
    /// a cursor move to its origin. The payload is borrowed, never copied.
    /// Call after the cell diff so the bitmap composites over the
    /// (already-current) cells. Placements identical to the previous frame —
    /// same rect, same cached payload instance — are skipped: the terminal
    /// still holds that bitmap, and re-sending it would cost megabytes per
    /// frame for a picture that has not moved.
    /// </summary>
    public void Emit(AnsiWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        // A geometry change reflows the terminal's own cell grid, so nothing
        // can be assumed to still be where we left it: resend the lot.
        bool geometryChanged = !_hasLastFrame || _lastFrame != FrameBounds;
        int emitted = 0;

        for (int i = 0; i < _count; i++)
        {
            var placement = _placements[i];
            byte[]? payload = Backing(placement.Payload);

            if (!geometryChanged
                && i < _lastPayloads.Length
                && _lastRects[i] == placement.Cells
                && ReferenceEquals(_lastPayloads[i], payload))
            {
                continue;
            }

            writer.MoveTo(placement.Cells.X, placement.Cells.Y);
            writer.RawBytes(placement.Payload.Span);
            if (i < _lastRects.Length)
            {
                _lastRects[i] = placement.Cells;
                _lastPayloads[i] = payload;
            }

            emitted++;
        }

        if (emitted > 0)
        {
            // The pen is unknowable from here (kitty C=1 does not move it,
            // OSC 1337 does): drop it rather than let the next frame elide a
            // MoveTo against a guess.
            writer.InvalidateCursorPosition();
            _lastFrame = FrameBounds;
            _hasLastFrame = true;
        }

        // Placements past the current count are gone from screen only when the
        // cells under them were rewritten — which the diff just did. Forget the
        // tail so a re-added block re-emits instead of being deduped away.
        for (int i = _count; i < _lastPayloads.Length; i++)
        {
            _lastPayloads[i] = null;
            _lastRects[i] = default;
        }

        _count = 0;
    }

    /// <summary>The array behind a memory view, for reference-identity dedupe.</summary>
    private static byte[]? Backing(ReadOnlyMemory<byte> memory) =>
        MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment) ? segment.Array : null;

    /// <summary>One frame's image placement: a clipped cell rect + borrowed bytes.</summary>
    internal readonly record struct InlinePlacement(UIR.Rect Cells, ReadOnlyMemory<byte> Payload);
}
