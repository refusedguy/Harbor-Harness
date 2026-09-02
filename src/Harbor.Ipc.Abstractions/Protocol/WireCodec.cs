using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using MessagePack;
using MessagePack.Formatters;
namespace Harbor.Ipc.Protocol;
/// <summary>
///     Length-prefixed MessagePack framing for a bidirectional pipe/socket
///     stream. Used by both <c>MessagePackRpcServer</c> and
///     <c>MessagePackRpcClient</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Frame layout:</b>
///     </para>
///     <list type="number">
///         <item>
///             <c>uint32 BE length</c> — the number of bytes in the payload
///             that follows. Capped at <see cref="MaxFrameBytes" /> to bound
///             memory allocation under adversarial input.
///         </item>
///         <item>
///             <c>byte[length] payload</c> — MessagePack-serialized
///             <see cref="HarborRequest" /> or <see cref="HarborResponse" />.
///         </item>
///     </list>
///     <para>
///         <b>Zero-alloc framing (perf sprint):</b> the write path serializes
///         into a pooled <c>IBufferWriter</c> rented from
///         <see cref="ArrayPool{T}.Shared" /> — header and payload form one
///         contiguous buffer, emitted with a single <see cref="Stream.WriteAsync" />.
///         The read path frames through <see cref="PipeReader" /> and
///         deserializes straight from the pipe's buffer — no intermediate
///         <c>byte[]</c> is ever allocated for a frame. On a warm connection
///         the framing layer is allocation-free; only the MessagePack object
///         graph itself allocates.
///     </para>
/// </remarks>
public static class WireCodec
{
    /// <summary>
    ///     Hard cap on a single frame's payload size. 64 MiB — generous enough
    ///     for any plausible message history (10k messages × ~6 KB each) while
    ///     still bounded.
    /// </summary>
    public const int MaxFrameBytes = 64 * 1024 * 1024;

    /// <summary>Length-prefix header size in bytes.</summary>
    internal const int HeaderLength = 4;

    /// <summary>
    ///     Hardened MessagePack options shared by every codec operation.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Security:</b> deliberately StandardResolver-based. The
    ///         type-emitting (assembly-qualified-name) resolver was removed
    ///         from this codec: it let any local process that could reach the
    ///         socket instantiate arbitrary types by name from the wire
    ///         stream — a classic deserialization-gadget RCE surface. With
    ///         typed serialization the concrete target of every
    ///         <c>Deserialize</c> call is fixed at compile time by the
    ///         generic parameter, so a hostile frame can never name its own
    ///         types. Domain payloads (<see cref="OkResponse.Payload" />,
    ///         <see cref="EventEnvelope.EventBytes" />, event DTO fields)
    ///         flow through <see cref="SerializeDomain{T}" /> /
    ///         <see cref="DeserializeDomain{T}" /> which are typed the same
    ///         way; only the known contract + domain POCO shapes may be
    ///         passed there.
    ///     </para>
    /// </remarks>
    /// <summary>
    ///     Wire options: standard resolver composed with the IPC formatters for
    ///     domain models (A1, sprint 5). Session/SessionMetadata are
    ///     MemoryPack-annotated in Harbor.Abstractions.Contracts (formerly
    ///     Harbor.Domain) — MessagePack has no built-in
    ///     formatter for them, so every "get sessions" response crashed with
    ///     FormatterNotRegisteredException before this composition existed.
    /// </summary>
    private static readonly MessagePackSerializerOptions WireOptions =
        MessagePackSerializerOptions.Standard.WithResolver(
            MessagePack.Resolvers.CompositeResolver.Create(
                new IMessagePackFormatter[]
                {
                    new SessionMessagePackFormatter(),
                    new SessionMetadataMessagePackFormatter(),
                    new ProviderIdMessagePackFormatter(),
                    new ToolDescriptorMessagePackFormatter()
                },
                new[] { MessagePack.Resolvers.StandardResolver.Instance }));

    // ── Write path (pooled, single write) ──────────────────────────────────

