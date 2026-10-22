extern alias AnthropicWire;
extern alias CompatWire;

using System.Text;
using Harbor.Abstractions.Events;
using Harbor.Providers.Anthropic;
using Harbor.Providers.Ollama;
using Harbor.Providers.OpenAI;
using TUnit.Assertions;
using AnthropicChunkState = AnthropicWire::Harbor.Providers.Internal.ChunkStreamState;
using CompatOpenAiWire = CompatWire::Harbor.Providers.Internal.OpenAiWire;

namespace Harbor.Providers.Tests;

/// <summary>
///     Behaviour parity for the property-name dispatch of the four span
///     parsers — the step that decides which wire field a chunk's property
///     belongs to.
/// </summary>
/// <remarks>
///     <para>
///         The parsers used to build a fresh <see cref="string" /> for every
///         property name they walked past, only to compare it against a
///         handful of literals. The names are now matched against the RAW
///         bytes of the name, and the literal is returned interned, so the
///         dispatch allocates nothing.
///     </para>
///     <para>
///         Two things have to hold for that to be a pure optimisation, and
///         this file is where they are pinned:
///     </para>
///     <list type="number">
///         <item>
///             <description>
///                 EVERY name the parser dispatches on still dispatches. A
///                 name missing from the matcher's table would stop being
///                 read, and the chunk would silently lose a field — the
///                 failure mode with no symptom of its own.
///             </description>
///         </item>
///         <item>
///             <description>
///                 A name written with an ESCAPE sequence still dispatches.
///                 The raw bytes of such a name are not the name's text, so
///                 the matcher cannot decide it — the reader has to. This is
///                 the path that keeps the optimisation from changing what
///                 the parser accepts.
///             </description>
///         </item>
///     </list>
/// </remarks>
public class ChunkPropertyNameParityTests
{
    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    // ── OpenAI chat completions (the shared wire core) ─────────────────

    /// <summary>
    ///     Every name <c>OpenAiWire</c> dispatches on, one chunk per name
    ///     group, each with the observable it is supposed to produce.
    /// </summary>
    [Test]
    public async Task OpenAiWire_EveryDispatchedName_StillDispatches()
    {
        // choices + delta + content
        var text = CompatOpenAiWire.ParseChatChunk(
            Utf8("""{"choices":[{"delta":{"content":"x"}}]}"""),
            new Dictionary<int, string>());
        await Assert.That(text.OfType<TextDeltaEvent>().Single().Delta).IsEqualTo("x");

        // reasoning_content
        var thinking = CompatOpenAiWire.ParseChatChunk(
            Utf8("""{"choices":[{"delta":{"reasoning_content":"r"}}]}"""),
            new Dictionary<int, string>());
        await Assert.That(thinking.OfType<ThinkingDeltaEvent>().Single().Delta).IsEqualTo("r");

        // tool_calls + index + id + function + name + arguments
        var tool = CompatOpenAiWire.ParseChatChunk(
            Utf8("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"read","arguments":"{}"}}]}}]}"""),
            new Dictionary<int, string>());
        await Assert.That(tool.OfType<ToolCallStartEvent>().Single().ToolName).IsEqualTo("read");
        await Assert.That(tool.OfType<ToolCallDeltaEvent>().Single().ArgsDelta).IsEqualTo("{}");

        // finish_reason
        var finish = CompatOpenAiWire.ParseChatChunk(
            Utf8("""{"choices":[{"finish_reason":"stop"}]}"""),
            new Dictionary<int, string>());
        await Assert.That(finish.OfType<StepFinishEvent>().Single().FinishReason).IsEqualTo("stop");

        // usage + prompt_tokens + completion_tokens
        var usage = CompatOpenAiWire.ParseChatChunk(
            Utf8("""{"usage":{"prompt_tokens":7,"completion_tokens":3}}"""),
            new Dictionary<int, string>());
        var step = usage.OfType<StepFinishEvent>().Single();
        await Assert.That(step.Usage!.InputTokens).IsEqualTo(7);
        await Assert.That(step.Usage.OutputTokens).IsEqualTo(3);
    }

