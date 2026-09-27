extern alias CompatWire;

using System.Text;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Providers.Anthropic;
using Harbor.Providers.Ollama;
using Harbor.Providers.OpenAI;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using CompatOpenAiWire = CompatWire::Harbor.Providers.Internal.OpenAiWire;
using CompatChunkState = CompatWire::Harbor.Providers.Internal.ChunkStreamState;

namespace Harbor.Providers.Tests;

/// <summary>
///     #171: behavioral parity for the span-based chunk parsers
///     (Utf8JsonReader over pooled UTF-8, no JsonDocument per chunk).
///     Each test pins the DOM walk's observable contract: event shapes,
///     stable tool-call ids, tolerant counts, skip-and-count malformed
///     policy. Plus one allocation tripwire proving unknown payloads are
///     skipped without materializing (a DOM revert blows the budget).
/// </summary>
public class SpanParserTests
{
    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    // ── OpenAiWire ───────────────────────────────────────────────────

    [Test]
    public async Task OpenAiWire_TextDelta_Parses()
    {
        var events = CompatOpenAiWire.ParseChatChunk(
            Utf8("""{"choices":[{"delta":{"content":"hello"}}]}"""),
            new Dictionary<int, string>());

        var delta = events.OfType<TextDeltaEvent>().Single();
        await Assert.That(delta.Id).IsEqualTo("0");
        await Assert.That(delta.Delta).IsEqualTo("hello");
    }

