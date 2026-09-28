// ChunkedLineReader.cs — bounded, chunk-at-a-time JSONL record reader (#460).
//
// Both JSONL paths used to size one structure from the file length: the read
// path rented ArrayPool<byte>.Rent((int)fileLength) behind a "~2 GiB" guard —
// a size the pool cannot serve anyway, so a merely-large file bought a fresh
// LOH array of its own length — and the rewrite paths pulled the whole file
// into a List<string> while holding the per-session semaphore. A session file
// is attacker-influenced: every tool result the agent has ever seen is a
// record in it, so the file length, not the code, decided the peak working
// set.
//
// This reader never sizes from the file. It holds one pooled block plus the
// record currently being assembled, and a record that would grow past
// MaxRecordBytes is reported rather than assembled. The format is line
// oriented, so streaming costs nothing semantically: a block boundary inside
// a record is carried over, exactly as the single-buffer reader carried the
// whole file.

using System.Buffers;

namespace Harbor.Storage.Jsonl;

/// <summary>
///     Splits a byte stream into JSONL records without materializing the
///     stream. Peak memory is one pooled <see cref="ChunkBytes" /> block plus
///     the longest single record — never the file.
/// </summary>
internal sealed class ChunkedLineReader : IDisposable
{
    /// <summary>
    ///     Bytes pulled per refill. Big enough that an ordinary session is a
    ///     couple of refills, small enough that two of them still fit inside
    ///     <see cref="ArrayPool{T}" />.Shared's 1 MiB poolable tier, so the
    ///     common case really is a pooled array and not a fresh LOH one.
    /// </summary>
    internal const int ChunkBytes = 256 * 1024;

    /// <summary>
    ///     Size the block starts at: two chunks, so the first refill always
    ///     leaves a whole chunk of slack behind it. A block of exactly
    ///     <see cref="ChunkBytes" /> would have zero slack, and the very first
    ///     record that did not end on a chunk boundary would look like a carry
    ///     and force a grow — i.e. the block would double for nothing on
    ///     ordinary input. The slack is what makes "the block never grows"
    ///     true in practice rather than only in theory.
    /// </summary>
    internal const int InitialBlockBytes = 2 * ChunkBytes;

    /// <summary>
    ///     Ceiling on a single JSONL record. The largest payload a message can
    ///     legitimately carry is a <c>read</c> tool result, which
    ///     <c>ReadTool.MaxFileBytes</c> caps at 10 MiB, and JSON escaping
    ///     inflates that text further; 32 MiB leaves more than 3x headroom over
    ///     the worst legitimate record while sitting 64x below the 2 GiB buffer
    ///     it replaces. A record past it is corrupt or hostile — it is reported,
    ///     never assembled.
    /// </summary>
    internal const long MaxRecordBytes = 32L * 1024 * 1024;

    private readonly Stream _stream;
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(InitialBlockBytes);
    private int _filled; // valid bytes in _buffer
    private int _cursor; // first unconsumed byte
    private bool _overflow; // the record in progress already blew MaxRecordBytes
    private bool _yieldedAny;
    private bool _trailingTaken;

    internal ChunkedLineReader(Stream stream) => _stream = stream;

    /// <summary>
    ///     Stop after this many bytes even if the stream has more, so a read
    ///     stays bounded by the length measured on the open handle (#459) — the
    ///     ceiling bounds the <em>read</em>, the block bounds the
    ///     <em>allocation</em>, and neither is the file length.
    /// </summary>
    internal long ByteCeiling { get; set; } = long.MaxValue;

    /// <summary>Bytes actually pulled from the stream so far.</summary>
    internal long BytesRead { get; private set; }

    /// <summary>
    ///     Size of the block currently held. Exposed so the #460 bound — "the
    ///     block is sized by <see cref="ChunkBytes" /> and by nothing else" —
    ///     is assertable from a test rather than inferred from an allocation
    ///     total.
    /// </summary>
    internal int BufferSize => _buffer.Length;

    /// <summary>
    ///     True once a record has been dropped for exceeding
    ///     <see cref="MaxRecordBytes" />. Its bytes are skipped up to the next
    ///     LF, so the stream stays in sync and the rest of the file still
    ///     parses.
    /// </summary>
    internal bool SawOversizedRecord { get; private set; }

    /// <summary>
    ///     Refills the block. False at the ceiling or at end of stream — the
    ///     last record, if the file carried no terminator after it, is then
    ///     still in the block and comes out of
    ///     <see cref="TryGetTrailingRecord" />.
    /// </summary>
    internal async Task<bool> FillAsync(CancellationToken ct)
    {
        if (!MakeRoom())
            return false;

        int n = await _stream.ReadAsync(_buffer.AsMemory(_filled, Room), ct).ConfigureAwait(false);
        return Absorb(n);
    }

