extern alias AnthropicWire;

using System.Text;
using System.Threading.Channels;
using Harbor.Abstractions.Events;
using Harbor.Providers.Anthropic;
using Harbor.TestKit;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using AnthropicChunkState = AnthropicWire::Harbor.Providers.Internal.ChunkStreamState;
using AnthropicDecoder = AnthropicWire::Harbor.Providers.Internal.ProviderChunkDecoder;
using AnthropicLogMalformed = AnthropicWire::Harbor.Providers.Internal.LogMalformed;
using AnthropicParseUtf8Chunk = AnthropicWire::Harbor.Providers.Internal.ParseUtf8Chunk;

namespace Harbor.Providers.Tests;

/// <summary>
///     #3748: behavior coverage for the shared wire-chunk decoder on real
///     Anthropic frames. The consumer (<see cref="AnthropicEventMapper.WriteAnthropicEventsAsync" />)
///     now only hands its parser and log delegate to the decoder, so these tests
///     pin the observable contract that moved: event order and payloads, the
///     malformed-chunk skip-and-count, the once-per-stream id-fallback warning,
///     and UTF-8 transcode of non-ASCII deltas.
/// </summary>
public class ProviderChunkDecoderWireTests
{
    /// <summary>Drive the real consumer frame by frame, then drain the channel in order.</summary>
    private static async Task<List<LlmEvent>> DecodeFramesAsync(
        string[] frames, AnthropicChunkState state, ILogger logger)
    {
        var channel = Channel.CreateUnbounded<LlmEvent>();
        foreach (string frame in frames)
        {
            await AnthropicEventMapper.WriteAnthropicEventsAsync(
                frame, channel.Writer, state, logger, CancellationToken.None);
        }

        channel.Writer.TryComplete();
        var events = new List<LlmEvent>();
        await foreach (LlmEvent evt in channel.Reader.ReadAllAsync())
        {
            events.Add(evt);
        }

        return events;
    }

    [Test]
    public async Task AnthropicWireFrames_EmitInOrder_SkipAndCountMalformed()
    {
        var state = new AnthropicChunkState();
        var logger = new CapturingLogger();

        List<LlmEvent> events = await DecodeFramesAsync(
        [
            """{"type":"message_start","message":{"id":"msg_1"}}""",
            """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_1","name":"read"}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"p\":"}}""",
            "{not json",
            """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"\"x\"}"}}""",
            """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"input_tokens":5,"output_tokens":10}}""",
            """{"type":"message_stop"}""",
        ], state, logger);

        await Assert.That(events.Count).IsEqualTo(5);
        await Assert.That(events[0]).IsTypeOf<StepStartEvent>();
        await Assert.That(((StepStartEvent)events[0]).Index).IsEqualTo(0);

        var start = (ToolCallStartEvent)events[1];
        await Assert.That(start.Id).IsEqualTo("toolu_1");
        await Assert.That(start.ToolName).IsEqualTo("read");

        var firstDelta = (ToolCallDeltaEvent)events[2];
        await Assert.That(firstDelta.Id).IsEqualTo("toolu_1");
        await Assert.That(firstDelta.ArgsDelta).IsEqualTo("{\"p\":");

        var secondDelta = (ToolCallDeltaEvent)events[3];
        await Assert.That(secondDelta.Id).IsEqualTo("toolu_1");
        await Assert.That(secondDelta.ArgsDelta).IsEqualTo("\"x\"}");

        var finish = (StepFinishEvent)events[4];
        await Assert.That(finish.FinishReason).IsEqualTo("tool_use");
        await Assert.That(finish.Usage!.InputTokens).IsEqualTo(5);
        await Assert.That(finish.Usage.OutputTokens).IsEqualTo(10);

        await Assert.That(state.MalformedChunks).IsEqualTo(1);
        await Assert.That(state.RemappedToolCalls).IsEqualTo(0);
        await Assert.That(logger.Warnings.Count).IsEqualTo(1);
        await Assert.That(logger.Warnings[0]).Contains("Failed to parse Anthropic event #1");
    }

    [Test]
    public async Task ToolDeltaWithoutStart_RemapsOncePerStream_AndWarnsOnce()
    {
        var state = new AnthropicChunkState();
        var logger = new CapturingLogger();

        List<LlmEvent> events = await DecodeFramesAsync(
        [
            """{"type":"content_block_delta","index":7,"delta":{"type":"input_json_delta","partial_json":"{}"}}""",
            """{"type":"content_block_delta","index":7,"delta":{"type":"input_json_delta","partial_json":"[]"}}""",
        ], state, logger);

        var deltas = events.OfType<ToolCallDeltaEvent>().ToList();
        await Assert.That(deltas.Count).IsEqualTo(2);
        await Assert.That(deltas.Select(d => d.Id).Distinct().ToList()).IsEquivalentTo(["tc7"]);
        await Assert.That(state.RemappedToolCalls).IsEqualTo(2);
        await Assert.That(state.MalformedChunks).IsEqualTo(0);
        await Assert.That(logger.Warnings.Count(w => w.Contains("fell back to positional ids"))).IsEqualTo(1);
    }

    [Test]
    public async Task NonAsciiWireFrame_TranscodesThroughThePooledBuffer()
    {
        var state = new AnthropicChunkState();
        var logger = new CapturingLogger();

        List<LlmEvent> events = await DecodeFramesAsync(
        [
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"привет → ✓"}}""",
        ], state, logger);

        var delta = events.OfType<TextDeltaEvent>().Single();
        await Assert.That(delta.Id).IsEqualTo("0");
        await Assert.That(delta.Delta).IsEqualTo("привет → ✓");
        await Assert.That(state.MalformedChunks).IsEqualTo(0);
    }

    [Test]
    public async Task Decoder_UsesTheSuppliedParserAndLogDelegate()
    {
        // The primitive itself, not the Anthropic wrapper: a caller-supplied
        // span parser and log delegate must be the ones invoked.
        var state = new AnthropicChunkState();
        var logger = new CapturingLogger();
        var channel = Channel.CreateUnbounded<LlmEvent>();
        int parsedBytes = -1;
        int logged = 0;

        AnthropicParseUtf8Chunk parse = (ReadOnlySpan<byte> utf8, ChunkStreamState? s) =>
        {
            if (utf8.Length == 0 || utf8[0] != (byte)'{')
                throw new FormatException("not a wire chunk");
            parsedBytes = utf8.Length;
            return [new TextDeltaEvent("span", Encoding.UTF8.GetString(utf8))];
        };
        AnthropicLogMalformed log = (ILogger l, Exception ex, int count, string data) =>
        {
            logged = count;
            l.LogWarning("custom: {Data}", data);
        };

        await AnthropicDecoder.DecodeAsync(
            """{"a":"b"}""", channel.Writer, state, logger, CancellationToken.None, parse, log);
        await AnthropicDecoder.DecodeAsync(
            "not json at all", channel.Writer, state, logger, CancellationToken.None, parse, log);

        channel.Writer.TryComplete();
        var events = new List<LlmEvent>();
        await foreach (LlmEvent evt in channel.Reader.ReadAllAsync())
        {
            events.Add(evt);
        }

        await Assert.That(parsedBytes).IsEqualTo(9);
        await Assert.That(events.OfType<TextDeltaEvent>().Select(e => e.Delta).ToList())
            .IsEquivalentTo(["{\"a\":\"b\"}"]);
        await Assert.That(state.MalformedChunks).IsEqualTo(1);
        await Assert.That(logged).IsEqualTo(1);
    }
}
