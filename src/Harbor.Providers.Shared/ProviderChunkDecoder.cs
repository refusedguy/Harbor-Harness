// Shared source: compiled INTO each Harbor.Providers.* assembly via
// <Compile Include> link items, exactly like SsePump.cs (ROP-A ПР.1). The
// architecture matrix forbids Infrastructure→Infrastructure project references,
// so the one decoder travels as linked source instead of a shared assembly.
// #3748: the transcode/parse/write/count loop every streaming provider repeats
// lives here once. A provider passes its span parser and its log delegate; the
// decoder owns the pooled buffer, the once-per-stream remap warning and the
// skip-and-count malformed-chunk policy (ПР.4). No reflection — the parse
// callback is a delegate rather than Func<> because ReadOnlySpan<byte> is a
// ref struct and cannot be a generic type argument.

using System.Buffers;
using System.Text;
using System.Threading.Channels;
using Harbor.Abstractions.Events;
using Microsoft.Extensions.Logging;

namespace Harbor.Providers.Internal;

/// <summary>
///     Parse one UTF-8 chunk into the events it carries. The span is valid only
///     for the duration of the call — it points into the decoder's pooled
///     buffer, which is returned before <see cref="ProviderChunkDecoder.DecodeAsync" />
///     writes anything to the channel. A parser that needs a value past the call
///     must materialise it itself.
/// </summary>
internal delegate List<LlmEvent> ParseUtf8Chunk(ReadOnlySpan<byte> utf8Json, ChunkStreamState? state);

/// <summary>
///     Log one wire chunk the parser could not read. The decoder supplies its
///     own logger and the running malformed count; the provider supplies the
///     message template through its source-generated log method.
/// </summary>
internal delegate void LogMalformed(ILogger logger, Exception ex, int count, string data);

/// <summary>
///     The minimal wire-chunk decoder behind the streaming providers (#3748).
///     Owns the four steps that used to be hand-written in every client:
///     transcode the SSE/NDJSON payload into pooled UTF-8, call the provider's
///     span parser, warn once on a positional tool-call id fallback, and write
///     the parsed events to the channel. A parse failure is counted on the
///     stream state and logged through the provider's delegate, never as a
///     terminal error — the stream survives wire noise (ПР.4).
/// </summary>
internal static class ProviderChunkDecoder
{
    /// <summary>
    ///     Decode one wire payload and write its events, in parse order, to
    ///     <paramref name="writer" />. The caller completes the writer; this
    ///     method never emits a terminal event.
    /// </summary>
    /// <param name="line">The raw SSE <c>data:</c> payload or NDJSON line.</param>
    /// <param name="writer">Target channel; awaited per event for backpressure.</param>
    /// <param name="state">Per-stream diagnostics (malformed count, id remaps).</param>
    /// <param name="logger">Client logger for transport warnings.</param>
    /// <param name="ct">Caller cancellation; cancellation propagates from the writes.</param>
    /// <param name="parse">Provider-specific span parser.</param>
    /// <param name="logMalformed">Provider-specific source-generated log method.</param>
    public static async Task DecodeAsync(
        string line,
        ChannelWriter<LlmEvent> writer,
        ChunkStreamState state,
        ILogger logger,
        CancellationToken ct,
        ParseUtf8Chunk parse,
        LogMalformed logMalformed)
    {
        List<LlmEvent> events;
        try
        {
            int remapsBefore = state.RemappedToolCalls;
            // #171: single-pass transcode — GetMaxByteCount rent + one
            // GetBytes (was GetByteCount + GetBytes: two passes per event).
            byte[] rented = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(line.Length));
            int byteCount;
            try
            {
                byteCount = Encoding.UTF8.GetBytes(line, rented);
                events = parse(rented.AsSpan(0, byteCount), state);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            // #203 B8: positional id fallback, counted in the parser —
            // warn once per stream instead of staying silent.
            SsePump.WarnOnceOnRemap(state, remapsBefore, logger);
        }
        catch (Exception ex)
        {
            state.CountMalformed();
            logMalformed(logger, ex, state.MalformedChunks, line);
            return;
        }

        foreach (LlmEvent evt in events)
        {
            await writer.WriteAsync(evt, ct).ConfigureAwait(false);
        }
    }
}
