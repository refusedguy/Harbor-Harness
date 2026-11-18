using System.Buffers;
using System.Buffers.Text;
using System.Text;
using System.Text.Json;

namespace Harbor.Lsp;

/// <summary>
///     The product's Content-Length framing over a pair of streams. The
///     framing code moved here verbatim from <see cref="LspClient" /> (#2613):
///     pooled header and body buffers, case-insensitive Content-Length
///     parsing, the single-writer header scratch and source-generated body
///     deserialization are unchanged, so the wire format is 1:1.
/// </summary>
/// <remarks>
///     The write lock is the only writer and lives here with the header
///     scratch it protects. <see cref="LspClient" /> serializes all writes
///     before they reach this type; disposing the transport disposes that
///     lock, which is the only resource this type owns.
/// </remarks>
internal sealed class LspFrameTransport : ILspFrameTransport
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _oneByte = new byte[1]; // reused header probe — the read loop is the only reader
    private readonly byte[] _headerScratch = new byte[32]; // formatted + written under _writeLock, never shared

    public LspFrameTransport(Stream input, Stream output)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    /// <inheritdoc />
    public async Task WriteFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        // #180: frame the pooled UTF-8 bytes directly — no string payload,
        // no re-encode per frame. The header is formatted into scratch space
        // under the write lock (the only writer), then both parts stream out.
        // "Content-Length: " (16) + up to 10 digits + "\r\n\r\n" (4) fits 32 bytes.
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            const int PrefixLength = 16;
            "Content-Length: "u8.CopyTo(_headerScratch);
            if (!payload.Length.TryFormat(_headerScratch.AsSpan(PrefixLength), out int digits))
                throw new InvalidOperationException("LSP frame is too large to frame.");
            _headerScratch[PrefixLength + digits] = (byte)'\r';
            _headerScratch[PrefixLength + digits + 1] = (byte)'\n';
            _headerScratch[PrefixLength + digits + 2] = (byte)'\r';
            _headerScratch[PrefixLength + digits + 3] = (byte)'\n';

            await _output.WriteAsync(_headerScratch.AsMemory(0, PrefixLength + digits + 4), ct).ConfigureAwait(false);
            await _output.WriteAsync(payload, ct).ConfigureAwait(false);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<JsonElement?> ReadFrameAsync(CancellationToken ct)
    {
        int contentLength = await ReadHeadersAsync(ct).ConfigureAwait(false);
        byte[] body = ArrayPool<byte>.Shared.Rent(contentLength);
        try
        {
            await ReadExactlyAsync(body, contentLength, ct).ConfigureAwait(false);
            // Same DOM as JsonDocument.Parse, but through the source-generated
            // JsonElement info (PERF-002): the element is self-contained, so
            // no document lifetime is tracked by the read loop. Malformed
            // bodies still throw JsonException — the loop logs and aborts the
            // session exactly as before.
            return JsonSerializer.Deserialize(body.AsSpan(0, contentLength), LspJsonContext.Default.JsonElement);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(body);
        }
    }

    public ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private async ValueTask<int> ReadHeadersAsync(CancellationToken ct)
    {
        // Header blocks are tiny (~40 bytes): pooled buffer, byte-wise scan
        // for the \r\n\r\n terminator, Content-Length parsed from the span —
        // no List<byte>, no header string, no Split (#180).
        byte[] rented = ArrayPool<byte>.Shared.Rent(64);
        int count = 0;
        try
        {
            while (true)
            {
                int b = await ReadByteAsync(ct).ConfigureAwait(false);
                if (b < 0) throw new EndOfStreamException("LSP stream ended while reading headers");

                if (count >= rented.Length)
                {
                    if (rented.Length >= 16_384)
                        throw new InvalidOperationException("LSP header block exceeded 16 KB.");
                    byte[] grown = ArrayPool<byte>.Shared.Rent(rented.Length * 2);
                    Buffer.BlockCopy(rented, 0, grown, 0, count);
                    ArrayPool<byte>.Shared.Return(rented);
                    rented = grown;
                }

                rented[count++] = (byte)b;
                if (count >= 4
                    && rented[count - 4] == (byte)'\r' && rented[count - 3] == (byte)'\n'
                    && rented[count - 2] == (byte)'\r' && rented[count - 1] == (byte)'\n')
                {
                    break;
                }

                if (count > 16_384)
                {
                    throw new InvalidOperationException("LSP header block exceeded 16 KB.");
                }
            }

            return ParseContentLength(rented.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Find the <c>Content-Length</c> line (case-insensitive, like the old
    /// string Split version) and parse its value from the span. An
    /// unparseable value is ignored in favor of later lines, exactly as
    /// <c>int.TryParse</c> failing did before.
    /// </summary>
    private static int ParseContentLength(ReadOnlySpan<byte> headers)
    {
        while (!headers.IsEmpty)
        {
            int eol = headers.IndexOf("\r\n"u8);
            ReadOnlySpan<byte> line = eol < 0 ? headers : headers[..eol];
            headers = eol < 0 ? default : headers[(eol + 2)..];

            if (line.Length > "Content-Length:".Length
                && Ascii.EqualsIgnoreCase(line[.."Content-Length:".Length], "Content-Length:"u8))
            {
                // int.TryParse accepted an explicit '+' sign; Utf8Parser may
                // not, so strip it after the whitespace trim it also needed.
                ReadOnlySpan<byte> value = TrimAsciiWhiteSpace(line["Content-Length:".Length..]);
                if (value.StartsWith("+"u8))
                    value = value[1..];
                if (Utf8Parser.TryParse(value, out int length, out int consumed) && consumed == value.Length)
                    return length;
            }
        }

        throw new FormatException("LSP frame is missing a Content-Length header.");
    }

    /// <summary>ASCII whitespace trim for header spans (no byte-span Trim() in BCL).</summary>
    private static ReadOnlySpan<byte> TrimAsciiWhiteSpace(ReadOnlySpan<byte> span)
    {
        int start = 0;
        while (start < span.Length && (span[start] == (byte)' ' || span[start] == (byte)'\t'))
            start++;
        int end = span.Length;
        while (end > start && (span[end - 1] == (byte)' ' || span[end - 1] == (byte)'\t'))
            end--;
        return span[start..end];
    }

    private async ValueTask<int> ReadByteAsync(CancellationToken ct)
    {
        int read = await _input.ReadAsync(_oneByte.AsMemory(), ct).ConfigureAwait(false);
        return read == 0 ? -1 : _oneByte[0];
    }

    private async ValueTask ReadExactlyAsync(byte[] buffer, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            int read = await _input.ReadAsync(buffer.AsMemory(total, count - total), ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("LSP stream ended mid-frame");
            total += read;
        }
    }
}