    /// <summary>
    ///     Serialize and frame-write a <see cref="HarborResponse" /> to the
    ///     given stream. Flushes the stream after writing so server-pushed
    ///     events reach the client promptly.
    /// </summary>
    public static async Task WriteResponseAsync(
        Stream stream,
        HarborResponse response,
        CancellationToken ct = default)
    {
        using PooledFrameBuffer frame = PooledFrameBuffer.Rent();
        frame.Begin();
        MessagePackSerializer.Serialize(frame, response, WireOptions, ct);
        await WriteFrameBufferAsync(stream, frame, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Serialize and frame-write a <see cref="HarborRequest" /> to the
    ///     given stream.
    /// </summary>
    public static async Task WriteRequestAsync(
        Stream stream,
        HarborRequest request,
        CancellationToken ct = default)
    {
        using PooledFrameBuffer frame = PooledFrameBuffer.Rent();
        frame.Begin();
        MessagePackSerializer.Serialize(frame, request, WireOptions, ct);
        await WriteFrameBufferAsync(stream, frame, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Frame-write a raw payload: header + payload are copied into one
    ///     pooled buffer and emitted with a single <see cref="Stream.WriteAsync" />.
    ///     Steady-state allocation: zero (the buffer is rented and returned).
    /// </summary>
    public static async ValueTask WriteFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct = default)
    {
        if (payload.Length > MaxFrameBytes)
        {
            throw new InvalidOperationException(
                $"Frame payload {payload.Length} bytes exceeds cap {MaxFrameBytes}");
        }

        int total = HeaderLength + payload.Length;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)payload.Length);
            payload.Span.CopyTo(buffer.AsSpan(HeaderLength));
            await stream.WriteAsync(buffer.AsMemory(0, total), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async ValueTask WriteFrameBufferAsync(
        Stream stream,
        PooledFrameBuffer frame,
        CancellationToken ct)
    {
        if (frame.PayloadLength > MaxFrameBytes)
        {
            throw new InvalidOperationException(
                $"Frame payload {frame.PayloadLength} bytes exceeds cap {MaxFrameBytes}");
        }

        frame.Finish();
        await stream.WriteAsync(frame.FrameMemory, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    // ── Read path (PipeReader, zero-copy) ──────────────────────────────────

    /// <summary>
    ///     Frame-level read: waits for one complete frame and returns its
    ///     payload slice <b>without consuming it</b> — the caller must call
    ///     <c>reader.AdvanceTo(payload.End)</c> (or complete the reader)
    ///     before the next <see cref="ReadFrameAsync" /> call, and must finish
    ///     reading the slice before then as well. Returns
    ///     <see langword="null" /> on clean EOF at a frame boundary.
    /// </summary>
    public static async ValueTask<ReadOnlySequence<byte>?> ReadFrameAsync(
        PipeReader reader,
        CancellationToken ct = default)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync(ct).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (buffer.Length < HeaderLength)
            {
                // Clean EOF at a frame boundary (or with a partial header,
                // which the Stream-based codec also treated as a clean close).
                if (result.IsCompleted)
                {
                    reader.AdvanceTo(buffer.End);
                    return null;
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                continue;
            }

            uint length = ReadHeaderLength(buffer);
            if (length > MaxFrameBytes)
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
                throw new InvalidOperationException(
                    $"Incoming frame length {length} exceeds cap {MaxFrameBytes}");
            }

            if (buffer.Length < HeaderLength + length)
            {
                if (result.IsCompleted)
                {
                    reader.AdvanceTo(buffer.End);
                    throw new EndOfStreamException("Peer closed connection mid-frame");
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                continue;
            }

            return buffer.Slice(HeaderLength, length);
        }
    }

    /// <summary>
    ///     Read one framed <see cref="HarborRequest" /> from a persistent
    ///     <see cref="PipeReader" />. Returns <see langword="null" /> when the
    ///     peer has closed the connection cleanly at a frame boundary.
    /// </summary>
    /// <remarks>
    ///     <paramref name="reader" /> must be owned by the caller and persist
    ///     across calls (one per connection) — a per-call reader would discard
    ///     any bytes over-read past the current frame. Deserialization reads
    ///     directly from the pipe's pooled buffer; no payload copy is made.
    /// </remarks>
    public static async Task<HarborRequest?> ReadRequestAsync(
        PipeReader reader,
        CancellationToken ct = default)
        => await ReadFramedAsync<HarborRequest>(reader, ct).ConfigureAwait(false);

    /// <summary>
    ///     Read one framed <see cref="HarborResponse" /> from a persistent
    ///     <see cref="PipeReader" />. Returns <see langword="null" /> when the
    ///     peer has closed the connection cleanly at a frame boundary.
    /// </summary>
    /// <remarks>
    ///     <paramref name="reader" /> must be owned by the caller and persist
    ///     across calls (one per connection). Deserialization reads directly
    ///     from the pipe's pooled buffer; no payload copy is made.
    /// </remarks>
    public static async Task<HarborResponse?> ReadResponseAsync(
        PipeReader reader,
        CancellationToken ct = default)
        => await ReadFramedAsync<HarborResponse>(reader, ct).ConfigureAwait(false);

    /// <summary>
    ///     Stream-convenience overload. Creates a transient reader over
    ///     <paramref name="stream" /> for exactly one frame.
    /// </summary>
    /// <remarks>
    ///     Correct only for strict request-response (lock-step) streams with no
    ///     pipelined data: bytes over-read past the current frame are
    ///     discarded when the transient reader completes. Multiplexed
    ///     connections (e.g. the RPC client's interleaved event envelopes)
    ///     MUST use the <see cref="PipeReader" /> overload with one persistent
    ///     reader per connection instead.
    /// </remarks>
    public static async Task<HarborRequest?> ReadRequestAsync(
        Stream stream,
        CancellationToken ct = default)
    {
        PipeReader reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        try
        {
            return await ReadFramedAsync<HarborRequest>(reader, ct).ConfigureAwait(false);
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Stream-convenience overload. Creates a transient reader over
    ///     <paramref name="stream" /> for exactly one frame.
    /// </summary>
    /// <remarks>
    ///     Correct only for strict request-response (lock-step) streams with no
    ///     pipelined data: bytes over-read past the current frame are
    ///     discarded when the transient reader completes. Multiplexed
    ///     connections (e.g. the RPC client's interleaved event envelopes)
    ///     MUST use the <see cref="PipeReader" /> overload with one persistent
    ///     reader per connection instead.
    /// </remarks>
    public static async Task<HarborResponse?> ReadResponseAsync(
        Stream stream,
        CancellationToken ct = default)
    {
        PipeReader reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        try
        {
            return await ReadFramedAsync<HarborResponse>(reader, ct).ConfigureAwait(false);
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Read one framed <see cref="HarborRequest" /> /
    ///     <see cref="HarborResponse" /> — the shared framing core: wait for a
    ///     complete frame via <see cref="ReadFrameAsync" />, deserialize
    ///     straight from the pipe buffer, then consume the frame.
    /// </summary>
    private static async Task<T?> ReadFramedAsync<T>(PipeReader reader, CancellationToken ct)
        where T : class
    {
        ReadOnlySequence<byte>? payload = await ReadFrameAsync(reader, ct).ConfigureAwait(false);
        if (payload is null)
        {
            return null;
        }

        T value;
        try
        {
            value = MessagePackSerializer.Deserialize<T>(payload.Value, WireOptions, ct);
        }
        catch
        {
            // Consume the frame so the stream stays in sync — mirrors the
            // Stream-based codec, which had always fully read the payload
            // before deserializing.
            reader.AdvanceTo(payload.Value.End);
            throw;
        }

        reader.AdvanceTo(payload.Value.End);
        return value;
    }

    /// <summary>
    ///     Decode the big-endian length header. Fast path: the header lives in
    ///     one segment. The cross-segment case (≤ 3 continuation bytes) is a
    ///     rare fallback that never allocates.
    /// </summary>
    private static uint ReadHeaderLength(in ReadOnlySequence<byte> buffer)
    {
        ReadOnlySequence<byte> header = buffer.Slice(0, HeaderLength);
        if (header.IsSingleSegment)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(header.FirstSpan);
        }

        uint value = 0;
        foreach (ReadOnlyMemory<byte> segment in header)
        {
            foreach (byte b in segment.Span)
            {
                value = (value << 8) | b;
            }
        }

        return value;
    }

    /// <summary>
    ///     Serialize a domain object (e.g. <c>Session</c>, <c>AgentMessage</c>,
    ///     <c>ToolResult</c>, or a list of them) to typed MessagePack bytes for
    ///     use as a response <see cref="OkResponse.Payload" /> or an event
    ///     field. The wire form carries NO type metadata — the caller supplies
    ///     the concrete type via the generic parameter.
    /// </summary>
    /// <remarks>
    ///     Only known contract/domain POCO types should flow through this
    ///     helper; see <see cref="WireOptions" /> for the threat model this
    ///     closes. Signature and call sites are unchanged from the previous
    ///     (type-metadata-emitting) implementation — only the bytes on the
    ///     wire differ, and both IPC peers ship from the same build.
    /// </remarks>
    public static byte[] SerializeDomain<T>(T value, CancellationToken ct = default)
        => MessagePackSerializer.Serialize(value, WireOptions, ct);

    /// <summary>
    ///     Deserialize typed MessagePack bytes produced by
    ///     <see cref="SerializeDomain{T}" /> back into an instance of
    ///     <typeparamref name="T" />.
    /// </summary>
    /// <remarks>
    ///     Because the target type is fixed at compile time, deserialization
    ///     can never instantiate a type named by the stream — even if the
    ///     peer is hostile.
    /// </remarks>
    public static T? DeserializeDomain<T>(byte[]? bytes, CancellationToken ct = default)
    {
        if (bytes is null || bytes.Length == 0) return default;
        return MessagePackSerializer.Deserialize<T>(bytes, WireOptions, ct);
    }

    /// <summary>
    ///     Non-throwing variant of <see cref="DeserializeDomain{T}" />:
    ///     returns <see langword="false" /> when the bytes are absent or do
    ///     not decode into a valid <typeparamref name="T" />.
    /// </summary>
    public static bool TryDeserializeDomain<T>(byte[]? bytes, out T? value, CancellationToken ct = default)
    {
        value = default;
        if (bytes is null || bytes.Length == 0) return false;
        try
        {
            value = MessagePackSerializer.Deserialize<T>(bytes, WireOptions, ct);
            return true;
        }
        catch (MessagePackSerializationException)
        {
            return false;
        }
    }
}

/// <summary>
///     A single frame buffer rented from <see cref="ArrayPool{T}.Shared" />:
///     <c>[4-byte BE header][payload]</c>. Doubles as an
///     <see cref="IBufferWriter{T}" /> so MessagePack serializes directly
///     into the rented array — no intermediate output allocation. Grows by
///     re-renting a larger pooled array when the payload outgrows the current
///     one. The wrapper object itself is recycled through a per-thread
///     single-slot cache so the steady-state write path is allocation-free.
/// </summary>
internal sealed class PooledFrameBuffer : IBufferWriter<byte>, IDisposable
{
    private const int InitialCapacity = 1024;

    // Recycle cache is a separate static class: the analyzer forbids
    // instance-method writes to static fields (S2696).
    [ThreadStatic]
    private static PooledFrameBuffer? t_cached;

    private static PooledFrameBuffer? RentCached()
    {
        PooledFrameBuffer? cached = t_cached;
        t_cached = null;
        return cached;
    }

    private static void RecycleCached(PooledFrameBuffer instance) => t_cached ??= instance;

    private byte[] _buffer;
    private int _count;
    private bool _returned;

    private PooledFrameBuffer() => _buffer = ArrayPool<byte>.Shared.Rent(InitialCapacity);

    /// <summary>Rents a frame buffer (recycled instance when available).</summary>
    public static PooledFrameBuffer Rent()
    {
        PooledFrameBuffer? cached = RentCached();
        if (cached is not null)
        {
            cached._returned = false;
            return cached;
        }

        return new PooledFrameBuffer();
    }

    /// <summary>
    ///     Reserves the header bytes. Must be called once, before any payload
    ///     serialization, so header and payload end up contiguous.
    /// </summary>
    public void Begin() => _count = WireCodec.HeaderLength;

    /// <summary>Payload bytes serialized so far.</summary>
    public int PayloadLength => _count - WireCodec.HeaderLength;

    /// <summary>The whole frame (header + payload) as one contiguous memory.</summary>
    public Memory<byte> FrameMemory => _buffer.AsMemory(0, _count);

    /// <summary>Writes the payload length into the reserved header.</summary>
    public void Finish() => BinaryPrimitives.WriteUInt32BigEndian(_buffer, (uint)PayloadLength);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_returned)
        {
            return;
        }

        _returned = true;
        _count = 0;
        RecycleCached(this);
    }

    /// <inheritdoc />
    public Memory<byte> GetMemory(int sizeHint = 0) => EnsureCapacity(sizeHint).AsMemory(_count);

    /// <inheritdoc />
    public Span<byte> GetSpan(int sizeHint = 0) => EnsureCapacity(sizeHint).AsSpan(_count);

    /// <inheritdoc />
    public void Advance(int count) => _count += count;

    private byte[] EnsureCapacity(int sizeHint)
    {
        int needed = sizeHint <= 0 ? 256 : sizeHint;
        if (_count + needed <= _buffer.Length)
        {
            return _buffer;
        }

        int target = _buffer.Length;
        while (target < _count + needed)
        {
            target *= 2;
        }

        byte[] bigger = ArrayPool<byte>.Shared.Rent(target);
        _buffer.AsSpan(0, _count).CopyTo(bigger);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = bigger;
        return _buffer;
    }
}

/// <summary>
///     Convenience logger category name for IPC internals.
/// </summary>
public static class IpcLogCategories
{
    /// <summary>Logger category for the RPC server.</summary>
    public const string Server = "Harbor.Ipc.Server";

    /// <summary>Logger category for the RPC client.</summary>
    public const string Client = "Harbor.Ipc.Client";

    /// <summary>Logger category for the transport-level events.</summary>
    public const string Transport = "Harbor.Ipc.Transport";
}
