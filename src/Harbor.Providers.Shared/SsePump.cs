// Shared source: compiled INTO each Harbor.Providers.* assembly via
// <Compile Include> link items (ROP-A ПР.1). The architecture matrix forbids
// Infrastructure→Infrastructure project references, so the single-source pump
// travels as a linked file instead of a shared assembly. One source of truth,
// four identical internal copies — no cross-provider coupling.

using System.Collections.Generic;
using System.Net.Http;
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
                logger.LogWarning(
                    "Dropping non-text content block(s): {Provider} does not support vision yet",
                    providerId);
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
            logger.LogWarning(
                "Tool-call chunk(s) arrived without a wire id; fell back to positional ids " +
                "(remaps={Remaps}). Out-of-order deltas may desync agent/store tool_call_id.",
                state.RemappedToolCalls);
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
    public static async Task RunAsync(
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
                    logger.LogWarning(
                        "{Label} error {Status}: {Snippet}", apiErrorLabel, (int)response.StatusCode, bounded);
                    await writer.WriteAsync(new ErrorEvent(
                        bounded,
                        errorBody.Length > ProviderErrors.MaxProviderErrorBodyChars ? errorBody : null,
                        Kind: ProviderErrors.FromStatus(response.StatusCode),
                        StatusCode: (int)response.StatusCode), ct).ConfigureAwait(false);
                    return;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(stream);

                string? line;
                while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    // Graceful stop (sentinel seen) breaks out to the shared
                    // single-FinishEvent tail below.
                    if (!await onLine(line, ct).ConfigureAwait(false)) break;
                }
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
            logger.LogWarning(ex, "{Label} stream failed: {Message}", apiErrorLabel, ex.Message);
            await writer.WriteAsync(new ErrorEvent(
                $"Stream failed: {ex.Message}", ex.ToString(),
                ProviderErrors.FromException(ex, ct)), ct).ConfigureAwait(false);
        }
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
        Action? onComplete = null) =>
        RunAsync(writer, http, request, async (line, token) =>
        {
            // SSE allows `data:{...}` with no space; only the `data:` prefix
            // itself is significant. TrimStart keeps payload JSON intact
            // (JsonDocument tolerates leading whitespace anyway).
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return true;

            string data = line["data:".Length..].TrimStart();
            if (data.Length == 0) return true; // Heartbeat `data:` — no payload.
            if (data.Trim().Equals("[DONE]", StringComparison.Ordinal)) return false;

            await onData(data, token).ConfigureAwait(false);
            return true;
        }, apiErrorLabel, logger, ct, onResponse, mapSendFailure, onTransportError, onComplete);
}