    /// <summary>
    ///     Synchronous <see cref="FillAsync" />, for the rewrite paths, which
    ///     stream inside the per-session semaphore and have no await of their
    ///     own to fold a span into.
    /// </summary>
    internal bool Fill()
    {
        if (!MakeRoom())
            return false;

        return Absorb(_stream.Read(_buffer, _filled, Room));
    }

    /// <summary>
    ///     Slides the unconsumed tail to the front so a record split across two
    ///     refills is contiguous again, and makes room for a whole chunk. False
    ///     once <see cref="ByteCeiling" /> is reached.
    /// </summary>
    private bool MakeRoom()
    {
        if (BytesRead >= ByteCeiling)
            return false;

        if (_cursor > 0)
        {
            int tail = _filled - _cursor;
            if (tail > 0)
            {
                _buffer.AsSpan(_cursor, tail).CopyTo(_buffer);
            }

            _filled = tail;
            _cursor = 0;
        }

        // Only the CARRY makes free space genuinely scarce: a record ending
        // mid-block is the normal case and must not grow anything, which is why
        // the block starts with a second chunk's worth of slack. When the carry
        // really is most of the block, grow to carry + one chunk so the rest of
        // an oversized record arrives in a single read rather than a trickle.
        //
        // Never grow past what the ceiling still owes, or a long carry plus a
        // long remainder would walk the block up a chunk per refill. The carry
        // is itself dropped the moment it passes MaxRecordBytes, so the block
        // never exceeds that plus one chunk.
        int want = (int)Math.Min(ChunkBytes, ByteCeiling - BytesRead);
        if (_buffer.Length - _filled >= want)
            return true;

        var grown = ArrayPool<byte>.Shared.Rent(_filled + want);
        _buffer.AsSpan(0, _filled).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = grown;
        return true;
    }

    /// <summary>Books the bytes a read just produced. False at end of stream.</summary>
    private bool Absorb(int read)
    {
        if (read <= 0)
            return false;

        _filled += read;
        BytesRead += read;
        return true;
    }

    /// <summary>
    ///     Free bytes in the block, clamped to what the ceiling still owes.
    ///     Always at least 1 after a successful <see cref="MakeRoom" />.
    /// </summary>
    private int Room => (int)Math.Min(_buffer.Length - _filled, ByteCeiling - BytesRead);

    /// <summary>
    ///     Next LF-terminated record, with a leading UTF-8 BOM (first record
    ///     only) and any trailing CR already stripped. False when the block
    ///     holds no further complete record.
    /// </summary>
    internal bool TryGetRecord(out ReadOnlySpan<byte> record)
    {
        while (_cursor < _filled)
        {
            int nl = _buffer.AsSpan(_cursor, _filled - _cursor).IndexOf((byte)'\n');
            if (nl < 0)
            {
                // No terminator in what we hold: the record continues in the
                // next block, unless it has already blown the ceiling.
                if (!_overflow && _filled - (long)_cursor > MaxRecordBytes)
                {
                    SawOversizedRecord = true;
                    _overflow = true;
                }

                if (_overflow)
                {
                    // Give up on the tail instead of carrying it. Drop mode is
                    // sticky: the next LF ends the record we abandoned.
                    _cursor = _filled;
                }

                record = default;
                return false;
            }

            ReadOnlySpan<byte> line = _buffer.AsSpan(_cursor, nl);
            _cursor += nl + 1;

            if (_overflow)
            {
                // This LF terminates the record we gave up on.
                _overflow = false;
                continue;
            }

            record = Trim(line);
            _yieldedAny = true;
            return true;
        }

        record = default;
        return false;
    }

    /// <summary>
    ///     The final record, for a file that does not end in LF. Call once,
    ///     after the last <see cref="FillAsync" /> returned false.
    /// </summary>
    internal bool TryGetTrailingRecord(out ReadOnlySpan<byte> record)
    {
        record = default;
        if (_trailingTaken)
            return false;

        _trailingTaken = true;
        if (_cursor >= _filled || _overflow)
        {
            _cursor = _filled;
            return false;
        }

        // Bounded by _filled, NOT by the block: the tail of a block is whatever
        // the pool handed back past the bytes we actually read, and those bytes
        // are stale from a previous tenant.
        ReadOnlySpan<byte> tail = _buffer.AsSpan(_cursor, _filled - _cursor);
        _cursor = _filled;
        record = Trim(tail);
        return true;
    }

    /// <summary>Returns the block to the pool. Safe to call twice.</summary>
    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);

        // Blanked so a second Dispose cannot hand the same array to the pool
        // twice — the double-return hazard the rent-before-return ordering in
        // MakeRoom exists to avoid.
        _buffer = [];
    }

    /// <summary>Strips the UTF-8 BOM from the first record and any trailing CR.</summary>
    private ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> line)
    {
        if (!_yieldedAny && line.StartsWith("\xEF\xBB\xBF"u8))
        {
            line = line[3..];
        }

        if (line.Length > 0 && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        return line;
    }
}
