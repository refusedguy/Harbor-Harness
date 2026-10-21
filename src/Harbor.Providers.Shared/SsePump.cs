// Shared source: compiled INTO each Harbor.Providers.* assembly via
// <Compile Include> link items (ROP-A ПР.1). The architecture matrix forbids
// Infrastructure→Infrastructure project references, so the single-source pump
// travels as a linked file instead of a shared assembly. One source of truth,
// four identical internal copies — no cross-provider coupling.

using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net.Http;
using System.Numerics;
using System.Text;
using System.Threading.Channels;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Microsoft.Extensions.Logging;

namespace Harbor.Providers.Internal;

/// <summary>
///     Shared payload-building helpers for the OpenAI-format clients
///     (ROP-A ПР.12).
/// </summary>
internal static class ProviderPayload
{
    /// <summary>
    ///     First text block of a user message, or empty. Non-text blocks
    ///     (images) are dropped — loudly: until <c>image_url</c> support lands,
    ///     silently sending an empty prompt is worse than a warning.
    /// </summary>
    public static string FirstTextOrEmpty(IReadOnlyList<LlmContentBlock> content, ILogger logger, string providerId)
    {
        for (int i = 0; i < content.Count; i++)
        {
            if (content[i] is not LlmTextBlock)
            {
                SsePumpLog.DroppingNonTextContent(logger, providerId);
                break;
            }
        }

        foreach (var block in content)
        {
            if (block is LlmTextBlock { Text: { Length: > 0 } text })
                return text;
        }
        return "";
    }
}

/// <summary>
///     Per-stream chunk-parsing state: the tool-call index→id map (ROP-A ПР.3)
///     plus the malformed-chunk counter (ROP-A ПР.4).
///     Issue #203: the index→id fallback (<c>tc{index}</c>) and usage chunks
///     that arrive without a finish reason are diagnosable through this
///     state instead of silent — remapped tool calls are counted (warned
///     once per stream at the parse site), the last finish reason is
///     remembered so a trailing usage-only chunk can complete the step
///     without flipping the stop reason, and already-delivered usage
///     duplicates are counted instead of re-emitted.
/// </summary>
internal sealed class ChunkStreamState
{
    /// <summary>First seen id wins per tool-call index.</summary>
    public Dictionary<int, string> IndexToId { get; } = new(capacity: 4);

    /// <summary>How many wire chunks were skipped as unparseable this stream.</summary>
    public int MalformedChunks { get; private set; }

    /// <summary>Record one skipped chunk.</summary>
    public void CountMalformed() => MalformedChunks++;

    /// <summary>
    ///     How many tool-call chunks fell back to the positional
    ///     <c>tc{index}</c> id (no wire id and nothing remembered).
    ///     The fallback keeps the stream coalescing, but an agent/store
    ///     <c>tool_call_id</c> mismatch is a real risk when the server
    ///     reorders deltas — hence counted, never silent (#203 B8).
    /// </summary>
    public int RemappedToolCalls { get; private set; }

    /// <summary>Record one positional id fallback.</summary>
    public void CountRemap() => RemappedToolCalls++;

    /// <summary>Whether the once-per-stream remap warning was already logged.</summary>
    public bool RemapWarned { get; private set; }

    /// <summary>Mark the once-per-stream remap warning as logged.</summary>
    public void MarkRemapWarned() => RemapWarned = true;

    /// <summary>
    ///     Finish reason of the last <c>StepFinish</c> emitted this stream
    ///     (null when none yet). A trailing usage-only chunk re-emits with
    ///     this reason so it can deliver token stats without changing the
    ///     step outcome (#203 E3).
    /// </summary>
    public string? LastFinishReason { get; private set; }

    /// <summary>Whether a <c>StepFinish</c> with non-null usage was emitted this stream.</summary>
    public bool UsageDelivered { get; private set; }

    /// <summary>Record an emitted step finish (and usage delivery, when present).</summary>
    public void MarkStepFinish(string? finishReason, bool hasUsage)
    {
        LastFinishReason = finishReason;
        if (hasUsage)
            UsageDelivered = true;
    }

    /// <summary>
    ///     Trailing usage chunks dropped because usage was already delivered
    ///     earlier this stream (same numbers, nothing new to report).
    /// </summary>
    public int DroppedUsageChunks { get; private set; }

