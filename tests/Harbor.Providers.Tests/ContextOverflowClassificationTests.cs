using System.Net;
using System.Text;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Providers;
using Harbor.Providers.OpenAI;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Providers.Tests;

/// <summary>
///     #1755 — the context-window overflow has to be RECOGNIZED, or the agent
///     loop cannot answer it with a compaction and a retry.
/// </summary>
/// <remarks>
///     <para>
///         The refusal is not a status code: every provider answers it with
///         its own body, so the body is the only place the refusal names
///         itself. These tests pin the three shapes Harbor talks to — OpenAI's
///         <c>context_length_exceeded</c> code, Anthropic's "prompt is too
///         long" prose and Ollama's "the input length exceeds the context
///         window" — plus the wiring that carries the classification from the
///         HTTP response onto the stream's error event.
///     </para>
/// </remarks>
public class ContextOverflowClassificationTests
{
    [Test]
    public async Task ClassifyResponse_RecognisesTheProviderOverflowShapes()
    {
        // OpenAI and the gateways that copy its error shape.
        await Assert.That(ProviderErrors.ClassifyResponse(
                HttpStatusCode.BadRequest,
                """{"error":{"message":"This model's maximum context length is 8192 tokens.","type":"invalid_request_error","code":"context_length_exceeded"}}"""))
            .IsEqualTo(ProviderErrorKind.ContextOverflow);

        await Assert.That(ProviderErrors.ClassifyResponse(
                HttpStatusCode.BadRequest,
                """{"error":{"code":"maximum_context_length_exceeded"}}"""))
            .IsEqualTo(ProviderErrorKind.ContextOverflow);

        // Anthropic: 400 with prose, no code field.
        await Assert.That(ProviderErrors.ClassifyResponse(
                HttpStatusCode.BadRequest,
                """{"type":"error","error":{"type":"invalid_request_error","message":"prompt is too long: 250000 tokens > 200000 maximum"}}"""))
            .IsEqualTo(ProviderErrorKind.ContextOverflow);

        // Ollama / llama.cpp: 400 with its own wording.
        await Assert.That(ProviderErrors.ClassifyResponse(
                HttpStatusCode.BadRequest,
                """{"error":"the input length exceeds the context window"}"""))
            .IsEqualTo(ProviderErrorKind.ContextOverflow);

        // Some gateways answer 413 instead of 400 — the body still names it.
        await Assert.That(ProviderErrors.ClassifyResponse(
                HttpStatusCode.RequestEntityTooLarge,
                """{"error":{"code":"input_too_long"}}"""))
            .IsEqualTo(ProviderErrorKind.ContextOverflow);
    }

    [Test]
    public async Task ClassifyResponse_FallsBackToTheStatusClassification()
    {
        // A rate limit is a rate limit, whatever the body says.
        await Assert.That(ProviderErrors.ClassifyResponse(
                HttpStatusCode.TooManyRequests,
                """{"error":{"message":"rate limit reached","code":"rate_limit_exceeded"}}"""))
            .IsEqualTo(ProviderErrorKind.RateLimit);

        // A bad request that is NOT an overflow stays unclassified — the
        // recovery path must not fire for it.
        await Assert.That(ProviderErrors.ClassifyResponse(
                HttpStatusCode.BadRequest,
                """{"error":{"message":"unknown parameter 'temperature'","code":"invalid_request"}}"""))
            .IsEqualTo(ProviderErrorKind.Unknown);

        await Assert.That(ProviderErrors.ClassifyResponse(HttpStatusCode.BadRequest, null))
            .IsEqualTo(ProviderErrorKind.Unknown);
        await Assert.That(ProviderErrors.ClassifyResponse(HttpStatusCode.BadRequest, string.Empty))
            .IsEqualTo(ProviderErrorKind.Unknown);
    }

    [Test]
    public async Task IsContextOverflow_VerdictMatchesTheClassification()
    {
        await Assert.That(ProviderErrors.IsContextOverflow(ProviderErrorKind.ContextOverflow)).IsTrue();
        await Assert.That(ProviderErrors.IsContextOverflow(ProviderErrorKind.Unknown)).IsFalse();
        await Assert.That(ProviderErrors.IsContextOverflow(ProviderErrorKind.RateLimit)).IsFalse();

        // The overflow is NOT transient: the same request fails the same way,
        // so the transport retry policy must not spend its budget on it.
        await Assert.That(ProviderErrors.IsTransient(ProviderErrorKind.ContextOverflow)).IsFalse();
    }

    [Test]
    public async Task OverflowBody_DoesNotMatchAnUnrelatedRefusal()
    {
        // An output-token refusal mentions tokens and limits, but it is not a
        // context overflow — the harness must not compact a history over it.
        await Assert.That(ProviderErrors.IsContextOverflowBody(
                """{"error":{"message":"max_tokens is too large: 4096"}}""")).IsFalse();
        await Assert.That(ProviderErrors.IsContextOverflowBody(
                """{"error":{"message":"invalid api key"}}""")).IsFalse();
        await Assert.That(ProviderErrors.IsContextOverflowBody(null)).IsFalse();
    }

    [Test]
    public async Task ProviderStream_OverflowBody_ReachesTheErrorEventAsContextOverflow()
    {
        // The wiring: the HTTP body is what classifies the refusal, and the
        // classification has to arrive on the stream's error event for the
        // agent loop to see it.
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"error":{"message":"This model's maximum context length is 8192 tokens.","code":"context_length_exceeded"}}""",
                Encoding.UTF8,
                "application/json")
        });

        var client = new OpenAILlmClient(
            new HttpClient(handler),
            new OpenAIConfig(),
            new TestAuthResolver(),
            NullLogger<OpenAILlmClient>.Instance);

        var events = await StreamCollector.CollectAsync(
            client.StreamAsync(new LlmRequest("gpt-4o", [LlmUserMessage.Text("hello")], "", [])));

        var error = events.OfType<ErrorEvent>().Single();
        await Assert.That(error.Kind).IsEqualTo(ProviderErrorKind.ContextOverflow);
        await Assert.That(error.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task ProviderStream_NonOverflowBody_KeepsTheStatusClassification()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(
                """{"error":{"message":"rate limit reached"}}""",
                Encoding.UTF8,
                "application/json")
        });

        var client = new OpenAILlmClient(
            new HttpClient(handler),
            new OpenAIConfig(),
            new TestAuthResolver(),
            NullLogger<OpenAILlmClient>.Instance);

        var events = await StreamCollector.CollectAsync(
            client.StreamAsync(new LlmRequest("gpt-4o", [LlmUserMessage.Text("hello")], "", [])));

        var error = events.OfType<ErrorEvent>().Single();
        await Assert.That(error.Kind).IsEqualTo(ProviderErrorKind.RateLimit);
    }
}