    [Test]
    public async Task OpenAiWire_ToolCall_KeepsStableIdAcrossChunks()
    {
        var map = new Dictionary<int, string>();
        var first = CompatOpenAiWire.ParseChatChunk(
            Utf8("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_X","function":{"name":"read","arguments":"{\"pa"}}]}}]}"""),
            map);
        var second = CompatOpenAiWire.ParseChatChunk(
            Utf8("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th"}}]}}]}"""),
            map);

        await Assert.That(first.OfType<ToolCallStartEvent>().Single().Id).IsEqualTo("call_X");
        var stableIds = second.OfType<ToolCallDeltaEvent>().Select(d => d.Id).Distinct().ToList();
        await Assert.That(stableIds.Count).IsEqualTo(1);
        await Assert.That(stableIds[0]).IsEqualTo("call_X");
    }

    [Test]
    public async Task OpenAiWire_UsageOnlyChunk_EmitsStopFinish()
    {
        var events = CompatOpenAiWire.ParseChatChunk(
            Utf8("""{"usage":{"prompt_tokens":7,"completion_tokens":3}}"""),
            new Dictionary<int, string>());

        var finish = events.OfType<StepFinishEvent>().Single();
        await Assert.That(finish.FinishReason).IsEqualTo("stop");
        await Assert.That(finish.Usage!.InputTokens).IsEqualTo(7);
        await Assert.That(finish.Usage.OutputTokens).IsEqualTo(3);
    }

    [Test]
    public async Task OpenAiWire_FloatAndStringCounts_DoNotKillChunk()
    {
        // DOM parity (issue #86): counts arrive as 7.0 or "7" in the wild.
        var events = CompatOpenAiWire.ParseChatChunk(
            Utf8("""{"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":7.0,"completion_tokens":"3"}}"""),
            new Dictionary<int, string>());

        var finish = events.OfType<StepFinishEvent>().Single();
        await Assert.That(finish.Usage!.InputTokens).IsEqualTo(7);
        await Assert.That(finish.Usage.OutputTokens).IsEqualTo(3);
    }

    [Test]
    public async Task OpenAiWire_MalformedLine_SkippedAndCounted()
    {
        var state = new CompatChunkState();
        var events = CompatOpenAiWire.TryParseChatChunkLine("{not json", state, NullLogger.Instance);

        await Assert.That(events.Count).IsEqualTo(0);
        await Assert.That(state.MalformedChunks).IsEqualTo(1);
    }

    // ── Anthropic ────────────────────────────────────────────────────

    [Test]
    public async Task Anthropic_TextDelta_Parses()
    {
        var events = AnthropicEventMapper.MapAnthropicEvents(
            Utf8("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hi"}}"""));

        var delta = events.OfType<TextDeltaEvent>().Single();
        await Assert.That(delta.Id).IsEqualTo("0");
        await Assert.That(delta.Delta).IsEqualTo("hi");
    }

    [Test]
    public async Task Anthropic_ToolUseStart_AndPartialJson()
    {
        var start = AnthropicEventMapper.MapAnthropicEvents(
            Utf8("""{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"read"}}"""));
        var delta = AnthropicEventMapper.MapAnthropicEvents(
            Utf8("""{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"path\":"}}"""));

        var started = start.OfType<ToolCallStartEvent>().Single();
        await Assert.That(started.Id).IsEqualTo("toolu_1");
        await Assert.That(started.ToolName).IsEqualTo("read");
        await Assert.That(delta.OfType<ToolCallDeltaEvent>().Single().ArgsDelta).IsEqualTo("{\"path\":");
    }

    [Test]
    public async Task Anthropic_MessageDelta_UsageAndStop()
    {
        var events = AnthropicEventMapper.MapAnthropicEvents(
            Utf8("""{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"input_tokens":10,"output_tokens":4}}"""));

        var finish = events.OfType<StepFinishEvent>().Single();
        await Assert.That(finish.FinishReason).IsEqualTo("end_turn");
        await Assert.That(finish.Usage!.InputTokens).IsEqualTo(10);
        await Assert.That(finish.Usage.OutputTokens).IsEqualTo(4);
    }

    [Test]
    public async Task Anthropic_MessageStop_EmitsNothing()
    {
        var events = AnthropicEventMapper.MapAnthropicEvents(Utf8("""{"type":"message_stop"}"""));
        await Assert.That(events.Count).IsEqualTo(0);
    }

    // ── Responses ────────────────────────────────────────────────────

    [Test]
    public async Task Responses_TextAndFunctionArgs_Parse()
    {
        var text = OpenAiResponsesMapper.MapResponsesChunk(
            Utf8("""{"type":"response.output_text.delta","output_index":0,"delta":"hello"}"""));
        var args = OpenAiResponsesMapper.MapResponsesChunk(
            Utf8("""{"type":"response.function_call_arguments.delta","output_index":2,"delta":"{\"a\":1}"}"""));

        await Assert.That(text.OfType<TextDeltaEvent>().Single().Delta).IsEqualTo("hello");
        var toolDelta = args.OfType<ToolCallDeltaEvent>().Single();
        await Assert.That(toolDelta.Id).IsEqualTo("2");
        await Assert.That(toolDelta.ArgsDelta).IsEqualTo("{\"a\":1}");
    }

    [Test]
    public async Task Responses_ItemAdded_AndCompleted()
    {
        var added = OpenAiResponsesMapper.MapResponsesChunk(
            Utf8("""{"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","call_id":"call_9","name":"bash"}}"""));
        var done = OpenAiResponsesMapper.MapResponsesChunk(
            Utf8("""{"type":"response.completed","response":{"usage":{"input_tokens":5,"output_tokens":2,"output_tokens_details":{"reasoning_tokens":1}}}}"""));

        var started = added.OfType<ToolCallStartEvent>().Single();
        await Assert.That(started.Id).IsEqualTo("call_9");
        await Assert.That(started.ToolName).IsEqualTo("bash");
        var finish = done.OfType<StepFinishEvent>().Single();
        await Assert.That(finish.Usage!.InputTokens).IsEqualTo(5);
        await Assert.That(finish.Usage.ReasoningTokens).IsEqualTo(1);
    }

    // ── Ollama ───────────────────────────────────────────────────────

    [Test]
    public async Task Ollama_ContentToolsDone_Parse()
    {
        var events = OllamaLlmClient.MapNdjsonChunk(
            Utf8("""{"message":{"content":"yo","tool_calls":[{"index":0,"id":"c1","function":{"name":"ls","arguments":"{}"}}]},"done":true,"prompt_eval_count":3,"eval_count":9}"""),
            new Dictionary<int, string>());

        // DOM parity: content first, then tool calls, then done.
        await Assert.That(events[0] is TextDeltaEvent).IsTrue();
        var started = events.OfType<ToolCallStartEvent>().Single();
        await Assert.That(started.Id).IsEqualTo("c1");
        var finish = events.OfType<StepFinishEvent>().Single();
        await Assert.That(finish.Usage!.InputTokens).IsEqualTo(3);
        await Assert.That(finish.Usage.OutputTokens).IsEqualTo(9);
    }

    [Test]
    public async Task Ollama_ObjectArguments_Reserialize()
    {
        // GetRawText parity: object-shaped arguments survive canonically.
        var events = OllamaLlmClient.MapNdjsonChunk(
            Utf8("""{"message":{"tool_calls":[{"function":{"name":"x","arguments":{"a":1}}}]},"done":false}"""),
            new Dictionary<int, string>());

        await Assert.That(events.OfType<ToolCallDeltaEvent>().Single().ArgsDelta).IsEqualTo("{\"a\":1}");
    }

    // ── Allocation tripwire ──────────────────────────────────────────

    [Test]
    public async Task SpanParse_JunkPayload_StaysBounded()
    {
        // A 5KB unknown blob: the span walk skips it without materializing.
        // A JsonDocument-per-chunk revert allocates ~6KB+/parse and blows
        // this budget; the span path stays near ~0.4KB (events + strings).
        string json = "{\"choices\":[{\"delta\":{\"content\":\"hi\"},\"finish_reason\":\"stop\"}],"
            + "\"unknown_blob\":\"" + new string('x', 5000) + "\"}";
        byte[] utf8 = Utf8(json);
        var map = new Dictionary<int, string>();

        for (int i = 0; i < 20; i++)
        {
            map.Clear();
            _ = CompatOpenAiWire.ParseChatChunk(utf8, map);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            map.Clear();
            _ = CompatOpenAiWire.ParseChatChunk(utf8, map);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        await Assert.That(after - before).IsLessThanOrEqualTo(50 * 2048);
    }

    // ── #203: unattached usage + remap diagnostics ───────────────────

    [Test]
    public async Task OpenAiWire_EmptyChoicesUsageChunk_EmitsStopFinishWithUsage()
    {
        // OpenAI include_usage trailing chunk: empty choices + usage, no
        // finish_reason. Dropping it loses the turn's token stats silently.
        var events = CompatOpenAiWire.ParseChatChunk(
            Utf8("""{"choices":[],"usage":{"prompt_tokens":7,"completion_tokens":3}}"""),
            new Dictionary<int, string>());

        var finish = events.OfType<StepFinishEvent>().Single();
        await Assert.That(finish.FinishReason).IsEqualTo("stop");
        await Assert.That(finish.Usage!.InputTokens).IsEqualTo(7);
        await Assert.That(finish.Usage.OutputTokens).IsEqualTo(3);
    }

    [Test]
    public async Task OpenAiWire_TrailingUsageAfterBareFinish_ReemitsRememberedReason()
    {
        // A compat server splits usage off the finish chunk. The synthesized
        // finish must reuse the remembered reason — a fresh "stop" after a
        // tool_calls finish would flip the stop reason and skip execution.
        var state = new CompatChunkState();
        var first = CompatOpenAiWire.ParseChatChunk(
            Utf8("""{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}"""),
            state.IndexToId, state);
        var second = CompatOpenAiWire.ParseChatChunk(
            Utf8("""{"choices":[],"usage":{"prompt_tokens":7,"completion_tokens":3}}"""),
            state.IndexToId, state);

        await Assert.That(first.OfType<StepFinishEvent>().Single().FinishReason).IsEqualTo("tool_calls");
        var trailing = second.OfType<StepFinishEvent>().Single();
        await Assert.That(trailing.FinishReason).IsEqualTo("tool_calls");
        await Assert.That(trailing.Usage!.InputTokens).IsEqualTo(7);
        await Assert.That(trailing.Usage.OutputTokens).IsEqualTo(3);
    }

    [Test]
    public async Task OpenAiWire_DuplicateTrailingUsage_AfterDelivery_DroppedAndCounted()
    {
        // Usage attached to the finish chunk AND trailed afterwards: the
        // duplicate carries nothing new — counted, not re-emitted.
        var state = new CompatChunkState();
        var first = CompatOpenAiWire.ParseChatChunk(
            Utf8("""{"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":7,"completion_tokens":3}}"""),
            state.IndexToId, state);
        var second = CompatOpenAiWire.ParseChatChunk(
            Utf8("""{"choices":[],"usage":{"prompt_tokens":7,"completion_tokens":3}}"""),
            state.IndexToId, state);

        await Assert.That(first.OfType<StepFinishEvent>().Count()).IsEqualTo(1);
        await Assert.That(second.OfType<StepFinishEvent>().Count()).IsEqualTo(0);
        await Assert.That(state.DroppedUsageChunks).IsEqualTo(1);
    }

    [Test]
    public async Task OpenAiWire_MissingWireId_Fallback_CountsRemap()
    {
        // B8: the positional tc{index} fallback keeps coalescing but must be
        // diagnosable — counted on the stream state (warned once per stream
        // at the TryParse site).
        var state = new CompatChunkState();
        var events = CompatOpenAiWire.ParseChatChunk(
            Utf8("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"ls","arguments":"{}"}}]}}]}"""),
            state.IndexToId, state);

        await Assert.That(events.OfType<ToolCallStartEvent>().Single().Id).IsEqualTo("tc0");
        await Assert.That(state.RemappedToolCalls).IsEqualTo(1);
    }
}
