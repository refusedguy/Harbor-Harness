extern alias CompatWire;

using System.Text;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Providers.Anthropic;
using Harbor.Providers.OpenAI;
using Microsoft.Extensions.Logging.Abstractions;
using CompatClient = Harbor.Providers.OpenAiCompatible.OpenAiCompatibleLlmClient;
using CompatConfig = Harbor.Providers.OpenAiCompatible.ProviderConfig;

// Both provider assemblies compile the same shared OpenAiImageContent source,
// so the type only resolves unambiguously through the assembly alias.
using CompatImageContent = CompatWire::Harbor.Providers.Internal.OpenAiImageContent;

namespace Harbor.Providers.Tests;

/// <summary>
///     Issue #386 — <see cref="LlmImageBlock" /> must reach the model on every
///     provider path, in that provider's OWN wire dialect. The exact JSON shape
///     is asserted here (including the <c>data:</c> URL prefix) because a
///     silently-wrong shape is a provider 400 minutes later, mid-stream.
/// </summary>
public class ImageContentBlockTests
{
    /// <summary>Three bytes so the base64 payload is a readable, stable string.</summary>
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47];

    private const string PngDataUrlPrefix = "data:image/png;base64,";

    private static LlmRequest RequestWith(LlmContentBlock[] userContent, string model = "gpt-4o") =>
        new(
            Model: model,
            Messages: [new LlmUserMessage(userContent)],
            SystemPrompt: "sys",
            Tools: []);

    private static LlmContentBlock[] TextPlusImage() =>
        [new LlmTextBlock("what is this?"), new LlmImageBlock("image/png", PngBytes)];

    private static string ExpectedBase64() => Convert.ToBase64String(PngBytes);

    // ── data-URL construction (shared OpenAI helper) ──────────────────────

    [Test]
    public async Task ToDataUrl_ProducesDataPrefixedBase64()
    {
        // Exercised through the compat assembly; the native OpenAI builder
        // compiles the identical shared source (asserted via its wire output).
        string url = CompatImageContent.ToDataUrl("image/png", PngBytes);
        await Assert.That(url).IsEqualTo(PngDataUrlPrefix + ExpectedBase64());
    }

    [Test]
    public async Task ToDataUrl_EmptyPayload_StillEmitsValidPrefix()
    {
        string url = CompatImageContent.ToDataUrl("image/png", []);
        await Assert.That(url).IsEqualTo(PngDataUrlPrefix);
    }

    // ── OpenAI /chat/completions ──────────────────────────────────────────

    [Test]
    public async Task BuildChatCompletionsRequest_ImageTurn_EmitsImageUrlContentPart()
    {
        var request = RequestWith(TextPlusImage());
        var msg = OpenAiRequestBuilder.BuildChatCompletionsRequest(request, "https://api.openai.com/v1", NullLogger.Instance);

        string body = ReadBody(msg);
        using var doc = JsonDocument.Parse(body);

        JsonElement user = doc.RootElement.GetProperty("messages")[1];
        await Assert.That(user.GetProperty("role").GetString()).IsEqualTo("user");

        JsonElement[] parts = [.. user.GetProperty("content").EnumerateArray()];
        await Assert.That(parts.Length).IsEqualTo(2);

        await Assert.That(parts[0].GetProperty("type").GetString()).IsEqualTo("text");
        await Assert.That(parts[0].GetProperty("text").GetString()).IsEqualTo("what is this?");

        await Assert.That(parts[1].GetProperty("type").GetString()).IsEqualTo("image_url");
        string url = parts[1].GetProperty("image_url").GetProperty("url").GetString()!;
        await Assert.That(url).IsEqualTo(PngDataUrlPrefix + ExpectedBase64());
    }

    [Test]
    public async Task BuildChatCompletionsRequest_TextOnlyTurn_KeepsTheStringShape()
    {
        var request = RequestWith([new LlmTextBlock("plain")]);
        var msg = OpenAiRequestBuilder.BuildChatCompletionsRequest(request, "https://api.openai.com/v1", NullLogger.Instance);

        using var doc = JsonDocument.Parse(ReadBody(msg));
        JsonElement user = doc.RootElement.GetProperty("messages")[1];
        await Assert.That(user.GetProperty("content").ValueKind).IsEqualTo(JsonValueKind.String);
        await Assert.That(user.GetProperty("content").GetString()).IsEqualTo("plain");
    }

    [Test]
    public async Task BuildChatCompletionsRequest_ImageOnlyTurn_StillEmitsAnArray()
    {
        var request = RequestWith([new LlmImageBlock("image/png", PngBytes)]);
        var msg = OpenAiRequestBuilder.BuildChatCompletionsRequest(request, "https://api.openai.com/v1", NullLogger.Instance);

        using var doc = JsonDocument.Parse(ReadBody(msg));
        JsonElement[] parts = [.. doc.RootElement.GetProperty("messages")[1].GetProperty("content").EnumerateArray()];
        await Assert.That(parts.Length).IsEqualTo(1);
        await Assert.That(parts[0].GetProperty("type").GetString()).IsEqualTo("image_url");
    }

    // ── OpenAI /responses ─────────────────────────────────────────────────

    [Test]
    public async Task BuildResponsesRequest_ImageTurn_EmitsInputImagePart()
    {
        var request = RequestWith(TextPlusImage(), "gpt-5");
        var msg = OpenAiRequestBuilder.BuildResponsesRequest(request, "https://api.openai.com/v1", NullLogger.Instance);

        using var doc = JsonDocument.Parse(ReadBody(msg));
        JsonElement user = doc.RootElement.GetProperty("input")[1];

        JsonElement[] parts = [.. user.GetProperty("content").EnumerateArray()];
        await Assert.That(parts.Length).IsEqualTo(2);
        await Assert.That(parts[0].GetProperty("type").GetString()).IsEqualTo("input_text");
        await Assert.That(parts[1].GetProperty("type").GetString()).IsEqualTo("input_image");
        await Assert.That(parts[1].GetProperty("image_url").GetString())
            .IsEqualTo(PngDataUrlPrefix + ExpectedBase64());
    }

    // ── OpenAI-compatible adapter (Kilocode / OpenRouter / Groq / Mistral) ─

    [Test]
    public async Task CompatAdapter_ImageTurn_EmitsImageUrlContentPart()
    {
        string? body = await CaptureCompatBodyAsync();
        using var doc = JsonDocument.Parse(body!);

        JsonElement user = doc.RootElement.GetProperty("messages")[1];
        JsonElement[] parts = [.. user.GetProperty("content").EnumerateArray()];
        await Assert.That(parts.Length).IsEqualTo(2);

        await Assert.That(parts[0].GetProperty("type").GetString()).IsEqualTo("text");
        await Assert.That(parts[0].GetProperty("text").GetString()).IsEqualTo("what is this?");

        await Assert.That(parts[1].GetProperty("type").GetString()).IsEqualTo("image_url");
        await Assert.That(parts[1].GetProperty("image_url").GetProperty("url").GetString())
            .IsEqualTo(PngDataUrlPrefix + ExpectedBase64());
    }

    [Test]
    public async Task CompatAdapter_TextOnlyTurn_KeepsTheStringShape()
    {
        string? body = await CaptureCompatBodyAsync([new LlmTextBlock("plain")]);
        using var doc = JsonDocument.Parse(body!);

        JsonElement user = doc.RootElement.GetProperty("messages")[1];
        await Assert.That(user.GetProperty("content").ValueKind).IsEqualTo(JsonValueKind.String);
        await Assert.That(user.GetProperty("content").GetString()).IsEqualTo("plain");
    }

    // ── Anthropic (already supported — pinned so the dialect can't drift) ──

    [Test]
    public async Task AnthropicBuildRequest_ImageTurn_EmitsBase64SourceBlock()
    {
        var request = RequestWith(TextPlusImage(), "claude-opus-4");
        var msg = AnthropicRequestBuilder.BuildRequest(request, "https://api.anthropic.com/v1", "key", null, null);

        using var doc = JsonDocument.Parse(ReadBody(msg));
        JsonElement user = doc.RootElement.GetProperty("messages")[0];

        JsonElement[] parts = [.. user.GetProperty("content").EnumerateArray()];
        await Assert.That(parts.Length).IsEqualTo(2);

        // Anthropic's dialect: `type: "image"` + `source.media_type`, NOT `image_url`.
        await Assert.That(parts[0].GetProperty("type").GetString()).IsEqualTo("text");
        await Assert.That(parts[1].GetProperty("type").GetString()).IsEqualTo("image");
        JsonElement source = parts[1].GetProperty("source");
        await Assert.That(source.GetProperty("type").GetString()).IsEqualTo("base64");
        await Assert.That(source.GetProperty("media_type").GetString()).IsEqualTo("image/png");
        await Assert.That(source.GetProperty("data").GetString()).IsEqualTo(ExpectedBase64());
    }

    [Test]
    public async Task AnthropicBuildRequest_UsesTheProbedMimeTypeNotTheFileExtension()
    {
        // A `.png` file that is really a JPEG must go out as image/jpeg —
        // the MIME type comes from the magic-byte probe (ImageProbe).
        var request = RequestWith([new LlmImageBlock("image/jpeg", [0xFF, 0xD8, 0xFF])], "claude-opus-4");
        var msg = AnthropicRequestBuilder.BuildRequest(request, "https://api.anthropic.com/v1", "key", null, null);

        using var doc = JsonDocument.Parse(ReadBody(msg));
        JsonElement source = doc.RootElement.GetProperty("messages")[0]
            .GetProperty("content")[0].GetProperty("source");
        await Assert.That(source.GetProperty("media_type").GetString()).IsEqualTo("image/jpeg");
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static string ReadBody(HttpRequestMessage msg) =>
        msg.Content!.ReadAsStringAsync().GetAwaiter().GetResult();

    private static async Task<string?> CaptureCompatBodyAsync(
        LlmContentBlock[]? userContent = null)
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n",
                Encoding.UTF8,
                "text/event-stream")
        });

        var client = new CompatClient(
            new HttpClient(handler),
            new CompatConfig { Id = "stub", BaseUrl = "http://stub" },
            StubAuthResolver.Instance,
            StubModelCatalog.Instance,
            NullLogger<CompatClient>.Instance);

        var request = RequestWith(userContent ?? TextPlusImage(), "kilocode/kilo-auto/free");
        await foreach (var _ in client.StreamAsync(request)) { }

        await Assert.That(handler.CapturedRequests.Count).IsEqualTo(1);
        return await handler.CapturedRequests[0].Content!.ReadAsStringAsync();
    }
}
