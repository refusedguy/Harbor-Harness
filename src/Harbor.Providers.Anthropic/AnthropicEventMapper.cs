using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Providers.Internal;
using Microsoft.Extensions.Logging;

namespace Harbor.Providers.Anthropic;

/// <summary>
///     Maps Anthropic Messages-API SSE events to <see cref="LlmEvent" />.
///     Extracted from <see cref="AnthropicLlmClient" /> (§ROP god-object split).
///     #171: span-based core — Utf8JsonReader over pooled UTF-8, no
///     JsonDocument per event. Dispatch is ordinal name compares; only
///     payload strings allocate.
/// </summary>
internal static class AnthropicEventMapper
{
    /// <summary>
    ///     Parse one SSE data line and write any emitted events directly into the
    ///     channel. The payload transcodes into a pooled buffer; a malformed
    ///     line is logged, counted and skipped (#203: the counter is new —
    ///     the former DOM walk only logged).
    /// </summary>
    public static async Task WriteAnthropicEventsAsync(
        string data,
        ChannelWriter<LlmEvent> writer,
        ChunkStreamState state,
        ILogger logger,
        CancellationToken ct)
    {
        List<LlmEvent> events;
        try
        {
            int remapsBefore = state.RemappedToolCalls;
            int byteCount = Encoding.UTF8.GetByteCount(data);
            byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                Encoding.UTF8.GetBytes(data, rented);
                events = MapAnthropicEvents(rented.AsSpan(0, byteCount), state);
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
            logger.LogWarning(ex, "Failed to parse Anthropic event #{Count}: {Data}",
                state.MalformedChunks, data);
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
    ///     #203 E5: pass the stream <paramref name="state" /> (when available)
    ///     so <c>input_json_delta</c> chunks resolve the block id recorded at
    ///     <c>content_block_start</c> — without it every tool-args delta is
    ///     emitted under the fixed "0" id and the coalescer drops the args
    ///     (start carries the wire block id). Null state preserves the exact
    ///     legacy shapes (used by tests).
    /// </summary>
    internal static List<LlmEvent> MapAnthropicEvents(ReadOnlySpan<byte> utf8Json, ChunkStreamState? state = null)
    {
        var events = new List<LlmEvent>(capacity: 2);
        var reader = new Utf8JsonReader(utf8Json, isFinalBlock: true, state: default);

        int depth = 0;
        bool sawRoot = false;
        AnthropicEventKind kind = AnthropicEventKind.Unknown;
        bool inDelta = false;
        bool inContentBlock = false;
        bool inUsage = false;

        // Top-level block index (content_block_start/_delta): the key that
        // correlates deltas with the wire block id recorded at start (#203).
        int blockIndex = 0;
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
                    string prop = reader.GetString() ?? string.Empty;
                    if (!reader.Read())
                        throw new JsonException("Truncated line: property without value.");
                    HandleValue(ref reader, depth, prop, inDelta, inContentBlock, inUsage,
                        ref kind, ref inDelta, ref inContentBlock, ref inUsage, ref depth,
                        ref blockIndex, ref blockId, ref blockType, ref blockName,
                        ref deltaType, ref deltaText,
                        ref stopReason, ref sawStopReason,
                        ref inputTokens, ref outputTokens,
                        ref cacheReadTokens, ref cacheWriteTokens);
                    break;
            }
        }

        Emit(events, kind, blockIndex, blockId, blockType, blockName, deltaType, deltaText,
            stopReason, sawStopReason, inputTokens, outputTokens,
            cacheReadTokens, cacheWriteTokens, sawUsageObject, state);
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
        ref Utf8JsonReader reader, int depth, string prop, bool inDelta, bool inContentBlock, bool inUsage,
        ref AnthropicEventKind rKind,
        ref bool rInDelta, ref bool rInContentBlock, ref bool rInUsage, ref int rDepth,
        ref int rBlockIndex, ref string rBlockId, ref string? rBlockType, ref string? rBlockName,
        ref string? rDeltaType, ref string? rDeltaText,
        ref string? rStopReason, ref bool rSawStopReason,
        ref int rInputTokens, ref int rOutputTokens,
        ref int? rCacheReadTokens, ref int? rCacheWriteTokens)
    {
        if (depth == 1)
        {
            if (prop == "type")
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    // NOTE: the VALUE carries the event type here (prop is
                    // just "type"), so compare the value span, not the name.
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

            if (prop == "delta")
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

            if (prop == "content_block")
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

            if (prop == "usage")
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

            if (prop == "index")
            {
                // content_block_start/_delta carry the block index at top
                // level — the correlation key for tool-arg deltas (#203 E5).
                rBlockIndex = ReadTolerantInt(ref reader);
                return;
            }

            SkipContainer(ref reader);
            return;
        }

        if (depth == 2 && inDelta)
        {
            if (prop == "type")
            {
                rDeltaType = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else if (prop == "text" ||
                     prop == "thinking" ||
                     prop == "partial_json")
            {
                rDeltaText = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else if (prop == "stop_reason")
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
            if (prop == "id")
            {
                rBlockId = reader.TokenType == JsonTokenType.String ? reader.GetString() ?? "0" : "0";
            }
            else if (prop == "type")
            {
                rBlockType = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else if (prop == "name")
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
            if (prop == "input_tokens")
            {
                rInputTokens = ReadTolerantInt(ref reader);
            }
            else if (prop == "output_tokens")
            {
                rOutputTokens = ReadTolerantInt(ref reader);
            }
            else if (prop == "cache_read_input_tokens")
            {
                rCacheReadTokens = ReadTolerantNullableInt(ref reader);
            }
            else if (prop == "cache_creation_input_tokens")
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
        int blockIndex, string blockId, string? blockType, string? blockName,
        string? deltaType, string? deltaText,
        string? stopReason, bool sawStopReason,
        int inputTokens, int outputTokens,
        int? cacheReadTokens, int? cacheWriteTokens, bool sawUsageObject,
        ChunkStreamState? state)
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
                {
                    events.Add(new ToolCallStartEvent(blockId, blockName ?? ""));
                    // #203 E5: remember index→wire-id so the following
                    // input_json deltas correlate (else they fall back to
                    // "0" and the coalescer drops the args).
                    if (state is not null && !string.IsNullOrEmpty(blockId))
                        state.IndexToId[blockIndex] = blockId;
                }

                break;

            case AnthropicEventKind.ContentBlockDelta:
                // DOM parity: deltas carry the fixed "0" id (Anthropic omits
                // ids on deltas) and only non-empty strings emit.
                if (deltaType == "text_delta" && !string.IsNullOrEmpty(deltaText))
                    events.Add(new TextDeltaEvent("0", deltaText!));
                else if (deltaType == "thinking_delta" && !string.IsNullOrEmpty(deltaText))
                    events.Add(new ThinkingDeltaEvent("0", deltaText!));
                else if (deltaType == "input_json_delta" && !string.IsNullOrEmpty(deltaText))
                    events.Add(new ToolCallDeltaEvent(ResolveToolDeltaId(state, blockIndex), deltaText!));
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

    /// <summary>
    ///     Resolve the tool-args delta id: the wire block id recorded at
    ///     <c>content_block_start</c> for this index; a positional fallback
    ///     when the start was never seen (counted, #203 B8); the legacy
    ///     fixed "0" on the stateless path (tests).
    /// </summary>
    private static string ResolveToolDeltaId(ChunkStreamState? state, int blockIndex)
    {
        if (state is null)
            return "0";
        string? remembered = state.IndexToId.GetValueOrDefault(blockIndex);
        if (remembered is not null)
            return remembered;
        state.CountRemap();
        return $"tc{blockIndex}";
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
