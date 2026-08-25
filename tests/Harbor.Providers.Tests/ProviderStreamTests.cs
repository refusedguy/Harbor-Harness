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
            StubOpenAIAuthResolver.Instance,
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
            StubOpenAIAuthResolver.Instance,
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
}
