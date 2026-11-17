using Ensure = Harbor.Abstractions.Extensions.Ensure;
using Harbor.Tui.CellForge.Capabilities;
using Harbor.Ui.Framework.Rendering;

// #436: same-namespace capture as LayoutTree — the engine's verbatim Rect
// port shadows the Rendering vocabulary in this namespace, but this layer
// implements the Rendering IInlineImageSink, so Rect is pinned through UIR.
// InlineImageKind/InlineImageEncoder/AnsiWriter stay engine-typed: they have
// no Rendering twins and never cross into Rendering-typed APIs.
using UIR = Harbor.Ui.Framework.Rendering;
using EngineCells = Harbor.Tui.CellForge.Rendering;

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
/// <para><b>Identity, z-order and delete (issue #2222).</b> Each distinct image
/// bytes gets a stable kitty image id, keyed by content hash (the Crush
/// <c>HasTransmitted</c> pattern): the same payload never re-transmits, even
/// when the block recreates its byte array. Placements carry a z-index — emit
/// order is ascending z (higher paints last, i.e. on top) and the cells under
/// a placement are marked <see cref="EngineCells.CellDiffOption.Skip" /> so
/// the cell diff cannot overdraw it. When an id leaves the frame, an explicit
/// delete-by-id sequence clears it, so no bitmap artifact survives.</para>
///
/// <para>Placement lists and caches are preallocated rings with a hard cap
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

    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private readonly InlinePlacement[] _placements = new InlinePlacement[MaxPlacements];
    private int _count;

    // Last emitted frame, so a steady-state repaint re-sends nothing. Both
    // protocols draw INTO the cell grid (iTerm2 inline images and kitty's
    // a=T placements are grid content, erased by the next write to those
    // cells), so an unchanged placement keeps painting itself — re-transmitting
    // megabytes of base64 at frame rate would be the single most expensive
    // thing this feature could do.
    private readonly UIR.Rect[] _lastRects = new UIR.Rect[MaxPlacements];
    private readonly ulong[] _lastHashes = new ulong[MaxPlacements];
    private readonly uint[] _lastIds = new uint[MaxPlacements];
    private readonly int[] _lastZ = new int[MaxPlacements];
    private UIR.Rect _lastFrame;
    private bool _hasLastFrame;

    // Content-hash → image id (kitty). A given byte payload keeps its id for
    // the whole session, which is what makes delete-by-id and transmission
    // dedupe stable across frames and byte-array recreations.
    private readonly ImageIdentity[] _identities = new ImageIdentity[MaxPlacements];
    private uint _nextImageId = 1;

    // Encoded payload hash → id/z, so Place (which only receives bytes) can
    // recover the identity stamped into the envelope by TryEncode.
    private readonly EncodedIdentity[] _encoded = new EncodedIdentity[MaxPlacements];
    private int _encodedCursor;

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
    /// The id↔content map survives: re-transmission reuses the same ids.
    /// </summary>
    public void InvalidateEmitted()
    {
        _hasLastFrame = false;
        Array.Clear(_lastHashes);
        Array.Clear(_lastRects);
        Array.Clear(_lastIds);
        Array.Clear(_lastZ);
    }

    /// <inheritdoc />
    public bool TryEncode(string name, string mimeType, ReadOnlySpan<byte> data, int cols, int rows, out byte[]? payload) =>
        TryEncode(name, mimeType, data, cols, rows, 0, out payload);

    /// <summary>
    /// Z-aware twin of the interface method: <paramref name="zIndex" /> is
    /// stamped into the kitty envelope (<c>z=</c>) and carried by the
    /// placement, so emit order and skip application can honour stacking.
    /// </summary>
    public bool TryEncode(
        string name,
        string mimeType,
        ReadOnlySpan<byte> data,
        int cols,
        int rows,
        int zIndex,
        out byte[]? payload)
    {
        payload = null;
        if (!Enabled || !InlineImageEncoder.Supports(Kind, mimeType))
        {
            return false;
        }

        uint id = Kind == InlineImageKind.KittyApc ? IdentityFor(data, zIndex) : 0;
        payload = InlineImageEncoder.Encode(Kind, name, data, cols, rows, id, zIndex);
        if (id != 0 && payload is { Length: > 0 })
        {
            RememberEncoded(Hash(payload), id, zIndex);
        }

        return payload is { Length: > 0 };
    }

    /// <inheritdoc />
    public UIR.Rect ClipToFrame(UIR.Rect cellRect) => cellRect.Intersect(FrameBounds);

    /// <inheritdoc />
    public void Place(UIR.Rect cellRect, ReadOnlyMemory<byte> payload) => Place(cellRect, payload, 0);

    /// <summary>
    /// Z-aware twin of the interface method. A non-zero
    /// <paramref name="zIndex" /> wins over the z recovered from the encoded
    /// envelope, so a modal overlay can lift an already-encoded picture above
    /// the feed without re-encoding it.
    /// </summary>
    public void Place(UIR.Rect cellRect, ReadOnlyMemory<byte> payload, int zIndex)
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

        uint id = 0;
        int z = zIndex;
        if (Kind == InlineImageKind.KittyApc)
        {
            ulong payloadHash = Hash(payload.Span);
            foreach (var encoded in _encoded)
            {
                if (encoded.Hash == payloadHash && encoded.Hash != 0)
                {
                    id = encoded.Id;
                    if (z == 0)
                    {
                        z = encoded.Z;
                    }

                    break;
                }
            }
        }

        _placements[_count++] = new InlinePlacement(clipped, payload, id, z);
    }

    /// <summary>
    /// Marks every cell under this frame's placements as
    /// <see cref="EngineCells.CellDiffOption.Skip" /> so the cell diff cannot
    /// draw over a bitmap (issue #2222 z-order). Called by the host between
    /// copying the painted grid into BACK and diffing it — Skip mirrors
    /// silently, so FRONT still matches BACK after the frame.
    /// </summary>
    public void ApplySkip(EngineCells.ScreenBuffer buffer)
    {
        Ensure.NotNull(buffer);
        for (int i = 0; i < _count; i++)
        {
            var cells = _placements[i].Cells;
            int right = Math.Min(cells.Right, buffer.Cols);
            int bottom = Math.Min(cells.Bottom, buffer.Rows);
            for (int y = Math.Max(0, cells.Y); y < bottom; y++)
            {
                for (int x = Math.Max(0, cells.X); x < right; x++)
                {
                    buffer.SetDiffOption(x, y, EngineCells.CellDiffOption.Skip);
                }
            }
        }
    }

    /// <summary>
    /// Emits deletes for ids that disappeared, then the placements that
    /// CHANGED since the last frame, each preceded by a cursor move to its
    /// origin. The payload is borrowed, never copied. Placements are emitted
    /// in ascending z order; an identical (rect, payload, id, z) tuple is
    /// skipped — the terminal still holds that bitmap. Content hashing makes
    /// the dedupe stable even when the block rebuilds its byte array.
    /// </summary>
    public void Emit(AnsiWriter writer)
    {
        Ensure.NotNull(writer);

        // A geometry change reflows the terminal's own cell grid, so nothing
        // can be assumed to still be where we left it: resend the lot.
        bool geometryChanged = !_hasLastFrame || _lastFrame != FrameBounds;
        int emitted = 0;

        emitted += EmitDeletes(writer);

        // Stable ascending-z emit order: lower stacking values are painted
        // first so higher ones composite on top. stackalloc keeps this
        // allocation-free on the frame hot path.
        Span<int> order = stackalloc int[MaxPlacements];
        for (int i = 0; i < _count; i++)
        {
            order[i] = i;
        }

        for (int i = 1; i < _count; i++)
        {
            int key = order[i];
            int j = i - 1;
            while (j >= 0 && _placements[order[j]].Z > _placements[key].Z)
            {
                order[j + 1] = order[j];
                j--;
            }

            order[j + 1] = key;
        }

        for (int n = 0; n < _count; n++)
        {
            int i = order[n];
            var placement = _placements[i];
            ulong hash = Hash(placement.Payload.Span);

            if (!geometryChanged
                && _lastHashes[i] == hash
                && _lastRects[i] == placement.Cells
                && _lastIds[i] == placement.ImageId
                && _lastZ[i] == placement.Z)
            {
                continue;
            }

            writer.MoveTo(placement.Cells.X, placement.Cells.Y);
            writer.RawBytes(placement.Payload.Span);
            _lastRects[i] = placement.Cells;
            _lastHashes[i] = hash;
            _lastIds[i] = placement.ImageId;
            _lastZ[i] = placement.Z;
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
        for (int i = _count; i < MaxPlacements; i++)
        {
            _lastHashes[i] = 0;
            _lastRects[i] = default;
            _lastIds[i] = 0;
            _lastZ[i] = 0;
        }

        _count = 0;
    }

    /// <summary>
    /// Kitty only: delete every id that was on screen last frame but is not
    /// in this frame's placements. Emitted BEFORE the new placements so a
    /// replacement image never flashes over its predecessor's stale bitmap.
    /// </summary>
    private int EmitDeletes(AnsiWriter writer)
    {
        if (Kind != InlineImageKind.KittyApc || !_hasLastFrame)
        {
            return 0;
        }

        int emitted = 0;
        for (int i = 0; i < MaxPlacements; i++)
        {
            uint id = _lastIds[i];
            if (id == 0 || StillVisible(id))
            {
                continue;
            }

            writer.RawBytes(Graphics.KittyDeleteImage(id));
            _lastIds[i] = 0;
            emitted++;
        }

        return emitted;
    }

    private bool StillVisible(uint id)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_placements[i].ImageId == id)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>FNV-1a over the payload bytes — the Crush <c>HasTransmitted</c>
    /// ingredient: same bytes ⇒ same hash ⇒ one transmission, one id.</summary>
    private static ulong Hash(ReadOnlySpan<byte> bytes)
    {
        ulong hash = FnvOffsetBasis;
        foreach (byte b in bytes)
        {
            hash = (hash ^ b) * FnvPrime;
        }

        return hash;
    }

    /// <summary>Stable identity for a source payload: reuse an existing id on
    /// a hash hit, otherwise hand out the next one from the 1-based counter.</summary>
    private uint IdentityFor(ReadOnlySpan<byte> data, int zIndex)
    {
        ulong hash = Hash(data);
        foreach (var identity in _identities)
        {
            if (identity.Hash == hash && identity.Hash != 0)
            {
                return identity.Id;
            }
        }

        uint id = _nextImageId++;
        for (int i = 0; i < _identities.Length; i++)
        {
            if (_identities[i].Hash == 0)
            {
                _identities[i] = new ImageIdentity(hash, id, zIndex);
                break;
            }
        }

        return id;
    }

    private void RememberEncoded(ulong hash, uint id, int zIndex)
    {
        _encoded[_encodedCursor] = new EncodedIdentity(hash, id, zIndex);
        _encodedCursor = (_encodedCursor + 1) % _encoded.Length;
    }

    /// <summary>One frame's image placement: a clipped cell rect, borrowed
    /// bytes, and the identity/stacking metadata carried by the envelope.</summary>
    internal readonly record struct InlinePlacement(UIR.Rect Cells, ReadOnlyMemory<byte> Payload, uint ImageId = 0, int Z = 0);

    private readonly record struct ImageIdentity(ulong Hash, uint Id, int Z);
    private readonly record struct EncodedIdentity(ulong Hash, uint Id, int Z);
}
