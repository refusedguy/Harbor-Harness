using System.Buffers;
using System.Text;
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
///     #171: span-based core — Utf8JsonReader over pooled UTF-8, no
///     JsonDocument per chunk. Dispatch compares via ValueTextEquals; only
///     payload strings allocate.
/// </summary>
internal static class OpenAiResponsesMapper
{
    /// <summary>
    ///     Parse one SSE data line and write any emitted events directly into the channel.
    ///     The payload transcodes into a pooled buffer; a malformed line is
    ///     logged and skipped (same as the former DOM walk).
    /// </summary>
    public static async Task WriteResponsesEventsAsync(
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
                events = MapResponsesChunk(rented.AsSpan(0, byteCount));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse OpenAI Responses chunk: {Data}", data);
            return;
        }

        foreach (var evt in events)
        {
            await writer.WriteAsync(evt, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Map one Responses-API SSE chunk from UTF-8 JSON. Fields buffer
    ///     across the single pass and emit at the end, so property order on
    ///     the wire never matters. Unknown chunk types yield no events.
    ///     Tolerance deltas vs the DOM walk (all strictly looser, never
    ///     stricter): a non-string delta is ignored instead of failing the
    ///     chunk; a missing/non-integer output_index falls back to "0".
    /// </summary>
    internal static List<LlmEvent> MapResponsesChunk(ReadOnlySpan<byte> utf8Json)
    {
        var events = new List<LlmEvent>(capacity: 2);
        var reader = new Utf8JsonReader(utf8Json, isFinalBlock: true, state: default);

        int depth = 0;
        bool sawRoot = false;
        ResponsesChunkKind kind = ResponsesChunkKind.Unknown;
        bool inItem = false;
        bool inResponse = false;
        bool inUsage = false;
        bool inReasoningDetails = false;

        string? deltaText = null;
        int outputIndex = 0;
        bool sawOutputIndex = false;
        string? itemType = null;
        string? callId = null;
        string? itemName = null;
        int inputTokens = 0;
        int outputTokens = 0;
        int? reasoningTokens = null;
        bool sawUsageObject = false;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    if (depth == 0)
                    {
                        // Single top-level object only (DOM parity) — skip
                        // anything else whole.
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
                    if (depth == 4 && inReasoningDetails)
                    {
                        inReasoningDetails = false;
                    }
                    else if (depth == 3 && inUsage)
                    {
                        inUsage = false;
                        sawUsageObject = true;
                    }
                    else if (depth == 2)
                    {
                        inItem = false;
                        inResponse = false;
                    }

                    if (depth > 0)
                        depth--;
                    break;

                case JsonTokenType.PropertyName:
                    if (!reader.Read())
                        throw new JsonException("Truncated chunk: property without value.");
                    HandleValue(ref reader, depth, inItem, inResponse, inUsage, inReasoningDetails,
                        ref kind, ref inItem, ref inResponse, ref inUsage, ref inReasoningDetails, ref depth,
                        ref deltaText, ref outputIndex, ref sawOutputIndex,
                        ref itemType, ref callId, ref itemName,
                        ref inputTokens, ref outputTokens, ref reasoningTokens);
                    break;
            }
        }

        Emit(events, kind, deltaText, outputIndex, sawOutputIndex, itemType, callId, itemName,
            inputTokens, outputTokens, reasoningTokens, sawUsageObject);
        return events;
    }

    private enum ResponsesChunkKind : byte
    {
        Unknown,
        Created,
        OutputTextDelta,
        ReasoningDelta,
        FunctionCallArgumentsDelta,
        OutputItemAdded,
        Completed,
    }

    private static void HandleValue(
        ref Utf8JsonReader reader, int depth, bool inItem, bool inResponse, bool inUsage, bool inReasoningDetails,
        ref ResponsesChunkKind rKind,
        ref bool rInItem, ref bool rInResponse, ref bool rInUsage, ref bool rInReasoningDetails, ref int rDepth,
        ref string? rDeltaText, ref int rOutputIndex, ref bool rSawOutputIndex,
        ref string? rItemType, ref string? rCallId, ref string? rItemName,
        ref int rInputTokens, ref int rOutputTokens, ref int? rReasoningTokens)
    {
        if (depth == 1)
        {
            if (reader.ValueTextEquals("type"u8))
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    if (reader.ValueTextEquals("response.created"u8))
                        rKind = ResponsesChunkKind.Created;
                    else if (reader.ValueTextEquals("response.output_text.delta"u8))
                        rKind = ResponsesChunkKind.OutputTextDelta;
                    else if (reader.ValueTextEquals("response.reasoning.delta"u8))
                        rKind = ResponsesChunkKind.ReasoningDelta;
                    else if (reader.ValueTextEquals("response.function_call_arguments.delta"u8))
                        rKind = ResponsesChunkKind.FunctionCallArgumentsDelta;
                    else if (reader.ValueTextEquals("response.output_item.added"u8))
                        rKind = ResponsesChunkKind.OutputItemAdded;
                    else if (reader.ValueTextEquals("response.completed"u8))
                        rKind = ResponsesChunkKind.Completed;
                }

                return;
            }

            if (reader.ValueTextEquals("delta"u8))
            {
                rDeltaText = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                return;
            }

            if (reader.ValueTextEquals("output_index"u8))
            {
                if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int oi))
                {
                    rOutputIndex = oi;
                    rSawOutputIndex = true;
                }

