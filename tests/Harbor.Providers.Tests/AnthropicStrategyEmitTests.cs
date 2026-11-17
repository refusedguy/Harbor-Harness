extern alias AnthropicWire;

using System.Text;
using Harbor.Abstractions.Events;
using Harbor.Providers.Anthropic;
using TUnit.Assertions;
using AnthropicChunkState = AnthropicWire::Harbor.Providers.Internal.ChunkStreamState;

namespace Harbor.Providers.Tests;

/// <summary>
///     #1489: the emit switch and the nested block/delta chains became a
///     Strategy registry. Every wire kind keeps its old event shape, and an
///     unregistered kind still emits nothing.
/// </summary>
public class AnthropicStrategyEmitTests
{
    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    [Test]
    public async Task Anthropic_Strategy_MessageStart_EmitsStepStart()
    {
        var events = AnthropicEventMapper.MapAnthropicEvents(
            Utf8("""{"type":"message_start","message":{"id":"msg_1"}}"""));

        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events.OfType<StepStartEvent>().Single().Index).IsEqualTo(0);
    }

    [Test]
    public async Task Anthropic_Strategy_ContentBlockStart_AllSubtypes()
    {
        var text = AnthropicEventMapper.MapAnthropicEvents(
            Utf8("""{"type":"content_block_start","content_block":{"type":"text","id":"b1"}}"""));
        var thinking = AnthropicEventMapper.MapAnthropicEvents(
            Utf8("""{"type":"content_block_start","content_block":{"type":"thinking","id":"b2"}}"""));
        var tool = AnthropicEventMapper.MapAnthropicEvents(
            Utf8("""{"type":"content_block_start","content_block":{"type":"tool_use","id":"toolu_1","name":"read"}}"""));
        var unknown = AnthropicEventMapper.MapAnthropicEvents(
            Utf8("""{"type":"content_block_start","content_block":{"type":"redacted_thinking","id":"b3"}}"""));

        await Assert.That(text.OfType<TextStartEvent>().Single().Id).IsEqualTo("b1");
        await Assert.That(thinking.OfType<ThinkingStartEvent>().Single().Id).IsEqualTo("b2");
        await Assert.That(tool.OfType<ToolCallStartEvent>().Single().ToolName).IsEqualTo("read");
        await Assert.That(unknown.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Anthropic_Strategy_ContentBlockDelta_AllSubtypes()
    {
        var text = AnthropicEventMapper.MapAnthropicEvents(
            Utf8("""{"type":"content_block_delta","delta":{"type":"text_delta","text":"hi"}}"""));
        var thinking = AnthropicEventMapper.MapAnthropicEvents(
            Utf8("""{"type":"content_block_delta","delta":{"type":"thinking_delta","thinking":"r"}}"""));
        var tool = AnthropicEventMapper.MapAnthropicEvents(
            Utf8("""{"type":"content_block_delta","delta":{"type":"input_json_delta","partial_json":"{}"}}"""));
        var empty = AnthropicEventMapper.MapAnthropicEvents(
            Utf8("""{"type":"content_block_delta","delta":{"type":"text_delta","text":""}}"""));
        var unknown = AnthropicEventMapper.MapAnthropicEvents(
            Utf8("""{"type":"content_block_delta","delta":{"type":"signature_delta","signature":"x"}}"""));

        // Deltas keep the fixed "0" id (Anthropic omits ids on deltas).
        await Assert.That(text.OfType<TextDeltaEvent>().Single().Id).IsEqualTo("0");
        await Assert.That(text.OfType<TextDeltaEvent>().Single().Delta).IsEqualTo("hi");
        await Assert.That(thinking.OfType<ThinkingDeltaEvent>().Single().Delta).IsEqualTo("r");
        await Assert.That(tool.OfType<ToolCallDeltaEvent>().Single().ArgsDelta).IsEqualTo("{}");
        await Assert.That(empty.Count).IsEqualTo(0);
        await Assert.That(unknown.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Anthropic_Strategy_MessageDelta_AndNoopKinds()
    {
        var withUsage = AnthropicEventMapper.MapAnthropicEvents(
            Utf8("""{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"input_tokens":7,"output_tokens":3}}"""));
        var withoutUsage = AnthropicEventMapper.MapAnthropicEvents(
            Utf8("""{"type":"message_delta","delta":{"stop_reason":"tool_use"}}"""));
        var noStop = AnthropicEventMapper.MapAnthropicEvents(
            Utf8("""{"type":"message_delta","usage":{"input_tokens":7,"output_tokens":3}}"""));
        var stop = AnthropicEventMapper.MapAnthropicEvents(Utf8("""{"type":"message_stop"}"""));
        var unknown = AnthropicEventMapper.MapAnthropicEvents(Utf8("""{"type":"future_wire_kind"}"""));

        var finish = withUsage.OfType<StepFinishEvent>().Single();
        await Assert.That(finish.FinishReason).IsEqualTo("end_turn");
        await Assert.That(finish.Usage!.InputTokens).IsEqualTo(7);
        await Assert.That(finish.Usage.OutputTokens).IsEqualTo(3);
        await Assert.That(withoutUsage.OfType<StepFinishEvent>().Single().Usage).IsNull();
        await Assert.That(noStop.Count).IsEqualTo(0);
        await Assert.That(stop.Count).IsEqualTo(0);
        await Assert.That(unknown.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Anthropic_Strategy_ToolUseStart_StillRecordsBlockIndex()
    {
        var state = new AnthropicChunkState();
        _ = AnthropicEventMapper.MapAnthropicEvents(
            Utf8("""{"type":"content_block_start","index":7,"content_block":{"type":"tool_use","id":"toolu_1","name":"read"}}"""),
            state);

        await Assert.That(state.IndexToId[7]).IsEqualTo("toolu_1");
    }
}
