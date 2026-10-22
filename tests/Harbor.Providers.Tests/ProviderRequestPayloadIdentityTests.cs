// ProviderRequestPayloadIdentityTests.cs — #97: the request body did not
// change while the buffer that carries it did.
//
// WHAT #97 CHANGED
// ----------------
// The three request builders used to render their body into a FRESH
// `ArrayBufferWriter` per request. They now render it into a scratch buffer
// that is retained per thread at the high-water mark of the largest body that
// thread wrote, and copy the bytes out once.
//
// A retained buffer has exactly one way to be wrong: it can carry the PREVIOUS
// body's bytes into the next body. That is what these tests are about.
//
// THE ORACLE
// ----------
// The three assertions below are the whole contract:
//
//   1. The same request rendered twice yields BYTE-IDENTICAL bodies. A buffer
//      that was not reset, or that grew without being cleared, breaks this.
//   2. A small request rendered after a big one yields a body that parses as
//      JSON with nothing after it. `JsonDocument.Parse` throws on trailing
//      content, so a leaked tail is a red test, not a silent corruption.
//   3. The body's CONTENT is the request's content — the model, the messages,
//      the tool calls. The wire shape is the provider's contract, and a
//      provider that receives a different body is a different conversation.
//
// The bytes themselves cannot change for another reason: the writes are the
// same lines, in the same order, through the same `Utf8JsonWriter` with the
// same (default) encoder. Only the buffer's LIFETIME changed.

using System.Text;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Providers.Anthropic;
using Harbor.Providers.OpenAI;
using Harbor.Providers.OpenAiCompatible;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;

namespace Harbor.Providers.Tests;

/// <summary>
///     The three request builders' bodies, before and after the retained
///     buffer landed.
/// </summary>
public sealed class ProviderRequestPayloadIdentityTests
{
    private static readonly JsonDocument ArgsDoc = JsonDocument.Parse("""{"path":"a.txt"}""");

    /// <summary>A request with <paramref name="toolCalls" /> tool calls and a text block.</summary>
    private static LlmRequest Request(int toolCalls)
    {
        var blocks = new LlmContentBlock[toolCalls + 1];
        blocks[0] = new LlmTextBlock("hello");
        JsonElement args = ArgsDoc.RootElement;
        for (int i = 0; i < toolCalls; i++)
        {
            blocks[i + 1] = new LlmToolCallBlock($"tc_{i}", "read", args);
        }

        return new LlmRequest(
            Model: "kilocode/kilo-auto/free",
            Messages: [new LlmAssistantMessage(blocks)],
            SystemPrompt: "sys",
            Tools: []);
    }

    private static OpenAiCompatibleLlmClient CompatibleClient() =>
        new(
            new HttpClient(),
            new ProviderConfig { Id = "stub", BaseUrl = "http://stub" },
            new TestAuthResolver(),
            FakeModelCatalog.Instance,
            NullLogger<OpenAiCompatibleLlmClient>.Instance);

    private static async Task<byte[]> BodyOfAsync(HttpRequestMessage message) =>
        await message.Content!.ReadAsByteArrayAsync();

    // ── The OpenAI-compatible adapter (the provider most sessions use) ───────

    [Test]
    public async Task Compatible_RendersTheSameBytesTwice()
    {
        var client = CompatibleClient();
        LlmRequest request = Request(3);

        using HttpRequestMessage first = client.BuildRequest(request, "test-key");
        using HttpRequestMessage second = client.BuildRequest(request, "test-key");

        byte[] firstBytes = await BodyOfAsync(first);
        byte[] secondBytes = await BodyOfAsync(second);

        await Assert.That(secondBytes.Length).IsEqualTo(firstBytes.Length);
        await Assert.That(secondBytes).IsEquivalentTo(firstBytes);
    }

