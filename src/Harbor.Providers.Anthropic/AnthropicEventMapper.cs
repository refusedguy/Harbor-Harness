using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace Harbor.Providers.Anthropic;

/// <summary>
///     Maps Anthropic Messages-API SSE events to <see cref="LlmEvent" />.
///     Extracted from <see cref="AnthropicLlmClient" /> (§ROP god-object split).
///     #171: span-based core — Utf8JsonReader over pooled UTF-8, no
///     JsonDocument per event. Dispatch compares via ValueTextEquals; only
///     payload strings allocate.
/// </summary>
internal static class AnthropicEventMapper
{
    /// <summary>
    ///     Parse one SSE data line and write any emitted events directly into the
    ///     channel. The payload transcodes into a pooled buffer; a malformed
    ///     line is logged and skipped (no malformed counter on this path —
    ///     same as the former DOM walk).
    /// </summary>
    public static async Task WriteAnthropicEventsAsync(
        string data,
        ChannelWriter<LlmEvent> writer,
        ILogger logger,
        CancellationToken ct)
    {
        List<LlmEvent> events;
        try
        {
            int byteCount = Encoding.UTF8.GetByteCount(data);
            byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                Encoding.UTF8.GetBytes(data, rented);
                events = MapAnthropicEvents(rented.AsSpan(0, byteCount));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse Anthropic event: {Data}", data);
            return;
        }

        foreach (var evt in events)
        {
            await writer.WriteAsync(evt, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Map one Anthropic SSE event from UTF-8 JSON. Fields buffer across
    ///     the single pass and emit at the end, so property order on the wire
    ///     never matters (DOM parity). Unknown event types and shapes yield
    ///     no events.
    /// </summary>
    internal static List<LlmEvent> MapAnthropicEvents(ReadOnlySpan<byte> utf8Json)
    {
        var events = new List<LlmEvent>(capacity: 2);
        var reader = new Utf8JsonReader(utf8Json, isFinalBlock: true, state: default);

        int depth = 0;
        bool sawRoot = false;
        AnthropicEventKind kind = AnthropicEventKind.Unknown;
        bool inDelta = false;
        bool inContentBlock = false;
        bool inUsage = false;

        string blockId = "0";
        string? blockType = null;
        string? blockName = null;
        string? deltaType = null;
        string? deltaText = null;
        string? stopReason = null;
        bool sawStopReason = false;
        int inputTokens = 0;
        int outputTokens = 0;
        int? cacheReadTokens = null;
        int? cacheWriteTokens = null;
        bool sawUsageObject = false;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    if (depth == 0)
                    {
                        // Single top-level object only (DOM parity: a root
                        // array/scalar carries no event) — skip anything else.
                        if (!sawRoot)
                        {
                            depth = 1;
                            sawRoot = true;
                        }
                        else
                        {
                            reader.Skip();
                        }

                        break;
                    }

                    if (depth == 1)
                    {
                        depth = 2;
                        break;
                    }

                    reader.Skip();
                    break;

                case JsonTokenType.StartArray:
                    if (depth <= 1)
                    {
                        depth++;
                        break;
                    }

                    reader.Skip();
                    break;

                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    if (depth == 2)
                    {
                        inDelta = false;
                        inContentBlock = false;
                        if (inUsage)
                        {
                            inUsage = false;
                            sawUsageObject = true;
                        }
                    }

                    if (depth > 0)
                        depth--;
                    break;

                case JsonTokenType.PropertyName:
                    if (!reader.Read())
                        throw new JsonException("Truncated line: property without value.");
                    HandleValue(ref reader, depth, inDelta, inContentBlock, inUsage,
                        ref kind, ref inDelta, ref inContentBlock, ref inUsage, ref depth,
                        ref blockId, ref blockType, ref blockName,
                        ref deltaType, ref deltaText,
                        ref stopReason, ref sawStopReason,
                        ref inputTokens, ref outputTokens,
                        ref cacheReadTokens, ref cacheWriteTokens);
                    break;
            }
        }

        Emit(events, kind, blockId, blockType, blockName, deltaType, deltaText,
            stopReason, sawStopReason, inputTokens, outputTokens,
            cacheReadTokens, cacheWriteTokens, sawUsageObject);
        return events;
    }

    private enum AnthropicEventKind : byte
    {
        Unknown,
        MessageStart,
        ContentBlockStart,
        ContentBlockDelta,
        MessageDelta,
        MessageStop,
    }

    private static void HandleValue(
        ref Utf8JsonReader reader, int depth, bool inDelta, bool inContentBlock, bool inUsage,
        ref AnthropicEventKind rKind,
        ref bool rInDelta, ref bool rInContentBlock, ref bool rInUsage, ref int rDepth,
        ref string rBlockId, ref string? rBlockType, ref string? rBlockName,
        ref string? rDeltaType, ref string? rDeltaText,
        ref string? rStopReason, ref bool rSawStopReason,
        ref int rInputTokens, ref int rOutputTokens,
        ref int? rCacheReadTokens, ref int? rCacheWriteTokens)
    {
        if (depth == 1)
        {
            if (reader.ValueTextEquals("type"u8))
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    if (reader.ValueTextEquals("message_start"u8))
                        rKind = AnthropicEventKind.MessageStart;
                    else if (reader.ValueTextEquals("content_block_start"u8))
                        rKind = AnthropicEventKind.ContentBlockStart;
                    else if (reader.ValueTextEquals("content_block_delta"u8))
                        rKind = AnthropicEventKind.ContentBlockDelta;
                    else if (reader.ValueTextEquals("message_delta"u8))
                        rKind = AnthropicEventKind.MessageDelta;
                    else if (reader.ValueTextEquals("message_stop"u8))
                        rKind = AnthropicEventKind.MessageStop;
                }

                return;
            }

            if (reader.ValueTextEquals("delta"u8))
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    rInDelta = true;
                    rDepth++;
                }
                else
                {
                    SkipContainer(ref reader);
                }

