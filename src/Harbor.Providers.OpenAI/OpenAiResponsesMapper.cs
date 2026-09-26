using System.Text.Json;
using System.Threading.Channels;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace Harbor.Providers.OpenAI;

/// <summary>
///     Maps OpenAI Responses-API SSE chunks to <see cref="LlmEvent" />.
///     (Chat Completions chunks are handled by the shared
///     <c>Harbor.Providers.Internal.OpenAiWire</c> helpers.)
///     Extracted from <see cref="OpenAILlmClient" /> (§ROP god-object split).
/// </summary>
internal static class OpenAiResponsesMapper
{
    /// <summary>
    ///     Parse one SSE data line and write any emitted events directly into the channel.
    ///     Streaming inside the JsonDocument's `using` scope eliminates the per-chunk .ToList()
    ///     materialization that the previous MapResponsesChunk helper required to keep JsonElement
    ///     values alive after dispose.
    /// </summary>
    public static async Task WriteResponsesEventsAsync(
        string data,
        ChannelWriter<LlmEvent> writer,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            foreach (var evt in MapResponsesChunkFromDocument(doc.RootElement))
            {
                await writer.WriteAsync(evt, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse OpenAI Responses chunk: {Data}", data);
        }
    }

    public static IEnumerable<LlmEvent> MapResponsesChunkFromDocument(JsonElement root)
    {
        string? type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

        switch (type)
        {
            case "response.created":
                yield return new StepStartEvent(0);
                break;

            case "response.output_text.delta":
                if (root.TryGetProperty("delta", out var textDelta))
                {
                    string? text = textDelta.GetString();
                    if (!string.IsNullOrEmpty(text))
                        yield return new TextDeltaEvent("0", text);
                }
                break;

            case "response.reasoning.delta":
                if (root.TryGetProperty("delta", out var reasoningDelta))
                {
                    string? text = reasoningDelta.GetString();
                    if (!string.IsNullOrEmpty(text))
                        yield return new ThinkingDeltaEvent("0", text);
                }
                break;

            case "response.function_call_arguments.delta":
                string callId = root.TryGetProperty("output_index", out var oi) ? oi.GetInt32().ToString() : "0";
                if (root.TryGetProperty("delta", out var argsDelta))
                {
                    string? args = argsDelta.GetString();
                    if (!string.IsNullOrEmpty(args))
                        yield return new ToolCallDeltaEvent(callId, args);
                }
                break;

            case "response.output_item.added":
                if (root.TryGetProperty("item", out var item) &&
                    item.TryGetProperty("type", out var itemType) &&
                    itemType.GetString() == "function_call")
                {
                    string id = item.TryGetProperty("call_id", out var idEl) ? idEl.GetString() ?? "0" : "0";
                    string name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(name))
                        yield return new ToolCallStartEvent(id, name);
                }
                break;

            case "response.completed":
                var usageEl = root.TryGetProperty("response", out var resp) && resp.TryGetProperty("usage", out var u)
                    ? u
                    : default;
                var usage = usageEl.ValueKind == JsonValueKind.Object
                    ? new Usage(
                        usageEl.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0,
                        usageEl.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0,
                        usageEl.TryGetProperty("output_tokens_details", out var od) && od.TryGetProperty("reasoning_tokens", out var rt) ? rt.GetInt32() : null)
                    : null;
                // No FinishEvent here (ROP-A ПР.1): the shared pump emits exactly one.
                yield return new StepFinishEvent(0, "stop", usage);
                break;
        }
    }
}
