using System.Buffers.Binary;
using System.IO.Pipelines;
using MessagePack;
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
///         The reader uses <see cref="PipeReader" /> for zero-copy async
///         reads; the writer uses a pooled <see cref="MemoryStream" />
///         substitute (just a <c>byte[]</c> + <see cref="Stream.WriteAsync" />
///         for simplicity — perf is not on the hot path for IPC).
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
    private static readonly MessagePackSerializerOptions WireOptions = MessagePackSerializerOptions.Standard;

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
        byte[] payload = MessagePackSerializer.Serialize(response, WireOptions, ct);
        await WriteFrameAsync(stream, payload, ct).ConfigureAwait(false);
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
        byte[] payload = MessagePackSerializer.Serialize(request, WireOptions, ct);
        await WriteFrameAsync(stream, payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Read one framed <see cref="HarborRequest" /> from the stream.
    ///     Returns <see langword="null" /> when the peer has closed the
    ///     connection cleanly.
    /// </summary>
    public static async Task<HarborRequest?> ReadRequestAsync(
        Stream stream,
        CancellationToken ct = default)
    {
        byte[]? payload = await ReadFrameAsync(stream, ct).ConfigureAwait(false);
        if (payload is null) return null;
        return MessagePackSerializer.Deserialize<HarborRequest>(payload, WireOptions, ct);
    }

    /// <summary>
    ///     Read one framed <see cref="HarborResponse" /> from the stream.
    ///     Returns <see langword="null" /> when the peer has closed the
    ///     connection cleanly.
    /// </summary>
    public static async Task<HarborResponse?> ReadResponseAsync(
        Stream stream,
        CancellationToken ct = default)
    {
        byte[]? payload = await ReadFrameAsync(stream, ct).ConfigureAwait(false);
        if (payload is null) return null;
        return MessagePackSerializer.Deserialize<HarborResponse>(payload, WireOptions, ct);
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

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken ct)
    {
        if (payload.Length > MaxFrameBytes)
        {
            throw new InvalidOperationException(
                $"Frame payload {payload.Length} bytes exceeds cap {MaxFrameBytes}");
        }

        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false))
        {
            return null;
        }

        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length == 0) return Array.Empty<byte>();
        if (length > MaxFrameBytes)
        {
            throw new InvalidOperationException(
                $"Incoming frame length {length} exceeds cap {MaxFrameBytes}");
        }

        byte[] payload = new byte[length];
        if (!await ReadExactAsync(stream, payload, ct).ConfigureAwait(false))
        {
            throw new EndOfStreamException(
                "Peer closed connection mid-frame");
        }

        return payload;
    }

    private static async Task<bool> ReadExactAsync(
        Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)
                .ConfigureAwait(false);
            if (n == 0) return total == 0;
            total += n;
        }
        return true;
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

    /// <summary>Logger category for transport-level events.</summary>
    public const string Transport = "Harbor.Ipc.Transport";
}