    /// <summary>Record one dropped duplicate usage chunk.</summary>
    public void CountDroppedUsage() => DroppedUsageChunks++;
}

/// <summary>
///     Classification of one raw SSE line, as decided by
///     <see cref="SsePump.DecodeDataLine" /> (#467). The payload itself never
///     leaves as a string here — it rides out as a span over the pump's
///     pooled decode window (#1526), so the decode step cannot copy it.
/// </summary>
internal enum SseLineKind
{
    /// <summary>
    ///     Not a <c>data:</c> field — a comment (<c>: keep-alive</c>), an
    ///     <c>event:</c>/<c>id:</c>/<c>retry:</c> line, or anything blank.
    /// </summary>
    NotData,

    /// <summary>A <c>data:</c> field with no payload — the keep-alive heartbeat.</summary>
    Empty,

    /// <summary>
    ///     The <c>[DONE]</c> sentinel — graceful end of stream. Padding on
    ///     either side is tolerated.
    /// </summary>
    Done,

    /// <summary>A payload chunk; the decode's <c>payload</c> out carries it.</summary>
    Payload,
}

/// <summary>
///     #1526: the hot-line pump callback. Receives one raw line as a span
///     over the pump's pooled decode window — valid only for the duration of
///     the call, and the pump awaits the returned task before decoding the
///     next line into the same window, so the callback must not store the
///     span. Anything it needs past the call it must materialise itself.
/// </summary>
internal delegate Task<bool> SseSpanLineHandler(ReadOnlySpan<char> line, CancellationToken ct);

/// <summary>
///     The one SSE/NDJSON stream pump behind every ILlmClient (ROP-A ПР.1).
///     Owns the whole transport pipeline: send → status check → line loop →
///     completion, with canonical error classification (ROP-A ПР.5) and the
///     contract "exactly one <see cref="FinishEvent" /> after a graceful
///     end-of-stream, none on error or cancellation". Parsers downstream must
///     never emit <see cref="FinishEvent" /> themselves.
///     Also hosts the shared #203 diagnostics helper (<see cref="WarnOnceOnRemap" />)
///     so every provider assembly (each links this file) warns identically.
/// </summary>
internal static class SsePump
{
    /// <summary>
    ///     #203 B8: a positional tool-call id fallback means the server
    ///     omitted the wire id — agent/store <c>tool_call_id</c> sync then
    ///     relies on index order alone. Warn once per stream (count every
    ///     occurrence) instead of staying silent.
    /// </summary>
    internal static void WarnOnceOnRemap(ChunkStreamState state, int remapsBefore, ILogger logger)
    {
        if (state.RemappedToolCalls > remapsBefore && !state.RemapWarned)
        {
            state.MarkRemapWarned();
            SsePumpLog.ToolCallIdFallback(logger, state.RemappedToolCalls);
        }
    }

    /// <summary>
    ///     Runs the raw-line pump: send → status → line loop → single
    ///     FinishEvent on graceful end-of-stream.
    /// </summary>
    /// <param name="writer">Target channel; the caller completes it in its finally.</param>
    /// <param name="http">HttpClient used for the streaming request.</param>
    /// <param name="request">Fully-built request (auth headers included).</param>
    /// <param name="onLine">
    ///     Raw-line handler (NDJSON style). Return false for a graceful
    ///     end-of-stream (e.g. sentinel seen) — the pump then emits the single
    ///     <see cref="FinishEvent" /> and stops reading.
    /// </param>
    /// <param name="apiErrorLabel">
    ///     Provider label for non-success responses, e.g. "OpenAI API" →
    ///     "OpenAI API error 429: …".
    /// </param>
    /// <param name="logger">Client logger for transport warnings.</param>
    /// <param name="ct">Caller cancellation; cancellation emits nothing.</param>
    /// <param name="onResponse">
    ///     Observability hook fired once after a successful send (activity tags).
    /// </param>
    /// <param name="mapSendFailure">
    ///     Optional override for send-phase failures (provider-specific hint
    ///     text, e.g. Ollama's "`ollama serve` running?" message). When null,
    ///     the canonical "HTTP request failed" classification is emitted.
    /// </param>
    /// <param name="onTransportError">
    ///     Observability hook fired for mid-stream failures before the terminal
    ///     error event is written (activity status).
    /// </param>
    /// <param name="onComplete">Observability hook fired on graceful completion.</param>
    public static Task RunAsync(
        ChannelWriter<LlmEvent> writer,
        HttpClient http,
        HttpRequestMessage request,
        Func<string, CancellationToken, Task<bool>> onLine,
        string apiErrorLabel,
        ILogger logger,
        CancellationToken ct,
        Action<HttpResponseMessage>? onResponse = null,
        Func<Exception, CancellationToken, ErrorEvent>? mapSendFailure = null,
        Action<Exception>? onTransportError = null,
        Action? onComplete = null)
    {
        // #1526: the spread of one whole-line `string` per raw line from the
        // old StreamReader loop stops at the NDJSON seam. The core pump hands
        // out one span over its pooled decode window per line and the
        // NDJSON consumer materialises the line string exactly once — the
        // copy the JSON parser actually needs.
        var handler = new NdjsonLineHandler(onLine);
        return RunCoreAsync(
            writer, http, request, handler.HandleAsync, apiErrorLabel, logger, ct,
            onResponse, mapSendFailure, onTransportError, onComplete);
    }

