using System.Net;
using System.Text;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Providers.OpenAI;
using Harbor.Providers.Ollama;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Providers.Tests;

/// <summary>
///     ROP-A ПР.1/ПР.2/ПР.3 — end-to-end stream tests over a stubbed HTTP
///     transport: exactly one FinishEvent per stream, and stable tool-call ids
///     when the server omits them on delta chunks.
/// </summary>
public class ProviderStreamTests
{
    private static HttpResponseMessage Sse(params string[] dataLines)
    {
        var body = new StringBuilder();
        foreach (string line in dataLines)
        {
            body.Append("data: ").Append(line).Append("\n\n");
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body.ToString(), Encoding.UTF8, "text/event-stream")
        };
    }

    private static HttpResponseMessage SseRaw(params string[] rawLines)
    {
        // Lines are written verbatim (no "data: " prefix injection) so tests
        // can exercise spec-legal SSE variants: `data:{...}` with no space,
        // padded sentinels, comment lines.
        var body = new StringBuilder();
        foreach (string line in rawLines)
        {
            body.Append(line).Append('\n');
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body.ToString(), Encoding.UTF8, "text/event-stream")
        };
    }

    private static OpenAiCompatible.OpenAiCompatibleLlmClient CreateCompatClient(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            new OpenAiCompatible.ProviderConfig { Id = "stub", BaseUrl = "http://stub" },
            StubAuthResolver.Instance,
            StubModelCatalog.Instance,
            NullLogger<OpenAiCompatible.OpenAiCompatibleLlmClient>.Instance);

    private static async Task<List<LlmEvent>> CollectAsync(IAsyncEnumerable<LlmEvent> stream)
    {
        var events = new List<LlmEvent>();
        await foreach (var evt in stream)
        {
            events.Add(evt);
        }
        return events;
    }

    [Test]
    public async Task OpenAIChat_DeltaChunksWithoutIds_KeepStableToolCallId()
    {
        // Server sends the tool-call id only on the first chunk; delta chunks
        // omit it. Before ПР.3 every chunk got a fresh Guid → args lost.
        var handler = new StubHttpHandler(_ => Sse(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_X","function":{"name":"read","arguments":"{\"pa"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":\"a"}}]}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":7,"completion_tokens":3}}""",
            "[DONE]"));

        var client = new OpenAILlmClient(
            new HttpClient(handler),
            new OpenAIConfig(),
            StubAuthResolver.Instance,
            NullLogger<OpenAILlmClient>.Instance);

        var events = await CollectAsync(client.StreamAsync(new LlmRequest(
            "gpt-4o", [LlmUserMessage.Text("hello")], "", [])));

        var starts = events.OfType<ToolCallStartEvent>().ToList();
        var deltas = events.OfType<ToolCallDeltaEvent>().ToList();

        await Assert.That(starts.Count).IsEqualTo(1);
        await Assert.That(starts[0].Id).IsEqualTo("call_X");
        await Assert.That(deltas.Select(d => d.Id).Distinct().ToList()).IsEquivalentTo(["call_X"]);
        // Exactly one FinishEvent even though [DONE] arrived mid-stream.
        await Assert.That(events.Count(e => e is FinishEvent)).IsEqualTo(1);
    }

    [Test]
    public async Task OpenAIChat_IdMissingEntirely_FallsBackToPositionalId()
    {
        var handler = new StubHttpHandler(_ => Sse(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"ls","arguments":"{}"}}]}}]}""",
            "[DONE]"));

        var client = new OpenAILlmClient(
            new HttpClient(handler),
            new OpenAIConfig(),
            StubAuthResolver.Instance,
            NullLogger<OpenAILlmClient>.Instance);

        var events = await CollectAsync(client.StreamAsync(new LlmRequest(
            "gpt-4o", [LlmUserMessage.Text("hello")], "", [])));

        var starts = events.OfType<ToolCallStartEvent>().ToList();
        await Assert.That(starts.Count).IsEqualTo(1);
        await Assert.That(starts[0].Id).IsEqualTo("tc0");
    }

    [Test]
    public async Task Ollama_NoFinishSentinel_EmitsSingleFinishAtEof()
    {
        HttpResponseMessage responder(HttpRequestMessage _)
        {
            string ndjson =
                """{"message":{"content":"hi"},"done":false}""" + "\n" +
                """{"message":{"content":"!"},"done":false}""" + "\n" +
                """{"done":true,"prompt_eval_count":5,"eval_count":9}""" + "\n";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson")
            };
        }

        var client = new OllamaLlmClient(
            new HttpClient(new StubHttpHandler(responder)),
            new OllamaConfig(),
            NullLogger<OllamaLlmClient>.Instance);

        var events = await CollectAsync(client.StreamAsync(new LlmRequest(
            "llama3", [LlmUserMessage.Text("hello")], "", [])));

        await Assert.That(events.Count(e => e is FinishEvent)).IsEqualTo(1);
        await Assert.That(events.Count(e => e is StepFinishEvent)).IsEqualTo(1);
    }

    [Test]
    public async Task MalformedChunk_IsSkipped_StreamSurvives()
    {
        // ROP-A ПР.4: one unparseable line must NOT terminate the stream
        // (previously the compat adapter emitted a terminal ErrorEvent here).
        var handler = new StubHttpHandler(_ => Sse(
            """{"choices":[{"delta":{"content":"before"}}]}""",
            "{NOT VALID JSON",
            """{"choices":[{"delta":{"content":"after"}}],"finish_reason":"stop"}""",
            "[DONE]"));

        var config = new OpenAiCompatible.ProviderConfig { Id = "stub", BaseUrl = "http://stub" };
        var client = new OpenAiCompatible.OpenAiCompatibleLlmClient(
            new HttpClient(handler),
            config,
            StubAuthResolver.Instance,
            StubModelCatalog.Instance,
            NullLogger<OpenAiCompatible.OpenAiCompatibleLlmClient>.Instance);

        var events = await CollectAsync(client.StreamAsync(new LlmRequest(
            "m1", [LlmUserMessage.Text("hello")], "", [])));

        string text = string.Concat(events.OfType<TextDeltaEvent>().Select(t => t.Delta));
        await Assert.That(text).IsEqualTo("beforeafter");
        await Assert.That(events.Count(e => e is ErrorEvent)).IsEqualTo(0);
        await Assert.That(events.Count(e => e is FinishEvent)).IsEqualTo(1);
    }

    [Test]
    public async Task NoSpaceDataPrefix_ToolDeltasSurvive()
    {
        // Issue #86: SSE allows `data:{...}` with no trailing space; the old
        // `StartsWith("data: ")` filter dropped such lines silently (lost
        // tool-call deltas). The padded `[DONE]` here also exercises the
        // sentinel trim.
        var handler = new StubHttpHandler(_ => SseRaw(
            """data:{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_Y","function":{"name":"read","arguments":"{\"pa"}}]}}]}""",
            """data:{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\"}"}}]}}]}""",
            """data:{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""",
            "data:[DONE]"));

        var client = CreateCompatClient(handler);

        var events = await CollectAsync(client.StreamAsync(new LlmRequest(
            "m1", [LlmUserMessage.Text("hello")], "", [])));

        var starts = events.OfType<ToolCallStartEvent>().ToList();
        var deltas = events.OfType<ToolCallDeltaEvent>().ToList();

        await Assert.That(starts.Count).IsEqualTo(1);
        await Assert.That(starts[0].Id).IsEqualTo("call_Y");
        await Assert.That(deltas.Select(d => d.Id).Distinct().ToList()).IsEquivalentTo(["call_Y"]);
        await Assert.That(events.Count(e => e is ErrorEvent)).IsEqualTo(0);
        await Assert.That(events.Count(e => e is FinishEvent)).IsEqualTo(1);
    }

    [Test]
    public async Task PaddedDoneSentinel_TerminatesStream_LinesAfterAreIgnored()
    {
        // Issue #86: `data: [DONE]   ` (trailing whitespace) must still end
        // the stream. The trailing "after" line is the discriminator: if the
        // sentinel is missed it gets parsed and the text becomes "beforeafter".
        var handler = new StubHttpHandler(_ => SseRaw(
            """data: {"choices":[{"delta":{"content":"before"}}]}""",
            "data: [DONE]   ",
            """data: {"choices":[{"delta":{"content":"after"}}]}"""));

        var client = CreateCompatClient(handler);

        var events = await CollectAsync(client.StreamAsync(new LlmRequest(
            "m1", [LlmUserMessage.Text("hello")], "", [])));

        string text = string.Concat(events.OfType<TextDeltaEvent>().Select(t => t.Delta));
        await Assert.That(text).IsEqualTo("before");
        await Assert.That(events.Count(e => e is FinishEvent)).IsEqualTo(1);
    }

    [Test]
    public async Task NonIntegerUsage_DoesNotKillChunk()
    {
        // Issue #86: ReadUsage used GetInt32() which throws on float/string
        // wire values, discarding the whole chunk (incl. finish_reason).
        // Floats truncate, numeric strings parse, garbage falls back to 0.
        var handler = new StubHttpHandler(_ => Sse(
            """{"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":7.5,"completion_tokens":"3"}}""",
            "[DONE]"));

        var client = CreateCompatClient(handler);

        var events = await CollectAsync(client.StreamAsync(new LlmRequest(
            "m1", [LlmUserMessage.Text("hello")], "", [])));

        var finishes = events.OfType<StepFinishEvent>().ToList();
        await Assert.That(finishes.Count).IsEqualTo(1);
        await Assert.That(finishes[0].Usage).IsNotNull();
        await Assert.That(finishes[0].Usage!.InputTokens).IsEqualTo(7);
        await Assert.That(finishes[0].Usage!.OutputTokens).IsEqualTo(3);
        await Assert.That(events.Count(e => e is ErrorEvent)).IsEqualTo(0);
        await Assert.That(events.Count(e => e is FinishEvent)).IsEqualTo(1);
    }

    [Test]
    public async Task NonStringToolCallId_FallsBackToPositionalId()
    {
        // Issue #86 (adjacent): a numeric wire `id` made GetString() throw,
        // discarding the tool-call chunk. It must fall back to `tc{index}`.
        var handler = new StubHttpHandler(_ => Sse(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":123,"function":{"name":"ls","arguments":"{}"}}]}}]}""",
            "[DONE]"));

        var client = CreateCompatClient(handler);

        var events = await CollectAsync(client.StreamAsync(new LlmRequest(
            "m1", [LlmUserMessage.Text("hello")], "", [])));

        var starts = events.OfType<ToolCallStartEvent>().ToList();
        await Assert.That(starts.Count).IsEqualTo(1);
        await Assert.That(starts[0].Id).IsEqualTo("tc0");
        await Assert.That(events.Count(e => e is ErrorEvent)).IsEqualTo(0);
    }
}