                return;
            }

            if (reader.ValueTextEquals("content_block"u8))
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    rInContentBlock = true;
                    rDepth++;
                }
                else
                {
                    SkipContainer(ref reader);
                }

                return;
            }

            if (reader.ValueTextEquals("usage"u8))
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    rInUsage = true;
                    rDepth++;
                }
                else
                {
                    SkipContainer(ref reader);
                }

                return;
            }

            SkipContainer(ref reader);
            return;
        }

        if (depth == 2 && inDelta)
        {
            if (reader.ValueTextEquals("type"u8))
            {
                rDeltaType = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else if (reader.ValueTextEquals("partial_json"u8))
            {
                rDeltaText = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else if (reader.ValueTextEquals("stop_reason"u8))
            {
                // message_delta carries its stop reason inside delta; other
                // delta shapes never set it, so capturing here is exact.
                if (reader.TokenType == JsonTokenType.String)
                {
                    rStopReason = reader.GetString();
                    rSawStopReason = true;
                }
            }
            else
            {
                SkipContainer(ref reader);
            }

            return;
        }

        if (depth == 2 && inContentBlock)
        {
            if (reader.ValueTextEquals("id"u8))
            {
                rBlockId = reader.TokenType == JsonTokenType.String ? reader.GetString() ?? "0" : "0";
            }
            else if (reader.ValueTextEquals("type"u8))
            {
                rBlockType = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else if (reader.ValueTextEquals("name"u8))
            {
                rBlockName = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else
            {
                SkipContainer(ref reader);
            }

            return;
        }

        if (depth == 2 && inUsage)
        {
            if (reader.ValueTextEquals("input_tokens"u8))
            {
                rInputTokens = ReadTolerantInt(ref reader);
            }
            else if (reader.ValueTextEquals("output_tokens"u8))
            {
                rOutputTokens = ReadTolerantInt(ref reader);
            }
            else if (reader.ValueTextEquals("cache_read_input_tokens"u8))
            {
                rCacheReadTokens = ReadTolerantNullableInt(ref reader);
            }
            else if (reader.ValueTextEquals("cache_creation_input_tokens"u8))
            {
                rCacheWriteTokens = ReadTolerantNullableInt(ref reader);
            }
            else
            {
                SkipContainer(ref reader);
            }

            return;
        }

        SkipContainer(ref reader);
    }

    private static void Emit(
        List<LlmEvent> events, AnthropicEventKind kind,
        string blockId, string? blockType, string? blockName,
        string? deltaType, string? deltaText,
        string? stopReason, bool sawStopReason,
        int inputTokens, int outputTokens,
        int? cacheReadTokens, int? cacheWriteTokens, bool sawUsageObject)
    {
        switch (kind)
        {
            case AnthropicEventKind.MessageStart:
                events.Add(new StepStartEvent(0));
                break;

            case AnthropicEventKind.ContentBlockStart:
                if (blockType == "text")
                    events.Add(new TextStartEvent(blockId));
                else if (blockType == "thinking")
                    events.Add(new ThinkingStartEvent(blockId));
                else if (blockType == "tool_use")
                    events.Add(new ToolCallStartEvent(blockId, blockName ?? ""));
                break;

            case AnthropicEventKind.ContentBlockDelta:
                // DOM parity: deltas carry the fixed "0" id (Anthropic omits
                // ids on deltas) and only non-empty strings emit.
                if (deltaType == "text_delta" && !string.IsNullOrEmpty(deltaText))
                    events.Add(new TextDeltaEvent("0", deltaText!));
                else if (deltaType == "thinking_delta" && !string.IsNullOrEmpty(deltaText))
                    events.Add(new ThinkingDeltaEvent("0", deltaText!));
                else if (deltaType == "input_json_delta" && !string.IsNullOrEmpty(deltaText))
                    events.Add(new ToolCallDeltaEvent("0", deltaText!));
                break;

            case AnthropicEventKind.MessageDelta:
                // DOM parity: no stop_reason inside delta → no event at all.
                if (sawStopReason)
                {
                    events.Add(new StepFinishEvent(0, stopReason ?? "stop",
                        sawUsageObject
                            ? new Usage(inputTokens, outputTokens, null, cacheReadTokens, cacheWriteTokens)
                            : null));
                }

                break;

            case AnthropicEventKind.MessageStop:
            case AnthropicEventKind.Unknown:
                break;
        }
    }

    private static void SkipContainer(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            reader.Skip();
    }

    private static int ReadTolerantInt(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetInt32(out int direct))
                return direct;
            if (reader.TryGetDouble(out double dbl))
                return (int)dbl;
            return 0;
        }

        if (reader.TokenType == JsonTokenType.String &&
            int.TryParse(reader.GetString(), out int parsed))
        {
            return parsed;
        }

        return 0;
    }

    private static int? ReadTolerantNullableInt(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetInt32(out int direct))
                return direct;
            if (reader.TryGetDouble(out double dbl))
                return (int)dbl;
            return null;
        }

        if (reader.TokenType == JsonTokenType.String &&
            int.TryParse(reader.GetString(), out int parsed))
        {
            return parsed;
        }

        return null;
    }
}
