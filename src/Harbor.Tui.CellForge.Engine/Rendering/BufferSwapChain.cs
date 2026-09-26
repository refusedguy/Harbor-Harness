namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Immutable BACK/FRONT buffer pair handed between threads as one unit
/// (renderer-moat hot-swap runtime): publishing is a single reference write,
/// so a consumer either sees the whole pair or none of it — a torn half-pair
/// is unrepresentable.
/// </summary>
public sealed class BufferPair
{
    public BufferPair(ScreenBuffer back, ScreenBuffer front)
    {
        Back = back ?? throw new ArgumentNullException(nameof(back));
        Front = front ?? throw new ArgumentNullException(nameof(front));
    }

    /// <summary>Painter target of the incoming frame.</summary>
    public ScreenBuffer Back { get; }

    /// <summary>Terminal mirror paired with <see cref="Back" />.</summary>
    public ScreenBuffer Front { get; }
}

/// <summary>
/// Lock-free ScreenBuffer handoff for the hot-swap runtime (renderer-moat T2):
/// producers publish a freshly painted or re-geometry'd <see cref="BufferPair" />
/// from any thread; the render loop adopts it at the next frame boundary —
/// the loop never stops, blocks or locks. Synchronization is volatile reads +
/// Interlocked CAS exclusively (no Monitor anywhere), so under load there is
/// no lock contention by construction.
///
/// Publication is last-writer-wins: a newer offer displaces a pending one
/// (swap events are rare resize/attach/theme moments, not steady-state
/// traffic — the displaced pair is dropped to the pool by its producer).
/// The slot pool recycles retired buffers with bounded memory and zero
/// per-rent allocations within retained capacity.
/// </summary>
public sealed class BufferSwapChain
{
    private const int PoolCapacity = 8;

    // Free-list slots: null = free. Claim = Exchange(slot, null),
    // deposit = CAS(slot, buffer, null). Fixed array → no per-op allocation.
    private readonly ScreenBuffer?[] _pool = new ScreenBuffer?[PoolCapacity];

    // Single-slot offer. Reads are volatile; the take is a CAS check-and-clear.
    private BufferPair? _pending;

    /// <summary>Atomically publishes <paramref name="offer" />; a newer publish replaces a pending one.</summary>
    public void Publish(BufferPair offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        Volatile.Write(ref _pending, offer);
    }

    /// <summary>Takes the pending offer atomically (null when none is pending
    /// or a concurrent consumer won the race — callers simply re-check next frame).</summary>
    public BufferPair? TryTake()
    {
        var offer = Volatile.Read(ref _pending);
        if (offer is null || Interlocked.CompareExchange(ref _pending, null, offer) != offer)
        {
            return null;
        }

        return offer;
    }

    /// <summary>
    /// Claims a pooled buffer (resized to <paramref name="cols"/>×<paramref name="rows"/>,
    /// blanked) or allocates a fresh one when the pool is empty. Resizing within
    /// retained capacity is allocation-free.
    /// </summary>
    public ScreenBuffer Rent(int cols, int rows)
    {
        for (int i = 0; i < _pool.Length; i++)
        {
            var rented = Interlocked.Exchange(ref _pool[i], null);
            if (rented is not null)
            {
                rented.Resize(cols, rows);
                return rented;
            }
        }

        return new ScreenBuffer(cols, rows);
    }

    /// <summary>Deposits a buffer for reuse. Bounded: an overflow deposit is dropped to the GC.</summary>
    public void Return(ScreenBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        for (int i = 0; i < _pool.Length; i++)
        {
            if (Interlocked.CompareExchange(ref _pool[i], buffer, null) is null)
            {
                return;
            }
        }
    }
}