    /// <summary>
    ///     The tool-call index, read on its own: the positional id fallback
    ///     is <c>tc{index}</c>, so a dropped "index" would rename the call.
    /// </summary>
    [Test]
    public async Task OpenAiWire_ToolCallIndex_StillReachesThePositionalId()
    {
        var map = new Dictionary<int, string>();
        _ = CompatOpenAiWire.ParseChatChunk(
            Utf8("""{"choices":[{"delta":{"tool_calls":[{"index":4,"function":{"name":"ls","arguments":"{}"}}]}}]}"""),
            map);

        await Assert.That(map.ContainsKey(4)).IsTrue();
        await Assert.That(map[4]).IsEqualTo("tc4");
    }

    [Test]
    public async Task OpenAiWire_EscapedName_StillDispatches()
    {
        // "cho\u0069ces" is "choices" once decoded. The raw bytes are not
        // the name's text, so this is the path that has to ask the reader.
        var events = CompatOpenAiWire.ParseChatChunk(
            Utf8("""{"cho\u0069ces":[{"delta":{"content":"x"}}]}"""),
            new Dictionary<int, string>());

        await Assert.That(events.OfType<TextDeltaEvent>().Single().Delta).IsEqualTo("x");
    }

    [Test]
    public async Task OpenAiWire_UnknownAndNonAsciiNames_AreSkippedNotFatal()
    {
        var events = CompatOpenAiWire.ParseChatChunk(
            Utf8("""{"nope":1,"n\u00f6pe":2,"choices":[{"delta":{"content":"x"}}]}"""),
            new Dictionary<int, string>());

        await Assert.That(events.OfType<TextDeltaEvent>().Single().Delta).IsEqualTo("x");
    }

    // ── Anthropic ─────────────────────────────────────────────────────

    /// <summary>
    ///     Every name <c>AnthropicEventMapper</c> dispatches on, one chunk
    ///     per name group, each with the observable it is supposed to
    ///     produce.
    /// </summary>
    [Test]
    public async Task Anthropic_EveryDispatchedName_StillDispatches()
    {
        // type + delta + text
        var text = AnthropicEventMapper.MapAnthropicEvents(
            Utf8("""{"type":"content_block_delta","delta":{"type":"text_delta","text":"x"}}"""));
        await Assert.That(text.OfType<TextDeltaEvent>().Single().Delta).IsEqualTo("x");

        // thinking
        var thinking = AnthropicEventMapper.MapAnthropicEvents(
            Utf8("""{"type":"content_block_delta","delta":{"type":"thinking_delta","thinking":"r"}}"""));
        await Assert.That(thinking.OfType<ThinkingDeltaEvent>().Single().Delta).IsEqualTo("r");

        // partial_json
        var partial = AnthropicEventMapper.MapAnthropicEvents(
            Utf8("""{"type":"content_block_delta","delta":{"type":"input_json_delta","partial_json":"{}"}}"""));
        await Assert.That(partial.OfType<ToolCallDeltaEvent>().Single().ArgsDelta).IsEqualTo("{}");

        // content_block + id + name
        var block = AnthropicEventMapper.MapAnthropicEvents(
            Utf8("""{"type":"content_block_start","content_block":{"type":"tool_use","id":"toolu_1","name":"read"}}"""));
        var started = block.OfType<ToolCallStartEvent>().Single();
        await Assert.That(started.Id).IsEqualTo("toolu_1");
        await Assert.That(started.ToolName).IsEqualTo("read");

        // delta + stop_reason + usage + input_tokens + output_tokens
        var done = AnthropicEventMapper.MapAnthropicEvents(
            Utf8("""{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"input_tokens":5,"output_tokens":6}}"""));
        var finish = done.OfType<StepFinishEvent>().Single();
        await Assert.That(finish.FinishReason).IsEqualTo("end_turn");
        await Assert.That(finish.Usage!.InputTokens).IsEqualTo(5);
        await Assert.That(finish.Usage.OutputTokens).IsEqualTo(6);
    }

