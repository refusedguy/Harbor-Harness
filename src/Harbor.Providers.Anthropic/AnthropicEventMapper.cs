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
            // #171: single-pass transcode — GetMaxByteCount rent + one
            // GetBytes (was GetByteCount + GetBytes: two passes per event).
            byte[] rented = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(data.Length));
            int byteCount;
            try
            {
                byteCount = Encoding.UTF8.GetBytes(data, rented);
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
            AnthropicEventMapperLog.FailedToParseEvent(logger, ex, state.MalformedChunks, data);
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
                    // The name is matched against this mapper's own literal
                    // table, so the dispatch costs NO string: the old path
                    // allocated one per property per event, and an event is
                    // what every token of every answer arrives as (see
                    // Utf8JsonReaderExtensions.PropertyName).
                    string prop = Utf8JsonReaderExtensions.PropertyName(ref reader, MatchEventProperty(reader.ValueSpan));
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

    /// <summary>
    ///     The property names this mapper dispatches on. The table is
    ///     EXHAUSTIVE over the literals <see cref="HandleValue" /> compares
    ///     against — a name missing from it would stop being dispatched on,
    ///     which is why the parity test pins the whole set.
    /// </summary>
    private static string? MatchEventProperty(ReadOnlySpan<byte> raw) => raw switch
    {
        // Ordered by how often an event carries them, not alphabetically.
        var x when x.SequenceEqual("type"u8) => "type",
        var x when x.SequenceEqual("delta"u8) => "delta",
        var x when x.SequenceEqual("content_block"u8) => "content_block",
        var x when x.SequenceEqual("usage"u8) => "usage",
        var x when x.SequenceEqual("index"u8) => "index",
        var x when x.SequenceEqual("text"u8) => "text",
        var x when x.SequenceEqual("id"u8) => "id",
        var x when x.SequenceEqual("name"u8) => "name",
        var x when x.SequenceEqual("thinking"u8) => "thinking",
        var x when x.SequenceEqual("partial_json"u8) => "partial_json",
        var x when x.SequenceEqual("stop_reason"u8) => "stop_reason",
        var x when x.SequenceEqual("input_tokens"u8) => "input_tokens",
        var x when x.SequenceEqual("output_tokens"u8) => "output_tokens",
        var x when x.SequenceEqual("cache_read_input_tokens"u8) => "cache_read_input_tokens",
        var x when x.SequenceEqual("cache_creation_input_tokens"u8) => "cache_creation_input_tokens",
        _ => null
    };

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
                    Utf8JsonReaderExtensions.SkipContainer(ref reader);
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
                    Utf8JsonReaderExtensions.SkipContainer(ref reader);
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
                    Utf8JsonReaderExtensions.SkipContainer(ref reader);
                }

                return;
            }

            if (prop == "index")
            {
                // content_block_start/_delta carry the block index at top
                // level — the correlation key for tool-arg deltas (#203 E5).
                rBlockIndex = Utf8JsonReaderExtensions.ReadTolerantInt(ref reader);
                return;
            }

            Utf8JsonReaderExtensions.SkipContainer(ref reader);
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
                Utf8JsonReaderExtensions.SkipContainer(ref reader);
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
                Utf8JsonReaderExtensions.SkipContainer(ref reader);
            }

            return;
        }

        if (depth == 2 && inUsage)
        {
            if (prop == "input_tokens")
            {
                rInputTokens = Utf8JsonReaderExtensions.ReadTolerantInt(ref reader);
            }
            else if (prop == "output_tokens")
            {
                rOutputTokens = Utf8JsonReaderExtensions.ReadTolerantInt(ref reader);
            }
            else if (prop == "cache_read_input_tokens")
            {
                rCacheReadTokens = Utf8JsonReaderExtensions.ReadTolerantNullableInt(ref reader);
            }
            else if (prop == "cache_creation_input_tokens")
            {
                rCacheWriteTokens = Utf8JsonReaderExtensions.ReadTolerantNullableInt(ref reader);
            }
            else
            {
                Utf8JsonReaderExtensions.SkipContainer(ref reader);
            }

            return;
        }

        Utf8JsonReaderExtensions.SkipContainer(ref reader);
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
        var context = new EmitContext(blockIndex, blockId, blockType, blockName,
            deltaType, deltaText, stopReason, sawStopReason,
            inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens,
            sawUsageObject, state);
        _byKind[(byte)kind].Emit(events, in context);
    }

    /// <summary>
    ///     Strategy for emitting one wire chunk. Handlers are stateless
    ///     singletons in the static tables below, so a new wire kind adds a
    ///     handler instead of a branch in <see cref="Emit" />.
    /// </summary>
    private interface IEventHandler
    {
        void Emit(List<LlmEvent> events, in EmitContext context);
    }

    /// <summary>
    ///     Fields buffered by the parser. A struct passed by <c>in</c> keeps
    ///     the per-chunk dispatch free of allocations.
    /// </summary>
    private readonly record struct EmitContext(
        int BlockIndex,
        string BlockId,
        string? BlockType,
        string? BlockName,
        string? DeltaType,
        string? DeltaText,
        string? StopReason,
        bool SawStopReason,
        int InputTokens,
        int OutputTokens,
        int? CacheReadTokens,
        int? CacheWriteTokens,
        bool SawUsageObject,
        ChunkStreamState? State);

    // Dispatch tables: the wire event kind and, inside the two content-block
    // kinds, the wire block/delta type. Built once; the hot path only reads.
    private static readonly IEventHandler[] _byKind =
    {
        NoOp.Instance,                  // Unknown
        new MessageStartHandler(),      // MessageStart
        new ContentBlockStartHandler(), // ContentBlockStart
        new ContentBlockDeltaHandler(), // ContentBlockDelta
        new MessageDeltaHandler(),      // MessageDelta
        NoOp.Instance                   // MessageStop
    };

    private static readonly Dictionary<string, IEventHandler> _startByType = new(StringComparer.Ordinal)
    {
        ["text"] = new TextStartHandler(),
        ["thinking"] = new ThinkingStartHandler(),
        ["tool_use"] = new ToolUseStartHandler()
    };

    private static readonly Dictionary<string, IEventHandler> _deltaByType = new(StringComparer.Ordinal)
    {
        ["text_delta"] = new TextDeltaHandler(),
        ["thinking_delta"] = new ThinkingDeltaHandler(),
        ["input_json_delta"] = new ToolCallDeltaHandler()
    };

    private static IEventHandler ForStart(string? blockType) =>
        blockType is not null && _startByType.TryGetValue(blockType, out var handler)
            ? handler
            : NoOp.Instance;

    private static IEventHandler ForDelta(string? deltaType) =>
        deltaType is not null && _deltaByType.TryGetValue(deltaType, out var handler)
            ? handler
            : NoOp.Instance;

    private sealed class NoOp : IEventHandler
    {
        public static readonly NoOp Instance = new();

        public void Emit(List<LlmEvent> events, in EmitContext context) { }
    }

    private sealed class MessageStartHandler : IEventHandler
    {
        public void Emit(List<LlmEvent> events, in EmitContext context) =>
            events.Add(new StepStartEvent(0));
    }

    private sealed class ContentBlockStartHandler : IEventHandler
    {
        public void Emit(List<LlmEvent> events, in EmitContext context) =>
            ForStart(context.BlockType).Emit(events, in context);
    }

    private sealed class TextStartHandler : IEventHandler
    {
        public void Emit(List<LlmEvent> events, in EmitContext context) =>
            events.Add(new TextStartEvent(context.BlockId));
    }

    private sealed class ThinkingStartHandler : IEventHandler
    {
        public void Emit(List<LlmEvent> events, in EmitContext context) =>
            events.Add(new ThinkingStartEvent(context.BlockId));
    }

    private sealed class ToolUseStartHandler : IEventHandler
    {
        public void Emit(List<LlmEvent> events, in EmitContext context)
        {
            events.Add(new ToolCallStartEvent(context.BlockId, context.BlockName ?? ""));
            // #203 E5: remember index→wire-id so the following
            // input_json deltas correlate (else they fall back to
            // "0" and the coalescer drops the args).
            var state = context.State;
            if (state is not null && !string.IsNullOrEmpty(context.BlockId))
                state.IndexToId[context.BlockIndex] = context.BlockId;
        }
    }

    private sealed class ContentBlockDeltaHandler : IEventHandler
    {
        public void Emit(List<LlmEvent> events, in EmitContext context) =>
            ForDelta(context.DeltaType).Emit(events, in context);
    }

    private sealed class TextDeltaHandler : IEventHandler
    {
        public void Emit(List<LlmEvent> events, in EmitContext context)
        {
            if (!string.IsNullOrEmpty(context.DeltaText))
                events.Add(new TextDeltaEvent("0", context.DeltaText!));
        }
    }

    private sealed class ThinkingDeltaHandler : IEventHandler
    {
        public void Emit(List<LlmEvent> events, in EmitContext context)
        {
            if (!string.IsNullOrEmpty(context.DeltaText))
                events.Add(new ThinkingDeltaEvent("0", context.DeltaText!));
        }
    }

    private sealed class ToolCallDeltaHandler : IEventHandler
    {
        public void Emit(List<LlmEvent> events, in EmitContext context)
        {
            if (!string.IsNullOrEmpty(context.DeltaText))
                events.Add(new ToolCallDeltaEvent(
                    ResolveToolDeltaId(context.State, context.BlockIndex), context.DeltaText!));
        }
    }

    private sealed class MessageDeltaHandler : IEventHandler
    {
        public void Emit(List<LlmEvent> events, in EmitContext context)
        {
            // DOM parity: no stop_reason inside delta → no event at all.
            if (context.SawStopReason)
            {
                events.Add(new StepFinishEvent(0, context.StopReason ?? "stop",
                    context.SawUsageObject
                        ? new Usage(context.InputTokens, context.OutputTokens, null,
                            context.CacheReadTokens, context.CacheWriteTokens)
                        : null));
            }
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

}

/// <summary>
///     SG7: BCL <c>[LoggerMessage]</c> delegates for <see cref="AnthropicEventMapper" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class AnthropicEventMapperLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to parse Anthropic event #{Count}: {Data}")]
    public static partial void FailedToParseEvent(ILogger logger, Exception ex, int count, string data);
}
