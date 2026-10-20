using System.Text;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.TestKit;

/// <summary>
///     An in-memory <see cref="ISyncTerminalBackend" /> that captures every
///     frame the renderer ships.
/// </summary>
/// <remarks>
///     <para>
///         The terminal-backend seam had eleven hand-written implementations
///         across six projects, and they were four shapes: capture the bytes,
///         count them, absorb them, or implement only the async contract on
///         purpose. The names here are the ones the tree already used, so
///         the migration is a move rather than a rename.
///     </para>
///     <para>
///         <b>Sync-capable on purpose.</b>
///         <see cref="AnsiWriter" /> and
///         <see cref="Harbor.Tui.CellForge.Chat.Streaming.ScreenSession" />
///         take the sync path when the backend implements
///         <see cref="ISyncTerminalBackend" /> and the async path when it does
///         not. The two paths write the same bytes; what differs is whether a
///         frame can be shipped from a synchronous call site. A backend that
///         is sync-capable is therefore the one that exercises MORE of the
///         product, and a backend that is async-only is a deliberate statement
///         about the contract under test — which is why it gets its own type
///         (<see cref="AsyncOnlyBackend" />) instead of a flag.
///     </para>
/// </remarks>
public sealed class RecordingBackend : ISyncTerminalBackend
{
    private readonly List<byte[]> _writes = [];

    /// <summary>Every frame shipped, in order.</summary>
    public IReadOnlyList<byte[]> Writes => _writes;

    /// <summary>How many frames were shipped.</summary>
    public int WriteCount => _writes.Count;

    /// <summary>How many bytes were shipped in total.</summary>
    public long TotalBytes
    {
        get
        {
            long total = 0;
            foreach (byte[] write in _writes)
            {
                total += write.Length;
            }

            return total;
        }
    }

    /// <summary>Everything shipped so far, decoded as UTF-8 (escape characters kept raw).</summary>
    public string Text
    {
        get
        {
            var combined = new List<byte>();
            foreach (byte[] write in _writes)
            {
                combined.AddRange(write);
            }

            return Encoding.UTF8.GetString([.. combined]);
        }
    }

    /// <summary>
    ///     The captured text with control characters rendered visible
    ///     (<c>\e</c>, <c>\r</c>, <c>\n</c>), so a comparison never sees a raw
    ///     CR or LF.
    /// </summary>
    public string Escaped => Text
        .Replace("\u001B", "\\e", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    /// <summary>Forgets everything captured so far.</summary>
    public void Reset() => _writes.Clear();

    /// <summary>Forgets everything captured so far.</summary>
    public void ResetForTests() => Reset();

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        _writes.Add(bytes.ToArray());
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void Write(ReadOnlySpan<byte> bytes) => _writes.Add(bytes.ToArray());
}

/// <summary>
///     A <see cref="ISyncTerminalBackend" /> that counts frames and bytes
///     without copying them.
/// </summary>
/// <remarks>
///     The counting backends exist for allocation probes: a backend that
///     copied every frame would allocate the very thing the probe is
///     measuring. This one counts and forgets.
/// </remarks>
public sealed class CountingBackend : ISyncTerminalBackend
{
    /// <summary>How many frames were shipped.</summary>
    public int Writes { get; private set; }

    /// <summary>How many bytes were shipped in total.</summary>
    public long Bytes { get; private set; }

    /// <summary>Resets the counters.</summary>
    public void Reset()
    {
        Writes = 0;
        Bytes = 0;
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        Writes++;
        Bytes += bytes.Length;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void Write(ReadOnlySpan<byte> bytes)
    {
        Writes++;
        Bytes += bytes.Length;
    }
}

/// <summary>
///     A <see cref="ISyncTerminalBackend" /> that absorbs every frame.
/// </summary>
/// <remarks>
///     For the perf probes and benchmarks that measure the ENCODER rather
///     than the output device: the frame has to be produced, and nothing has
///     to happen to it afterwards.
/// </remarks>
public sealed class NullTerminalBackend : ISyncTerminalBackend
{
    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    /// <inheritdoc />
    public void Write(ReadOnlySpan<byte> bytes)
    {
    }
}

/// <summary>
///     A backend that implements the ASYNC contract only.
/// </summary>
/// <remarks>
///     <para>
///         The async-only backend is not a lesser backend; it is a statement
///         about the contract. <see cref="AnsiWriter.EndFrame" /> and
///         <c>ScreenSession.FlushFrame</c> throw on an async-only backend, and
///         that throw is the thing two of the tests exist to pin — so the type
///         has to exist, and it has to be distinguishable by name: the
///         product puts the backend's type name in the exception, and the
///         test asserts on it.
///     </para>
/// </remarks>
public sealed class AsyncOnlyBackend : ITerminalBackend
{
    private readonly List<byte[]> _writes = [];

    /// <summary>How many frames were shipped.</summary>
    public int Writes { get; private set; }

    /// <summary>Every frame shipped, in order.</summary>
    public IReadOnlyList<byte[]> Frames => _writes;

    /// <summary>How many bytes were shipped in total.</summary>
    public long TotalBytes
    {
        get
        {
            long total = 0;
            foreach (byte[] write in _writes)
            {
                total += write.Length;
            }

            return total;
        }
    }

    /// <summary>Everything shipped so far, decoded as UTF-8 (escape characters kept raw).</summary>
    public string Text
    {
        get
        {
            var combined = new List<byte>();
            foreach (byte[] write in _writes)
            {
                combined.AddRange(write);
            }

            return Encoding.UTF8.GetString([.. combined]);
        }
    }

    /// <summary>
    ///     The captured text with control characters rendered visible
    ///     (<c>\e</c>, <c>\r</c>, <c>\n</c>).
    /// </summary>
    public string Escaped => Text
        .Replace("\u001B", "\\e", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    /// <summary>Forgets everything captured so far.</summary>
    public void Reset()
    {
        _writes.Clear();
        Writes = 0;
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        Writes++;
        _writes.Add(bytes.ToArray());
        return ValueTask.CompletedTask;
    }
}