    /// <summary>
    ///     The block index, read on its own: it is the key the tool-args
    ///     delta is correlated by, so a dropped "index" would point the
    ///     delta at the wrong block.
    /// </summary>
    [Test]
    public async Task Anthropic_BlockIndex_StillReachesTheBlockMap()
    {
        var state = new AnthropicChunkState();
        _ = AnthropicEventMapper.MapAnthropicEvents(
            Utf8("""{"type":"content_block_start","index":7,"content_block":{"type":"tool_use","id":"toolu_1","name":"read"}}"""),
            state);

        await Assert.That(state.IndexToId.ContainsKey(7)).IsTrue();
        await Assert.That(state.IndexToId[7]).IsEqualTo("toolu_1");
    }

    [Test]
    public async Task Anthropic_EscapedName_StillDispatches()
    {
        var events = AnthropicEventMapper.MapAnthropicEvents(
            Utf8("""{"ty\u0070e":"content_block_delta","delta":{"type":"text_delta","text":"x"}}"""));

        await Assert.That(events.OfType<TextDeltaEvent>().Single().Delta).IsEqualTo("x");
    }

    [Test]
    public async Task Anthropic_UnknownAndNonAsciiNames_AreSkippedNotFatal()
    {
        var events = AnthropicEventMapper.MapAnthropicEvents(
            Utf8("""{"nope":1,"n\u00f6pe":2,"type":"content_block_delta","delta":{"type":"text_delta","text":"x"}}"""));

        await Assert.That(events.OfType<TextDeltaEvent>().Single().Delta).IsEqualTo("x");
    }

    // ── OpenAI Responses API ──────────────────────────────────────────

    /// <summary>
    ///     Every name <c>OpenAiResponsesMapper</c> dispatches on, one chunk
    ///     per name group, each with the observable it is supposed to
    ///     produce.
    /// </summary>
    [Test]
    public async Task Responses_EveryDispatchedName_StillDispatches()
    {
        // type + delta
        var text = OpenAiResponsesMapper.MapResponsesChunk(
            Utf8("""{"type":"response.output_text.delta","delta":"x"}"""));
        await Assert.That(text.OfType<TextDeltaEvent>().Single().Delta).IsEqualTo("x");

        // type (reasoning)
        var thinking = OpenAiResponsesMapper.MapResponsesChunk(
            Utf8("""{"type":"response.reasoning.delta","delta":"r"}"""));
        await Assert.That(thinking.OfType<ThinkingDeltaEvent>().Single().Delta).IsEqualTo("r");

        // item + call_id + name
        var added = OpenAiResponsesMapper.MapResponsesChunk(
            Utf8("""{"type":"response.output_item.added","item":{"type":"function_call","call_id":"call_1","name":"read"}}"""));
        var started = added.OfType<ToolCallStartEvent>().Single();
        await Assert.That(started.Id).IsEqualTo("call_1");
        await Assert.That(started.ToolName).IsEqualTo("read");

        // output_index — the id the args delta is keyed by
        var args = OpenAiResponsesMapper.MapResponsesChunk(
            Utf8("""{"type":"response.function_call_arguments.delta","output_index":2,"delta":"{}"}"""));
        await Assert.That(args.OfType<ToolCallDeltaEvent>().Single().Id).IsEqualTo("2");

        // response + usage + input_tokens + output_tokens + output_tokens_details + reasoning_tokens
        var done = OpenAiResponsesMapper.MapResponsesChunk(
            Utf8("""{"type":"response.completed","response":{"usage":{"input_tokens":5,"output_tokens":6,"output_tokens_details":{"reasoning_tokens":1}}}}"""));
        var finish = done.OfType<StepFinishEvent>().Single();
        await Assert.That(finish.Usage!.InputTokens).IsEqualTo(5);
        await Assert.That(finish.Usage.OutputTokens).IsEqualTo(6);
        await Assert.That(finish.Usage.ReasoningTokens).IsEqualTo(1);
    }

