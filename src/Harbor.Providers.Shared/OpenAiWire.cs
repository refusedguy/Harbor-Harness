// Shared source: compiled INTO the OpenAI and OpenAiCompatible provider
// assemblies via <Compile Include> link items (ROP-A ПР.1). One canonical
// chat-completions chunk parser for both the native client and the generic
// adapter, with stable tool-call ids (ROP-A ПР.3).
//
// #171: span-based core — Utf8JsonReader over pooled UTF-8, no JsonDocument
// per chunk. Only payload strings (deltas, names, ids) allocate; dispatch
// compares via ValueTextEquals and unknown subtrees are skipped.

using System.Buffers;
using System.Text;
using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace Harbor.Providers.Internal;

/// <summary>
///     Canonical OpenAI chat-completions wire parsing shared by
///     <c>OpenAILlmClient</c> and <c>OpenAiCompatibleLlmClient</c> (ROP-A ПР.2).
///     Tool-call ids are stabilised through a per-stream index→id map so
///     servers that omit <c>id</c> on delta chunks still coalesce Start/Delta
///     events instead of silently losing arguments (ROP-A ПР.3).
/// </summary>
internal static class OpenAiWire
{
    /// <summary>
    ///     Parse one chat-completions chunk from UTF-8 JSON. <paramref name="indexToId" />
    ///     is per-stream state: first seen id wins for a tool-call index, missing
    ///     ids fall back to the map, then to <c>tc{index}</c>.
    ///     Evaluation is eager: the returned list owns every string, so the
    ///     caller's buffer may be returned to the pool immediately.
    ///     Semantics mirror the former DOM walk 1:1 (first choice only,
    ///     string-only content/name/args, tolerant counts).
    /// </summary>
    public static List<LlmEvent> ParseChatChunk(ReadOnlySpan<byte> utf8Json, Dictionary<int, string> indexToId)
    {
        var events = new List<LlmEvent>(capacity: 2);
        var reader = new Utf8JsonReader(utf8Json, isFinalBlock: true, state: default);

        int depth = 0;
        bool sawRoot = false;
        bool sawChoicesArray = false;
        bool inChoicesArray = false;
        bool choiceDone = false;
        bool inChoice = false;
        bool inDelta = false;
        bool inToolCalls = false;
        bool inUsage = false;

        string? finishReason = null;
        bool sawUsageObject = false;
        int promptTokens = 0;
        int completionTokens = 0;

        // Per-tool-call buffers, emitted at the tc EndObject (id may precede
        // function in any order on the wire).
        bool inTc = false;
        bool inFunction = false;
        int tcIndex = 0;
        string? tcWireId = null;
        string? tcName = null;
        string? tcArgs = null;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    if (depth == 0)
                    {
                        // Root must be the single top-level object; a root
                        // array/scalar parses as valid JSON but carries no
                        // chunk (DOM parity: empty) — skip it whole.
                        if (!sawRoot && reader.TokenType == JsonTokenType.StartObject)
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

                    if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        // Only a tracked array advances depth; a root-level
                        // (or otherwise unexpected) array skips whole.
                        if (depth >= 1)
                            depth++;
                        else
                            reader.Skip();

                        break;
                    }

                    // StartObject: a tracked scope only in known positions,
                    // otherwise skip the subtree without materializing it.
                    if (inChoicesArray && depth == 2)
                    {
                        depth++;
                        if (!choiceDone)
                        {
                            choiceDone = true;
                            inChoice = true;
                        }
                        else
                        {
                            reader.Skip();
                            depth--;
                        }
                    }
                    else if (inToolCalls && depth == 5)
                    {
                        depth++;
                        inTc = true;
                        inFunction = false;
                        tcIndex = 0;
                        tcWireId = null;
                        tcName = null;
                        tcArgs = null;
                    }
                    else
                    {
                        reader.Skip();
                    }

                    break;

                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    if (depth == 7 && inFunction)
                    {
                        inFunction = false;
                    }
                    else if (depth == 6 && inTc)
                    {
                        EmitToolCall(events, indexToId, tcIndex, tcWireId, tcName, tcArgs);
                        inTc = false;
                    }
                    else if (depth == 5 && inToolCalls && reader.TokenType == JsonTokenType.EndArray)
                    {
                        inToolCalls = false;
                    }
                    else if (depth == 4 && inDelta)
                    {
                        inDelta = false;
                    }
                    else if (depth == 3 && inChoice)
                    {
                        inChoice = false;
                    }
                    else if (depth == 2 && inUsage)
                    {
                        inUsage = false;
                        sawUsageObject = true;
                    }
                    else if (depth == 2 && inChoicesArray && reader.TokenType == JsonTokenType.EndArray)
                    {
                        inChoicesArray = false;
                    }

                    if (depth > 0)
                        depth--;
                    break;

                case JsonTokenType.PropertyName:
                    if (!reader.Read())
                        throw new JsonException("Truncated chunk: property without value.");
                    HandleValue(ref reader, depth,
                        inChoice, inDelta, inTc, inFunction, inUsage,
                        events,
                        ref sawChoicesArray, ref inChoicesArray, ref inDelta, ref inToolCalls, ref inFunction, ref inUsage,
                        ref depth,
                        ref finishReason, ref promptTokens, ref completionTokens,
                        ref tcIndex, ref tcWireId, ref tcName, ref tcArgs);
                    break;
            }
        }

        if (!sawChoicesArray)
        {
            // No choices array: usage-only chunk (DOM parity).
            if (sawUsageObject)
                events.Add(new StepFinishEvent(0, "stop", new Usage(promptTokens, completionTokens)));
            return events;
        }

        if (finishReason is not null)
        {
            events.Add(new StepFinishEvent(0, finishReason,
                sawUsageObject ? new Usage(promptTokens, completionTokens) : null));
        }

        return events;
    }

    private static void EmitToolCall(
        List<LlmEvent> events, Dictionary<int, string> indexToId,
        int index, string? wireId, string? name, string? args)
    {
        // Stable id (ROP-A ПР.3): wire id → remembered id → positional
        // fallback. Never a fresh Guid per chunk — that broke coalescing.
        // Only string wire ids count: servers send numeric ids, and the DOM
        // walk ignored those (ValueKind guard).
        string id = !string.IsNullOrEmpty(wireId)
            ? wireId!
            : indexToId.GetValueOrDefault(index) ?? $"tc{index}";
        indexToId[index] = id;

        if (!string.IsNullOrEmpty(name))
            events.Add(new ToolCallStartEvent(id, name!));
        if (!string.IsNullOrEmpty(args))
            events.Add(new ToolCallDeltaEvent(id, args!));
    }

    private static void HandleValue(
        ref Utf8JsonReader reader, int depth,
        bool inChoice, bool inDelta, bool inTc, bool inFunction, bool inUsage,
        List<LlmEvent> events,
        ref bool rSawChoicesArray, ref bool rInChoicesArray, ref bool rInDelta, ref bool rInToolCalls, ref bool rInFunction, ref bool rInUsage,
        ref int rDepth,
        ref string? rFinishReason, ref int rPromptTokens, ref int rCompletionTokens,
        ref int rTcIndex, ref string? rTcWireId, ref string? rTcName, ref string? rTcArgs)
    {
        // Depth-1 root properties.
        if (depth == 1)
        {
            if (reader.ValueTextEquals("choices"u8))
            {
                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    rSawChoicesArray = true;
                    rInChoicesArray = true;
                    rDepth++;
                }
                else
                {
                    SkipContainer(ref reader);
                }
            }
            else if (reader.ValueTextEquals("usage"u8))
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

        // Depth-3 choice properties (first choice only).
        if (depth == 3 && inChoice)
        {
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
            }
            else if (reader.ValueTextEquals("finish_reason"u8))
            {
                // DOM parity: only a string reason counts (null/other → none).
                rFinishReason = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else
            {
                SkipContainer(ref reader);
            }

            return;
        }

        // Depth-4 delta properties.
        if (depth == 4 && inDelta)
        {
            if (reader.ValueTextEquals("content"u8))
            {
                string? text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                if (!string.IsNullOrEmpty(text))
                    events.Add(new TextDeltaEvent("0", text!));
            }
            else if (reader.ValueTextEquals("reasoning_content"u8))
            {
                string? text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                if (!string.IsNullOrEmpty(text))
                    events.Add(new ThinkingDeltaEvent("0", text!));
            }
            else if (reader.ValueTextEquals("tool_calls"u8))
            {
                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    rInToolCalls = true;
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

        // Depth-6 tool-call properties.
        if (depth == 6 && inTc)
        {
            if (reader.ValueTextEquals("index"u8))
            {
                rTcIndex = ReadTolerantInt(ref reader);
            }
            else if (reader.ValueTextEquals("id"u8))
            {
                rTcWireId = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else if (reader.ValueTextEquals("function"u8))
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    rInFunction = true;
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

        // Depth-7 function properties.
        if (depth == 7 && inFunction)
        {
            if (reader.ValueTextEquals("name"u8))
            {
                rTcName = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else if (reader.ValueTextEquals("arguments"u8))
            {
                rTcArgs = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else
            {
                SkipContainer(ref reader);
            }

            return;
        }

        // Depth-2 usage properties.
        if (depth == 2 && inUsage)
        {
            if (reader.ValueTextEquals("prompt_tokens"u8))
            {
                rPromptTokens = ReadTolerantInt(ref reader);
            }
            else if (reader.ValueTextEquals("completion_tokens"u8))
            {
                rCompletionTokens = ReadTolerantInt(ref reader);
            }
            else
            {
                SkipContainer(ref reader);
            }

            return;
        }

        SkipContainer(ref reader);
    }

    private static void SkipContainer(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            reader.Skip();
    }

    /// <summary>
    ///     Best-effort integer read: some servers send counts as floats
    ///     (<c>7.0</c>) or strings (<c>"7"</c>). Never throws — returns 0
    ///     when the value is missing or unparseable (DOM parity).
    /// </summary>
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

    /// <summary>
    ///     Unified malformed-chunk policy (ROP-A ПР.4): a chunk that fails to
    ///     parse is logged, counted and SKIPPED — the stream survives a single
    ///     bad line. Terminal error events are reserved for auth/HTTP/network
    ///     failures; they never fire for wire noise.
    ///     The payload transcodes into a pooled buffer: no intermediate string
    ///     beyond the SSE line the pump already owns, no JsonDocument.
    /// </summary>
    public static IReadOnlyList<LlmEvent> TryParseChatChunkLine(
        string data, ChunkStreamState state, ILogger logger)
    {
        try
        {
            int byteCount = Encoding.UTF8.GetByteCount(data);
            byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                Encoding.UTF8.GetBytes(data, rented);
                return ParseChatChunk(rented.AsSpan(0, byteCount), state.IndexToId);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        catch (Exception ex)
        {
            state.CountMalformed();
            logger.LogWarning(ex, "Skipping malformed chat-completions chunk #{Count}: {Data}",
                state.MalformedChunks, data);
            return Array.Empty<LlmEvent>();
        }
    }
}
