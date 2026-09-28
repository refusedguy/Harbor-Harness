using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Providers.Internal;
using Microsoft.Extensions.Logging;

namespace Harbor.Providers.OpenAI;

/// <summary>
///     Maps OpenAI Responses-API SSE chunks to <see cref="LlmEvent" />.
///     (Chat Completions chunks are handled by the shared
///     <c>Harbor.Providers.Internal.OpenAiWire</c> helpers.)
///     Extracted from <see cref="OpenAILlmClient" /> (§ROP god-object split).
///     #171: span-based core — Utf8JsonReader over pooled UTF-8, no
///     JsonDocument per chunk. Dispatch is ordinal name compares; only
///     payload strings allocate.
/// </summary>
internal static class OpenAiResponsesMapper
{
    /// <summary>
    ///     Parse one SSE data line and write any emitted events directly into the channel.
    ///     The payload transcodes into a pooled buffer; a malformed line is
    ///     logged, counted and skipped (#203: the counter is new — the
    ///     former DOM walk only logged).
    /// </summary>
    public static async Task WriteResponsesEventsAsync(
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
            // #171: single-pass transcode — GetMaxByteCount rent + one
            // GetBytes (was GetByteCount + GetBytes: two passes per chunk).
            byte[] rented = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(data.Length));
            int byteCount;
            try
            {
                byteCount = Encoding.UTF8.GetBytes(data, rented);
                events = MapResponsesChunk(rented.AsSpan(0, byteCount), state);
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
            logger.LogWarning(ex, "Failed to parse OpenAI Responses chunk #{Count}: {Data}",
                state.MalformedChunks, data);
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
    ///     #203 E5: pass the stream <paramref name="state" /> (when available)
    ///     so function-call argument deltas resolve the call id recorded at
    ///     <c>output_item.added</c> — without it deltas are keyed by the bare
    ///     output index while the start carries the wire call id, and the
    ///     coalescer drops the args. Null state preserves the exact legacy
    ///     shapes (used by tests).
    /// </summary>
    internal static List<LlmEvent> MapResponsesChunk(ReadOnlySpan<byte> utf8Json, ChunkStreamState? state = null)
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
                    string prop = reader.GetString() ?? string.Empty;
                    if (!reader.Read())
                        throw new JsonException("Truncated chunk: property without value.");
                    HandleValue(ref reader, depth, prop, inItem, inResponse, inUsage, inReasoningDetails,
                        ref kind, ref inItem, ref inResponse, ref inUsage, ref inReasoningDetails, ref depth,
                        ref deltaText, ref outputIndex, ref sawOutputIndex,
                        ref itemType, ref callId, ref itemName,
                        ref inputTokens, ref outputTokens, ref reasoningTokens);
                    break;
            }
        }

        Emit(events, kind, deltaText, outputIndex, sawOutputIndex, itemType, callId, itemName,
            inputTokens, outputTokens, reasoningTokens, sawUsageObject, state);
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
        ref Utf8JsonReader reader, int depth, string prop, bool inItem, bool inResponse, bool inUsage, bool inReasoningDetails,
        ref ResponsesChunkKind rKind,
        ref bool rInItem, ref bool rInResponse, ref bool rInUsage, ref bool rInReasoningDetails, ref int rDepth,
        ref string? rDeltaText, ref int rOutputIndex, ref bool rSawOutputIndex,
        ref string? rItemType, ref string? rCallId, ref string? rItemName,
        ref int rInputTokens, ref int rOutputTokens, ref int? rReasoningTokens)
    {
        if (depth == 1)
        {
            if (prop == "type")
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

            if (prop == "delta")
            {
                rDeltaText = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                return;
            }

            if (prop == "output_index")
            {
                if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int oi))
                {
                    rOutputIndex = oi;
                    rSawOutputIndex = true;
                }

                return;
            }

            if (prop == "item")
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

            if (prop == "response")
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
            if (prop == "type")
            {
                rItemType = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else if (prop == "call_id")
            {
                rCallId = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else if (prop == "name")
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
            }
            else
            {
                SkipContainer(ref reader);
            }

            return;
        }

        if (depth == 3 && inUsage)
        {
            if (prop == "input_tokens")
            {
                rInputTokens = ReadTolerantInt(ref reader);
            }
            else if (prop == "output_tokens")
            {
                rOutputTokens = ReadTolerantInt(ref reader);
            }
            else if (prop == "output_tokens_details")
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
            if (prop == "reasoning_tokens")
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
        int inputTokens, int outputTokens, int? reasoningTokens, bool sawUsageObject,
        ChunkStreamState? state)
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
                    events.Add(new ToolCallDeltaEvent(ResolveArgumentsDeltaId(state, outputIndex, sawOutputIndex), deltaText!));
                break;

            case ResponsesChunkKind.OutputItemAdded:
                if (itemType == "function_call" && !string.IsNullOrEmpty(itemName))
                {
                    events.Add(new ToolCallStartEvent(callId ?? "0", itemName!));
                    // #203 E5: remember output_index→call_id so argument
                    // deltas correlate (else they key on the bare index).
                    if (state is not null && !string.IsNullOrEmpty(callId))
                        state.IndexToId[outputIndex] = callId!;
                }

                break;

            case ResponsesChunkKind.Completed:
                events.Add(new StepFinishEvent(0, "stop",
                    sawUsageObject ? new Usage(inputTokens, outputTokens, reasoningTokens) : null));
                break;

            case ResponsesChunkKind.Unknown:
                break;
        }
    }

    /// <summary>
    ///     Resolve the function-call arguments delta id: the wire call id
    ///     recorded at <c>output_item.added</c> for this output index; the
    ///     legacy positional index string when the added event was never
    ///     seen (counted, #203 B8); "0" when the index itself is missing.
    /// </summary>
    private static string ResolveArgumentsDeltaId(ChunkStreamState? state, int outputIndex, bool sawOutputIndex)
    {
        if (!sawOutputIndex)
            return "0";
        if (state is null)
            return outputIndex.ToString();
        string? remembered = state.IndexToId.GetValueOrDefault(outputIndex);
        if (remembered is not null)
            return remembered;
        state.CountRemap();
        return outputIndex.ToString();
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