    /// <summary>
    ///     The transport pipeline behind both entry points — send → status →
    ///     line loop → single FinishEvent on graceful end-of-stream. The
    ///     error/cancellation shape is Byte-identical to the pre-#1526
    ///     <see cref="RunAsync" /> body; only the line loop changed hands:
    ///     <c>StreamReader.ReadLineAsync</c> (one alloc'd <c>string</c> per
    ///     raw line) became <see cref="PumpLinesAsync" /> (PipeReader +
    ///     pooled decode window) and the handler is a span callback.
    /// </summary>
    private static async Task RunCoreAsync(
        ChannelWriter<LlmEvent> writer,
        HttpClient http,
        HttpRequestMessage request,
        SseSpanLineHandler onLine,
        string apiErrorLabel,
        ILogger logger,
        CancellationToken ct,
        Action<HttpResponseMessage>? onResponse = null,
        Func<Exception, CancellationToken, ErrorEvent>? mapSendFailure = null,
        Action<Exception>? onTransportError = null,
        Action? onComplete = null)
    {
        try
        {
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                onResponse?.Invoke(response);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await writer.WriteAsync(
                    mapSendFailure?.Invoke(ex, ct)
                    ?? new ErrorEvent(
                        $"HTTP request failed: {ex.Message}", ex.ToString(),
                        ProviderErrors.FromException(ex, ct)), ct).ConfigureAwait(false);
                return;
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    string errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    // #259: bound the user-facing blob — 429 JSON bodies run
                    // to KBs and every renderer paints Message inline. The
                    // full body rides on Exception for diagnostics.
                    string bounded = ProviderErrors.BuildProviderErrorMessage(
                        apiErrorLabel, (int)response.StatusCode, errorBody);
                    SsePumpLog.ProviderError(logger, apiErrorLabel, (int)response.StatusCode, bounded);
                    await writer.WriteAsync(new ErrorEvent(
                        bounded,
                        errorBody.Length > ProviderErrors.MaxProviderErrorBodyChars ? errorBody : null,
                        // #1755: the context-window overflow is not a status
                        // code — every provider answers it with its own body
                        // (OpenAI's `context_length_exceeded`, Anthropic's
                        // "prompt is too long", Ollama's "the input length
                        // exceeds the context window"). The body is therefore
                        // what classifies it, and the agent loop compacts and
                        // retries on that kind.
                        Kind: ProviderErrors.ClassifyResponse(response.StatusCode, errorBody),
                        StatusCode: (int)response.StatusCode), ct).ConfigureAwait(false);
                    return;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

                // #1526: PipeReader + SequenceReader instead of one `string`
                // per raw SSE/NDJSON line — the decode window lives in pooled
                // scratch memory, so only the meaningful part of a line (or
                // none at all for comments/heartbeats/blank lines) is ever
                // materialised.
                await PumpLinesAsync(stream, onLine, ct).ConfigureAwait(false);
            }

            // Graceful end-of-stream: the single terminal success marker.
            onComplete?.Invoke();
            await writer.WriteAsync(new FinishEvent(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected on cancel — no FinishEvent.
        }
        catch (Exception ex)
        {
            onTransportError?.Invoke(ex);
            // #203 E5: the pump task is fire-and-forget from the client's
            // perspective — the terminal ErrorEvent reaches the consumer,
            // but without a log line the failure is invisible in traces.
            SsePumpLog.StreamFailed(logger, ex, apiErrorLabel, ex.Message);
            await writer.WriteAsync(new ErrorEvent(
                $"Stream failed: {ex.Message}", ex.ToString(),
                ProviderErrors.FromException(ex, ct)), ct).ConfigureAwait(false);
        }
    }

    /// <summary>SSE field name — the only field the pump decodes.</summary>
    private const string DataField = "data:";

    /// <summary>OpenAI-style graceful end-of-stream sentinel.</summary>
    private const string DoneSentinel = "[DONE]";

    /// <summary>
    ///     Initial pooled decode-window size — a typical control line plus a
    ///     small text delta fits with room; bigger lines grow the window to
    ///     the largest size seen so far (one pooled re-rent, never per line).
    /// </summary>
    private const int InitialWindowBytes = 1024;

    /// <summary>
    ///     #1526: the shared line-extraction body behind both entry points.
    ///     <c>StreamReader.ReadLineAsync</c> allocated one whole-line
    ///     <c>string</c> per raw line — a tool-arg delta line is kilobytes and
    ///     Anthropic-style streams interleave two or three non-payload lines
    ///     (event, blank) per payload, so even "empty" progress cost a string
    ///     plus a per-line <c>Task</c> each time. Instead: a PipeReader over
    ///     the response stream, lines split with a SequenceReader, and each
    ///     line decoded UTF-8-wise into a **pooled char window** sized
    ///     <c>byteLen + 1</c> (UTF-8 decodes to at most one char per byte, so
    ///     that bound never overflows). The window grows to the largest line
    ///     the stream has actually carried and is returned to the pool — the
    ///     loop itself allocates nothing per line; only the callback that
    ///     needs a string pays for it (<see cref="SseLineHandler" /> pays for
    ///     one <c>data:</c> payload per chunk, exactly the old contract).
    ///     <list type="bullet">
    ///         <item>
    ///         Terminators are LF and CR (SSE's spec allows either); CR-LF
    ///         degenerates to one empty line, which the blank-line skip drops.
    ///         <c>ReadLineAsync</c>'s per-line <c>Task&lt;string?&gt;</c>
    ///         churn disappears with it.
    ///         </item>
    ///         <item>
    ///         Blank/whitespace-only lines never reach the handler — the
    ///         <c>IsNullOrWhiteSpace</c> skip of the old loop, made
    ///         allocation-free — so a padding-only SSE stream now costs
    ///         literally zero.
    ///         </item>
    ///         <item>
    ///         A trailing unterminated final line still reaches the handler
    ///         (NDJSON servers end the last chunk at EOF as frequently as
    ///         SSE servers send a terminator).
    ///         </item>
    ///     </list>
    /// </summary>
    /// <param name="source">
    ///     The HTTP response body stream. Ownership stays with the caller:
    ///     the pipe reader is created with <c>leaveOpen</c>.
    /// </param>
    /// <param name="onLine">
    ///     One raw line per call as a span over the pooled decode window (see
    ///     <see cref="SseSpanLineHandler" /> for validity). Return false to
    ///     stop reading — a graceful end-of-stream the caller completes.
    /// </param>
    /// <param name="ct">Caller cancellation; <c>ReadAsync</c> honors it.</param>
    internal static async Task PumpLinesAsync(Stream source, SseSpanLineHandler onLine, CancellationToken ct)
    {
        PipeReader reader = PipeReader.Create(source, new StreamPipeReaderOptions(leaveOpen: true));
        try
        {
            byte[] byteWindow = ArrayPool<byte>.Shared.Rent(InitialWindowBytes);
            char[] charWindow = ArrayPool<char>.Shared.Rent(InitialWindowBytes + 1);
            try
            {
                // StreamReader parity: at most one UTF-8 preamble is stripped,
                // and only at the very start of the stream.
                bool trimBom = true;
                while (true)
                {
                    ReadResult result = await reader.ReadAsync(ct).ConfigureAwait(false);
                    ReadOnlySequence<byte> buffer = result.Buffer;
                    SequencePosition consumed = buffer.Start;
                    bool stopReading = false;

                    while (TryReadLine(ref buffer, out ReadOnlySequence<byte> lineBytes))
                    {
                        consumed = buffer.Start;
                        if (lineBytes.Length == 0)
                        {
                            continue; // Blank SSE delimiter (CR-LF tail, "\n\n") — the IsNullOrWhiteSpace skip.
                        }

                        ReadOnlySpan<char> line = DecodeLine(lineBytes, ref byteWindow, ref charWindow, ref trimBom);
                        if (line.Length == 0 || line.IsWhiteSpace())
                        {
                            continue;
                        }

                        // Awaiting before the next decode keeps the span's
                        // single-writer contract: the window is reused only
                        // after the callback has answered.
                        if (!await onLine(line, ct).ConfigureAwait(false))
                        {
                            stopReading = true; // Sentinel — graceful stop; the caller emits the single FinishEvent.
                            break;
                        }
                    }

                    // Consume everything the inner loop read; examine to the
                    // buffer END so a line split across refills makes the next
                    // ReadAsync WAIT for the rest instead of re-returning the
                    // same unexamined tail in a spin (the consumed==examined
                    // form would treat the partial line as fresh data).
                    reader.AdvanceTo(consumed, buffer.End);
                    if (result.IsCompleted)
                    {
                        // EOF: a trailing unterminated final line must still
                        // reach the handler (ReadLineAsync returned it).
                        if (!stopReading && buffer.Length > 0)
                        {
                            ReadOnlySpan<char> line = DecodeLine(buffer, ref byteWindow, ref charWindow, ref trimBom);
                            if (line.Length > 0 && !line.IsWhiteSpace())
                            {
                                _ = await onLine(line, ct).ConfigureAwait(false);
                            }
                        }

                        break;
                    }

                    if (stopReading)
                    {
                        break;
                    }
                }
            }
            finally
            {
                ArrayPool<char>.Shared.Return(charWindow);
                ArrayPool<byte>.Shared.Return(byteWindow);
            }
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>SSE/NDJSON line terminators — LF or CR; CR-LF is CR then an empty LF line.</summary>
    private static ReadOnlySpan<byte> LineTerminators => [(byte)'\n', (byte)'\r'];

    /// <summary>
    ///     One terminator-terminated line out of <paramref name="buffer" /> —
    ///     the delimiter is consumed and NOT part of the line. On success,
    ///     <paramref name="buffer" /> is resliced to the next line's start;
    ///     on failure (no terminator left), it is left untouched for the
    ///     next refill — the pump's unconsumed-tail bookkeeping rides on it.
    /// </summary>
    private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
    {
        if (buffer.IsEmpty)
        {
            line = default;
            return false;
        }

        SequenceReader<byte> scanner = new(buffer);
        if (!scanner.TryReadToAny(out line, LineTerminators, advancePastDelimiter: true))
        {
            line = default;
            return false;
        }

        buffer = buffer.Slice(scanner.Position);
        return true;
    }

    /// <summary>
    ///     Decode one line's bytes into the pooled char window and return the
    ///     decoded span. Grows both windows in place when the line outgrew
    ///     them (return → rent — pooled, so steady-state cost is zero); a
    ///     single-segment line skips the byte window entirely and decodes
    ///     straight out of the pipe's segment. Strips a UTF-8 preamble the
    ///     way <c>StreamReader</c> did, so the first <c>data:</c> line of a
    ///     BOM-prefixed stream still classifies as <see cref="SseLineKind.Payload" />.
    /// </summary>
    private static ReadOnlySpan<char> DecodeLine(
        ReadOnlySequence<byte> lineBytes,
        ref byte[] byteWindow,
        ref char[] charWindow,
        ref bool trimBom)
    {
        int byteLen = checked((int)lineBytes.Length);

        // #1526: the decode window grows to the largest line the stream has
        // actually carried. One pooled re-rent on the growth path; the hot
        // path (window suffices) allocates nothing.
        if (byteLen > byteWindow.Length)
        {
            ArrayPool<byte>.Shared.Return(byteWindow);
            byteWindow = ArrayPool<byte>.Shared.Rent((int)BitOperations.RoundUpToPowerOf2((uint)byteLen));
        }

        if (byteLen + 1 > charWindow.Length)
        {
            ArrayPool<char>.Shared.Return(charWindow);
            charWindow = ArrayPool<char>.Shared.Rent((int)BitOperations.RoundUpToPowerOf2((uint)(byteLen + 1)));
        }

        ReadOnlySpan<byte> line;
        if (lineBytes.IsSingleSegment)
        {
            // The overwhelmingly common case: the line arrived contiguous
            // inside the pipe's current segment — decode with no byte copy.
            line = lineBytes.FirstSpan;
        }
        else
        {
            // The line split across pipe refills: stitch it into the byte
            // window once, then decode from there.
            Span<byte> stitched = byteWindow.AsSpan(0, byteLen);
            lineBytes.CopyTo(stitched);
            line = stitched;
        }

        int charCount = Encoding.UTF8.GetChars(line, charWindow);
        if (trimBom && charCount > 0)
        {
            trimBom = false; // Only the stream's very first line could carry the preamble.
            if (charWindow[0] == '\uFEFF')
            {
                return charCount == 1
                    ? default // BOM-only first line — blank.
                    : charWindow.AsSpan(1, charCount - 1);
            }
        }

        return charWindow.AsSpan(0, charCount);
    }

    /// <summary>
    ///     SSE flavour of <see cref="RunAsync" />: filters <c>data:</c> lines
    ///     and treats the <c>[DONE]</c> sentinel as graceful end-of-stream.
    /// </summary>
    /// <param name="writer">Target channel; the caller completes it in its finally.</param>
    /// <param name="http">HttpClient used for the streaming request.</param>
    /// <param name="request">Fully-built request (auth headers included).</param>
    /// <param name="onData">Handler for one <c>data:</c> payload.</param>
    /// <param name="apiErrorLabel">
    ///     Provider label for non-success responses, e.g. "OpenAI API" →
    ///     "OpenAI API error 429: …".
    /// </param>
    /// <param name="logger">Client logger for transport warnings.</param>
    /// <param name="ct">Caller cancellation; cancellation emits nothing.</param>
    /// <param name="onResponse">
    ///     Observability hook fired once after a successful send (activity tags).
    /// </param>
    /// <param name="mapSendFailure">
    ///     Optional override for send-phase failures. When null, the canonical
    ///     "HTTP request failed" classification is emitted.
    /// </param>
    /// <param name="onTransportError">
    ///     Observability hook fired for mid-stream failures before the terminal
    ///     error event is written.
    /// </param>
    /// <param name="onComplete">Observability hook fired on graceful completion.</param>
    public static Task RunSseAsync(
        ChannelWriter<LlmEvent> writer,
        HttpClient http,
        HttpRequestMessage request,
        Func<string, CancellationToken, Task> onData,
        string apiErrorLabel,
        ILogger logger,
        CancellationToken ct,
        Action<HttpResponseMessage>? onResponse = null,
        Func<Exception, CancellationToken, ErrorEvent>? mapSendFailure = null,
        Action<Exception>? onTransportError = null,
        Action? onComplete = null)
    {
        // #467: one handler + one delegate per stream. This used to be an
        // `async (line, token) => …` lambda, whose per-call state machine
        // (plus a display class + delegate) was allocated per stream while
        // each line that carried no payload still paid to enter it.
        var handler = new SseLineHandler(onData);
        return RunCoreAsync(
            writer, http, request, handler.HandleAsync, apiErrorLabel, logger, ct,
            onResponse, mapSendFailure, onTransportError, onComplete);
    }

    /// <summary>
    ///     #467 + #1526: the per-line handler behind <see cref="RunSseAsync" />.
    ///     <see cref="HandleAsync" /> is deliberately **not** <c>async</c>: it
    ///     classifies the line on spans (see
    ///     <see cref="DecodeDataLine" />) and only the payload case descends
    ///     into a state machine — and then pays for exactly one payload
    ///     string, the one the chunk parser needs. Comments, heartbeats and
    ///     the <c>[DONE]</c> sentinel answer from cached completed tasks —
    ///     and under the #1526 window-pump this is now *also* the literal
    ///     allocation profile, since the zero-copy shape of a non-payload
    ///     line no longer even implies the pump allocated the line itself.
    /// </summary>
    private sealed class SseLineHandler(Func<string, CancellationToken, Task> onData)
    {
        private static readonly Task<bool> KeepStreaming = Task.FromResult(true);
        private static readonly Task<bool> EndOfStream = Task.FromResult(false);

        /// <summary>
        ///     Handle one raw line. False means the sentinel was seen: stop
        ///     reading, the pump emits its single FinishEvent.
        /// </summary>
        public Task<bool> HandleAsync(ReadOnlySpan<char> line, CancellationToken ct)
        {
            SseLineKind kind = DecodeDataLine(line, out ReadOnlySpan<char> payload);
            return kind switch
            {
                SseLineKind.NotData or SseLineKind.Empty => KeepStreaming,
                SseLineKind.Done => EndOfStream,
                // The one and only payload copy per delta.
                _ => DispatchAsync(new string(payload), ct),
            };
        }

        private async Task<bool> DispatchAsync(string data, CancellationToken ct)
        {
            await onData(data, ct).ConfigureAwait(false);
            return true;
        }
    }

    /// <summary>
    ///     #1526: the line handler behind <see cref="RunAsync" /> (NDJSON
    ///     mode). The whole line IS the payload here, so the old pump's
    ///     per-line <c>string</c> survives only as the materialisation the
    ///     chunk parser needs — one copy per line, at the JSON seam and
    ///     nowhere else.
    /// </summary>
    private sealed class NdjsonLineHandler(Func<string, CancellationToken, Task<bool>> onLine)
    {
        public Task<bool> HandleAsync(ReadOnlySpan<char> line, CancellationToken ct) =>
            onLine(new string(line), ct);
    }

    /// <summary>
    ///     #467: classify one raw SSE line and slice its payload **in place**.
    ///     The previous chain — <c>line["data:".Length..].TrimStart()</c>
    ///     followed by <c>data.Trim().Equals("[DONE]", Ordinal)</c> — copied
    ///     the whole payload (twice for the usual <c>data: {json}</c> line, and
    ///     a third time whenever the server pads the sentinel) purely to
    ///     compare it against a six-character literal. Tool-call argument
    ///     deltas are kilobytes, so that copy dominated the per-delta budget
    ///     — and it sat *above* <c>OpenAiWire.TryParseChatChunkLine</c>, i.e.
    ///     outside the reach of the #186 chunk-parsing tripwire. Trimming and
    ///     the sentinel test run on spans here, so the decode allocates
    ///     nothing; the caller materialises the payload string exactly once,
    ///     for the chunk parser.
    /// </summary>
    /// <param name="line">
    ///     One raw line as decoded by the pump (over its pooled decode
    ///     window, #1526).
    /// </param>
    /// <param name="payload">
    ///     For <see cref="SseLineKind.Payload" />: the payload with leading
    ///     whitespace trimmed — byte-for-byte what the old
    ///     <c>line["data:".Length..].TrimStart()</c> handed downstream
    ///     (trailing whitespace is kept, as before). Empty otherwise.
    /// </param>
    internal static SseLineKind DecodeDataLine(ReadOnlySpan<char> line, out ReadOnlySpan<char> payload)
    {
        payload = default;
        if (!line.StartsWith(DataField, StringComparison.OrdinalIgnoreCase))
        {
            return SseLineKind.NotData;
        }

        // SSE allows `data:{...}` with no space; only the `data:` prefix
        // itself is significant. TrimStart keeps payload JSON intact
        // (JsonDocument tolerates leading whitespace anyway).
        ReadOnlySpan<char> data = line[DataField.Length..].TrimStart();
        if (data.IsEmpty)
        {
            return SseLineKind.Empty; // Heartbeat `data:` — no payload.
        }

        if (data.Trim().SequenceEqual(DoneSentinel))
        {
            return SseLineKind.Done;
        }

        payload = data;
        return SseLineKind.Payload;
    }
}

/// <summary>
///     SG7: BCL <c>[LoggerMessage]</c> delegates for the shared SSE pump (<see cref="SsePump" />)
///     and payload helpers (<see cref="ProviderPayload" />).
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class SsePumpLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Dropping non-text content block(s): {Provider} does not support vision yet")]
    public static partial void DroppingNonTextContent(ILogger logger, string provider);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Tool-call chunk(s) arrived without a wire id; fell back to positional ids (remaps={Remaps}). Out-of-order deltas may desync agent/store tool_call_id.")]
    public static partial void ToolCallIdFallback(ILogger logger, int remaps);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "{Label} error {Status}: {Snippet}")]
    public static partial void ProviderError(ILogger logger, string label, int status, string snippet);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "{Label} stream failed: {Message}")]
    public static partial void StreamFailed(ILogger logger, Exception ex, string label, string message);
}
