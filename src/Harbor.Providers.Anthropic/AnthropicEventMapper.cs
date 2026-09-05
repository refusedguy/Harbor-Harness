using System.Text.Json;
using System.Threading.Channels;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace Harbor.Providers.Anthropic;

/// <summary>
///     Maps Anthropic Messages-API SSE events to <see cref="LlmEvent" />.
///     Extracted from <see cref="AnthropicLlmClient" /> (§ROP god-object split).
/// </summary>
internal static class AnthropicEventMapper
{
    /// <summary>
    ///     Parse one SSE data line and write any emitted events directly into the
    ///     channel. Streaming inside the JsonDocument's `using` scope eliminates the
    ///     per-chunk .ToList() materialization that the previous MapAnthropicEvent
    ///     helper required to keep JsonElement values alive after dispose.
    /// </summary>
    public static async Task WriteAnthropicEventsAsync(
        string data,
        ChannelWriter<LlmEvent> writer,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            foreach (var evt in MapAnthropicEventFromDocument(doc.RootElement))
            {
                await writer.WriteAsync(evt, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse Anthropic event: {Data}", data);
        }
    }

    public static IEnumerable<LlmEvent> MapAnthropicEventFromDocument(JsonElement root)
    {
        string? type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

        switch (type)
        {
            case "message_start":
                yield return new StepStartEvent(0);
                break;

            case "content_block_start":
                if (root.TryGetProperty("content_block", out var block) &&
                    block.TryGetProperty("type", out var blockType))
                {
                    string id = block.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "0" : "0";
                    string? blockTypeStr = blockType.GetString();

                    if (blockTypeStr == "text")
                        yield return new TextStartEvent(id);
                    else if (blockTypeStr == "thinking")
                        yield return new ThinkingStartEvent(id);
                    else if (blockTypeStr == "tool_use")
                    {
                        string name = block.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                        yield return new ToolCallStartEvent(id, name);
                    }
                }
                break;

            case "content_block_delta":
                if (root.TryGetProperty("delta", out var delta))
                {
                    string? deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() : null;
                    string id = "0"; // Anthropic doesn't include id in deltas; track via index

                    switch (deltaType)
                    {
                        case "text_delta":
                            if (delta.TryGetProperty("text", out var textEl))
                            {
                                string? text = textEl.GetString();
                                if (!string.IsNullOrEmpty(text))
                                    yield return new TextDeltaEvent(id, text);
                            }
                            break;

                        case "thinking_delta":
                            if (delta.TryGetProperty("thinking", out var thEl))
                            {
                                string? text = thEl.GetString();
                                if (!string.IsNullOrEmpty(text))
                                    yield return new ThinkingDeltaEvent(id, text);
                            }
                            break;

                        case "input_json_delta":
                            if (delta.TryGetProperty("partial_json", out var pjEl))
                            {
                                string? text = pjEl.GetString();
                                if (!string.IsNullOrEmpty(text))
                                    yield return new ToolCallDeltaEvent(id, text);
                            }
                            break;
                    }
                }
                break;

            case "message_delta":
                if (root.TryGetProperty("delta", out var msgDelta) &&
                    msgDelta.TryGetProperty("stop_reason", out var stopEl))
                {
                    string? stopReason = stopEl.GetString();
                    var usage = root.TryGetProperty("usage", out var usageEl)
                        ? new Usage(
                            usageEl.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0,
                            usageEl.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0,
                            null,
                            usageEl.TryGetProperty("cache_read_input_tokens", out var cr) ? cr.GetInt32() : null,
                            usageEl.TryGetProperty("cache_creation_input_tokens", out var cw) ? cw.GetInt32() : null)
                        : null;

                    yield return new StepFinishEvent(0, stopReason ?? "stop", usage);
                }
                break;

            case "message_stop":
                // No FinishEvent here (ROP-A ПР.1): the shared pump emits exactly one.
                break;
        }
    }
}
