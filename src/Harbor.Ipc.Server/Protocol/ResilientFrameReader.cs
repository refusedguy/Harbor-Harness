using System.Buffers.Binary;

namespace Harbor.Ipc.Protocol;

/// <summary>
///     Outcome of one <see cref="ResilientFrameReader.ReadRequestAsync" /> attempt.
///     Protocol-level problems are surfaced as outcomes instead of exceptions so the
///     per-connection read loop can decide between skip-and-continue and close.
/// </summary>
internal enum FrameReadOutcome
{
    /// <summary>A well-formed frame was read and decoded into a request.</summary>
    Request,

    /// <summary>Peer closed the stream (at a frame boundary, mid-header, or mid-payload).</summary>
    StreamEnded,

    /// <summary>Zero-length frame. The stream stays in sync — safe to skip and continue.</summary>
    EmptyFrame,

    /// <summary>Payload bytes were fully consumed but failed to decode as a
    /// <see cref="HarborRequest" />. The stream stays in sync — safe to skip and continue.</summary>
    UndecodableFrame,

    /// <summary>Declared frame length exceeds <see cref="WireCodec.MaxFrameBytes" />.
    /// The payload was not consumed — framing is unrecoverable; close the connection.</summary>
    OversizedFrame
}

/// <summary>Result of one framed-read attempt; see <see cref="FrameReadOutcome" />.</summary>
internal readonly record struct FrameReadResult(FrameReadOutcome Outcome, HarborRequest? Request, Exception? Error);

/// <summary>
///     Length-prefixed frame reader for the RPC server read loop, mirroring the wire
///     format of <see cref="WireCodec.ReadRequestAsync" /> (uint32 big-endian length
///     header followed by a MessagePack <see cref="HarborRequest" /> payload) but with
///     per-failure classification instead of exception-based control flow.
/// </summary>
/// <remarks>
///     <b>Framing contract:</b> this reader must stay byte-compatible with
///     <c>WireCodec</c>, which is the source of truth for the frame layout; the frame
///     size cap is shared via <see cref="WireCodec.MaxFrameBytes" />. A malformed or
///     empty frame must never kill a connection — only terminal stream conditions
///     (<see cref="OperationCanceledException" />, <see cref="System.IO.IOException" />)
///     propagate as exceptions.
/// </remarks>
internal static class ResilientFrameReader
{
    /// <summary>
    ///     Read one framed <see cref="HarborRequest" />, classifying every failure mode:
    ///     clean EOF or truncated frame → <see cref="FrameReadOutcome.StreamEnded" />,
    ///     zero-length frame → <see cref="FrameReadOutcome.EmptyFrame" />,
    ///     undecodable payload → <see cref="FrameReadOutcome.UndecodableFrame" />,
    ///     over-cap length → <see cref="FrameReadOutcome.OversizedFrame" />.
    /// </summary>
    public static async Task<FrameReadResult> ReadRequestAsync(Stream stream, CancellationToken ct = default)
    {
        byte[] header = new byte[4];
        int headerBytes = await ReadExactAsync(stream, header, ct).ConfigureAwait(false);
        if (headerBytes < header.Length)
            return new(FrameReadOutcome.StreamEnded, null, null);

        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length == 0)
            return new(FrameReadOutcome.EmptyFrame, null, null);

        if (length > WireCodec.MaxFrameBytes)
        {
            return new(
                FrameReadOutcome.OversizedFrame,
                null,
                new InvalidOperationException($"Incoming frame length {length} exceeds cap {WireCodec.MaxFrameBytes}"));
        }

        byte[] payload = new byte[length];
        int payloadBytes = await ReadExactAsync(stream, payload, ct).ConfigureAwait(false);
        if (payloadBytes < payload.Length)
            return new(FrameReadOutcome.StreamEnded, null, null);

        try
        {
            var request = MessagePackSerializer.Deserialize<HarborRequest>(payload, cancellationToken: ct);
            return new(FrameReadOutcome.Request, request, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(FrameReadOutcome.UndecodableFrame, null, ex);
        }
    }

    /// <summary>
    ///     Read exactly <c>buffer.Length</c> bytes. Returns the number of bytes actually
    ///     read; a value less than <c>buffer.Length</c> means the peer closed mid-read.
    /// </summary>
    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)
                .ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