                return;
            }

            if (reader.ValueTextEquals("item"u8))
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    rInItem = true;
                    rDepth++;
                }
                else
                {
                    SkipContainer(ref reader);
                }

                return;
            }

            if (reader.ValueTextEquals("response"u8))
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    rInResponse = true;
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

        if (depth == 2 && inItem)
        {
            if (reader.ValueTextEquals("type"u8))
            {
                rItemType = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else if (reader.ValueTextEquals("call_id"u8))
            {
                rCallId = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else if (reader.ValueTextEquals("name"u8))
            {
                rItemName = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else
            {
                SkipContainer(ref reader);
            }

            return;
        }

        if (depth == 2 && inResponse)
        {
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
            }
            else
            {
                SkipContainer(ref reader);
            }

            return;
        }

        if (depth == 3 && inUsage)
        {
            if (reader.ValueTextEquals("input_tokens"u8))
            {
                rInputTokens = ReadTolerantInt(ref reader);
            }
            else if (reader.ValueTextEquals("output_tokens"u8))
            {
                rOutputTokens = ReadTolerantInt(ref reader);
            }
            else if (reader.ValueTextEquals("output_tokens_details"u8))
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    rInReasoningDetails = true;
                    rDepth++;
                }
                else
                {
                    SkipContainer(ref reader);
                }
            }
            else
            {
                SkipContainer(ref reader);
            }

            return;
        }

        if (depth == 4 && inReasoningDetails)
        {
            if (reader.ValueTextEquals("reasoning_tokens"u8))
            {
                rReasoningTokens = ReadTolerantNullableInt(ref reader);
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
        List<LlmEvent> events, ResponsesChunkKind kind, string? deltaText,
        int outputIndex, bool sawOutputIndex, string? itemType, string? callId, string? itemName,
        int inputTokens, int outputTokens, int? reasoningTokens, bool sawUsageObject)
    {
        switch (kind)
        {
            case ResponsesChunkKind.Created:
                events.Add(new StepStartEvent(0));
                break;

            case ResponsesChunkKind.OutputTextDelta:
                if (!string.IsNullOrEmpty(deltaText))
                    events.Add(new TextDeltaEvent("0", deltaText!));
                break;

            case ResponsesChunkKind.ReasoningDelta:
                if (!string.IsNullOrEmpty(deltaText))
                    events.Add(new ThinkingDeltaEvent("0", deltaText!));
                break;

            case ResponsesChunkKind.FunctionCallArgumentsDelta:
                if (!string.IsNullOrEmpty(deltaText))
                    events.Add(new ToolCallDeltaEvent(sawOutputIndex ? outputIndex.ToString() : "0", deltaText!));
                break;

            case ResponsesChunkKind.OutputItemAdded:
                if (itemType == "function_call" && !string.IsNullOrEmpty(itemName))
                    events.Add(new ToolCallStartEvent(callId ?? "0", itemName!));
                break;

            case ResponsesChunkKind.Completed:
                events.Add(new StepFinishEvent(0, "stop",
                    sawUsageObject ? new Usage(inputTokens, outputTokens, reasoningTokens) : null));
                break;

            case ResponsesChunkKind.Unknown:
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
