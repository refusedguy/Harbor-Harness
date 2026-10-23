// PooledLineBuffer.cs — the pooled IBufferWriter behind SessionFileIO.EncodeLine.
//
// WHY THIS EXISTS
// ---------------
// The JSONL append path used to ask the serializer for the payload bytes and
// then copy them into a SECOND array, one byte longer, to carry the newline:
// two allocations and a full copy of the payload for every message the
// session log keeps. The payload is now written straight into a POOLED buffer
// and copied ONCE, into the array that goes to disk.
//
// The buffer is rented from ArrayPool and the wrapper object is recycled
// through a per-thread single-slot cache, so the steady-state write path
// allocates nothing but the line itself — the same shape as the IPC frame
// buffer (PooledFrameBuffer), for the same reason.

using System.Buffers;

namespace Harbor.Storage.Jsonl;

/// <summary>
///     A pooled <see cref="IBufferWriter{T}" /> over a rented array: the
///     source-generated JSON writer serializes straight into it, so the
///     payload never needs an array of its own.
/// </summary>
/// <remarks>
///     One instance serves one line at a time. It is rented, written, read
///     back through <see cref="WrittenSpan" /> and disposed — the dispose
///     returns the array to the pool and hands the wrapper itself to the
///     per-thread cache, so the next line on the same thread starts with
///     neither an array nor an object of its own.
/// </remarks>
internal sealed class PooledLineBuffer : IBufferWriter<byte>, IDisposable
{
    /// <summary>
    ///     Initial pooled capacity — a typical JSONL line fits with room, and
    ///     a longer one grows the rent once (never per line).
    /// </summary>
    private const int InitialCapacity = 1024;

    // The recycle cache is a separate set of static methods: the analyzer
    // forbids instance-method writes to static fields (S2696).
    [ThreadStatic]
    private static PooledLineBuffer? t_cached;

    private static PooledLineBuffer? RentCached()
    {
        PooledLineBuffer? cached = t_cached;
        t_cached = null;
        return cached;
    }

    private static void RecycleCached(PooledLineBuffer instance) => t_cached ??= instance;

    private byte[] _buffer;
    private int _written;
    private bool _returned;

    private PooledLineBuffer() => _buffer = ArrayPool<byte>.Shared.Rent(InitialCapacity);

    /// <summary>Rents a buffer (the recycled wrapper when the thread has one).</summary>
    public static PooledLineBuffer Rent()
    {
        PooledLineBuffer? cached = RentCached();
        if (cached is not null)
        {
            // The disposed wrapper gave its array back to the pool, so the
            // next line starts from a fresh rent.
            cached._returned = false;
            cached._written = 0;
            cached._buffer = ArrayPool<byte>.Shared.Rent(InitialCapacity);
            return cached;
        }

        return new PooledLineBuffer();
    }

    /// <summary>The bytes written so far.</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    /// <summary>How many bytes were written.</summary>
    public int WrittenLength => _written;

    /// <inheritdoc />
    public void Advance(int count) => _written += count;

    /// <inheritdoc />
    public Memory<byte> GetMemory(int sizeHint = 0) => EnsureCapacity(sizeHint).AsMemory(_written);

    /// <inheritdoc />
    public Span<byte> GetSpan(int sizeHint = 0) => EnsureCapacity(sizeHint).AsSpan(_written);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_returned)
        {
            return;
        }

        _returned = true;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = Array.Empty<byte>();
        _written = 0;
        RecycleCached(this);
    }

    /// <summary>
    ///     The rented array, grown (by a re-rent) when the line does not fit
    ///     in what is left of it. The bytes already written are carried over.
    /// </summary>
    /// <remarks>
    ///     The array that was grown out of is NOT returned to the pool here.
    ///     The writer holds the memory it was given until the next one
    ///     replaces it, and a pool that hands the same array to another thread
    ///     inside that window is a data race — the copy below is the only
    ///     thing that reads the old array, and it happens before the array
    ///     goes out of reach. A growth is rare (the rent is 1 KB, and a
    ///     JSONL line is shorter than that in the common case), so letting
    ///     the collector take the old array costs nothing that matters.
    /// </remarks>
    private byte[] EnsureCapacity(int sizeHint)
    {
        int needed = sizeHint <= 0 ? 256 : sizeHint;
        if (_written + needed <= _buffer.Length)
        {
            return _buffer;
        }

        int target = _buffer.Length;
        while (target < _written + needed)
        {
            target *= 2;
        }

        byte[] bigger = ArrayPool<byte>.Shared.Rent(target);
        _buffer.AsSpan(0, _written).CopyTo(bigger);
        _buffer = bigger;
        return _buffer;
    }
}
