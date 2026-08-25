// Shared source: compiled INTO each Harbor.Providers.* assembly via
// <Compile Include> link items (ROP-A ПР.1). The architecture matrix forbids
// Infrastructure→Infrastructure project references, so the single-source pump
// travels as a linked file instead of a shared assembly. One source of truth,
// four identical internal copies — no cross-provider coupling.

using System.Net.Http;
using System.Threading.Channels;
using Harbor.Abstractions.Events;
using Microsoft.Extensions.Logging;

namespace Harbor.Providers.Internal;

/// <summary>
///     Per-stream chunk-parsing state: the tool-call index→id map (ROP-A ПР.3)
///     plus the malformed-chunk counter (ROP-A ПР.4).
/// </summary>
internal sealed class ChunkStreamState
{
    /// <summary>First seen id wins per tool-call index.</summary>
    public Dictionary<int, string> IndexToId { get; } = new(capacity: 4);

    /// <summary>How many wire chunks were skipped as unparseable this stream.</summary>
    public int MalformedChunks { get; private set; }

    /// <summary>Record one skipped chunk.</summary>
    public void CountMalformed() => MalformedChunks++;
}

/// <summary>
///     The one SSE/NDJSON stream pump behind every ILlmClient (ROP-A ПР.1).
///     Owns the whole transport pipeline: send → status check → line loop →
///     completion, with canonical error classification (ROP-A ПР.5) and the
///     contract "exactly one <see cref="FinishEvent" /> after a graceful
///     end-of-stream, none on error or cancellation". Parsers downstream must
///     never emit <see cref="FinishEvent" /> themselves.
/// </summary>
internal static class SsePump
{
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
                    await writer.WriteAsync(new ErrorEvent(
                        $"{apiErrorLabel} error {(int)response.StatusCode}: {errorBody}",
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
            if (!line.StartsWith("data: ", StringComparison.OrdinalIgnoreCase)) return true;

            string data = line["data: ".Length..];
            if (data == "[DONE]") return false;

            await onData(data, token).ConfigureAwait(false);
            return true;
        }, apiErrorLabel, logger, ct, onResponse, mapSendFailure, onTransportError, onComplete);
}