    [Test]
    public async Task Compatible_RendersASmallRequestAfterABigOne_WithNoTail()
    {
        var client = CompatibleClient();

        // The big request first: it grows the retained buffer to its high-water
        // mark, so the small request that follows is written into a buffer
        // that still holds the big one's bytes.
        using (HttpRequestMessage big = client.BuildRequest(Request(64), "test-key"))
        {
            await Assert.That((await BodyOfAsync(big)).Length).IsGreaterThan(0);
        }

        using HttpRequestMessage small = client.BuildRequest(Request(1), "test-key");
        byte[] smallBytes = await BodyOfAsync(small);

        // JsonDocument.Parse throws on trailing content: a leaked tail of the
        // previous body fails here rather than reaching the provider.
        using JsonDocument document = JsonDocument.Parse(smallBytes);
        JsonElement[] calls = [.. document.RootElement.GetProperty("messages")[1].GetProperty("tool_calls").EnumerateArray()];

        await Assert.That(calls.Length).IsEqualTo(1);
        await Assert.That(document.RootElement.GetProperty("model").GetString()).IsEqualTo("kilocode/kilo-auto/free");
    }

    // ── The native OpenAI builder ───────────────────────────────────────────

    [Test]
    public async Task OpenAi_RendersTheSameBytesTwice_AndNoTailAfterABigBody()
    {
        LlmRequest request = Request(3);

        using HttpRequestMessage first = OpenAiRequestBuilder.BuildChatCompletionsRequest(
            request, "http://stub", NullLogger.Instance);
        byte[] firstBytes = await BodyOfAsync(first);

        using (HttpRequestMessage big = OpenAiRequestBuilder.BuildChatCompletionsRequest(
                   Request(64), "http://stub", NullLogger.Instance))
        {
            await Assert.That((await BodyOfAsync(big)).Length).IsGreaterThan(firstBytes.Length);
        }

        using HttpRequestMessage again = OpenAiRequestBuilder.BuildChatCompletionsRequest(
            request, "http://stub", NullLogger.Instance);
        byte[] againBytes = await BodyOfAsync(again);

        await Assert.That(againBytes.Length).IsEqualTo(firstBytes.Length);
        await Assert.That(againBytes).IsEquivalentTo(firstBytes);

        using JsonDocument document = JsonDocument.Parse(againBytes);
        await Assert.That(document.RootElement.GetProperty("model").GetString()).IsEqualTo("kilocode/kilo-auto/free");
    }

    // ── The native Anthropic builder ────────────────────────────────────────

    [Test]
    public async Task Anthropic_RendersTheSameBytesTwice_AndNoTailAfterABigBody()
    {
        LlmRequest request = Request(3);

        using HttpRequestMessage first = AnthropicRequestBuilder.BuildRequest(
            request, "http://stub", "test-key", null, null);
        byte[] firstBytes = await BodyOfAsync(first);

        using (HttpRequestMessage big = AnthropicRequestBuilder.BuildRequest(
                   Request(64), "http://stub", "test-key", null, null))
        {
            await Assert.That((await BodyOfAsync(big)).Length).IsGreaterThan(firstBytes.Length);
        }

        using HttpRequestMessage again = AnthropicRequestBuilder.BuildRequest(
            request, "http://stub", "test-key", null, null);
        byte[] againBytes = await BodyOfAsync(again);

        await Assert.That(againBytes.Length).IsEqualTo(firstBytes.Length);
        await Assert.That(againBytes).IsEquivalentTo(firstBytes);

        using JsonDocument document = JsonDocument.Parse(againBytes);
        await Assert.That(document.RootElement.GetProperty("model").GetString()).IsEqualTo("kilocode/kilo-auto/free");
    }

    /// <summary>
    ///     The body is UTF-8 JSON and nothing else: no BOM, no trailing
    ///     newline, no null bytes — the shape a provider's HTTP stack expects.
    /// </summary>
    [Test]
    public async Task Compatible_BodyIsUtf8JsonWithNoExtraBytes()
    {
        var client = CompatibleClient();
        using HttpRequestMessage message = client.BuildRequest(Request(2), "test-key");
        byte[] bytes = await BodyOfAsync(message);

        await Assert.That(bytes.Length).IsGreaterThan(0);
        await Assert.That(bytes[0]).IsNotEqualTo((byte)0xEF);
        await Assert.That(bytes[^1]).IsNotEqualTo((byte)0x00);
        await Assert.That(bytes[^1]).IsNotEqualTo((byte)'\n');

        string text = Encoding.UTF8.GetString(bytes);
        await Assert.That(text.StartsWith('{')).IsTrue();
        await Assert.That(text.EndsWith('}')).IsTrue();
    }
}