    [Test]
    public async Task Responses_EscapedName_StillDispatches()
    {
        var events = OpenAiResponsesMapper.MapResponsesChunk(
            Utf8("""{"ty\u0070e":"response.output_text.delta","delta":"x"}"""));

        await Assert.That(events.OfType<TextDeltaEvent>().Single().Delta).IsEqualTo("x");
    }

    [Test]
    public async Task Responses_UnknownAndNonAsciiNames_AreSkippedNotFatal()
    {
        var events = OpenAiResponsesMapper.MapResponsesChunk(
            Utf8("""{"nope":1,"n\u00f6pe":2,"type":"response.output_text.delta","delta":"x"}"""));

        await Assert.That(events.OfType<TextDeltaEvent>().Single().Delta).IsEqualTo("x");
    }

    // ── Ollama ────────────────────────────────────────────────────────

    /// <summary>
    ///     Every name the Ollama mapper dispatches on, one chunk per name
    ///     group, each with the observable it is supposed to produce.
    /// </summary>
    [Test]
    public async Task Ollama_EveryDispatchedName_StillDispatches()
    {
        // message + content
        var text = OllamaLlmClient.MapNdjsonChunk(
            Utf8("""{"message":{"content":"x"}}"""),
            new Dictionary<int, string>());
        await Assert.That(text.OfType<TextDeltaEvent>().Single().Delta).IsEqualTo("x");

        // tool_calls + index + id + function + name + arguments
        var tool = OllamaLlmClient.MapNdjsonChunk(
            Utf8("""{"message":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"read","arguments":"{}"}}]}}"""),
            new Dictionary<int, string>());
        await Assert.That(tool.OfType<ToolCallStartEvent>().Single().ToolName).IsEqualTo("read");
        await Assert.That(tool.OfType<ToolCallDeltaEvent>().Single().ArgsDelta).IsEqualTo("{}");

        // done + prompt_eval_count + eval_count
        var done = OllamaLlmClient.MapNdjsonChunk(
            Utf8("""{"done":true,"prompt_eval_count":3,"eval_count":9}"""),
            new Dictionary<int, string>());
        var finish = done.OfType<StepFinishEvent>().Single();
        await Assert.That(finish.Usage!.InputTokens).IsEqualTo(3);
        await Assert.That(finish.Usage.OutputTokens).IsEqualTo(9);
    }

    /// <summary>
    ///     The tool-call index, read on its own: the positional id fallback
    ///     is <c>tc{index}</c>, so a dropped "index" would rename the call.
    /// </summary>
    [Test]
    public async Task Ollama_ToolCallIndex_StillReachesThePositionalId()
    {
        var map = new Dictionary<int, string>();
        _ = OllamaLlmClient.MapNdjsonChunk(
            Utf8("""{"message":{"tool_calls":[{"index":4,"function":{"name":"ls","arguments":"{}"}}]}}"""),
            map);

        await Assert.That(map.ContainsKey(4)).IsTrue();
        await Assert.That(map[4]).IsEqualTo("tc4");
    }

    [Test]
    public async Task Ollama_EscapedName_StillDispatches()
    {
        var events = OllamaLlmClient.MapNdjsonChunk(
            Utf8("""{"mess\u0061ge":{"content":"x"}}"""),
            new Dictionary<int, string>());

        await Assert.That(events.OfType<TextDeltaEvent>().Single().Delta).IsEqualTo("x");
    }

    [Test]
    public async Task Ollama_UnknownAndNonAsciiNames_AreSkippedNotFatal()
    {
        var events = OllamaLlmClient.MapNdjsonChunk(
            Utf8("""{"nope":1,"n\u00f6pe":2,"message":{"content":"x"}}"""),
            new Dictionary<int, string>());

        await Assert.That(events.OfType<TextDeltaEvent>().Single().Delta).IsEqualTo("x");
    }
}
