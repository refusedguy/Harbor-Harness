using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Providers;
using Harbor.Providers.Anthropic;
using Harbor.Providers.OpenAI;
using Harbor.Providers.OpenAiCompatible;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Providers.Tests;

/// <summary>
///     Issue #202 (B2) regression tests: when API-key resolution fails, every
///     native client must surface a single <see cref="ErrorEvent" /> classified
///     as <see cref="ProviderErrorKind.Auth" /> — and must never touch HTTP
///     (the guarded <c>apiKeyResult.Value</c> sites).
/// </summary>
public class LlmClientAuthFailureTests
{
    private static async Task AssertSingleAuthErrorAsync(List<LlmEvent> events, StubHttpMessageHandler handler)
    {
        var errors = events.OfType<ErrorEvent>().ToList();
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Kind).IsEqualTo(ProviderErrorKind.Auth);
        await Assert.That(errors[0].Message).Contains("Auth failed");
        await Assert.That(handler.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task CompatStream_AuthFailure_EmitsSingleAuthErrorWithoutHttp()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException(
            "HTTP must not be called when API-key resolution failed."));
        var client = new OpenAiCompatibleLlmClient(
            new HttpClient(handler),
            new ProviderConfig { Id = "stub", BaseUrl = "http://stub" },
            new FailingAuthResolver(),
            StubModelCatalog.Instance,
            NullLogger<OpenAiCompatibleLlmClient>.Instance);

        var events = await StreamCollector.CollectAsync(client.StreamAsync(new LlmRequest("m", [LlmUserMessage.Text("hi")], "", [])));

        await AssertSingleAuthErrorAsync(events, handler);
    }

    [Test]
    public async Task AnthropicStream_AuthFailure_EmitsSingleAuthErrorWithoutHttp()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException(
            "HTTP must not be called when API-key resolution failed."));
        var client = new AnthropicLlmClient(
            new HttpClient(handler),
            new AnthropicConfig { BaseUrl = "http://stub" },
            new FailingAuthResolver(),
            NullLogger<AnthropicLlmClient>.Instance);

        var events = await StreamCollector.CollectAsync(client.StreamAsync(new LlmRequest("m", [LlmUserMessage.Text("hi")], "", [])));

        await AssertSingleAuthErrorAsync(events, handler);
    }

    [Test]
    public async Task OpenAIStream_AuthFailure_EmitsSingleAuthErrorWithoutHttp()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException(
            "HTTP must not be called when API-key resolution failed."));
        var client = new OpenAILlmClient(
            new HttpClient(handler),
            new OpenAIConfig { BaseUrl = "http://stub" },
            new FailingAuthResolver(),
            NullLogger<OpenAILlmClient>.Instance);

        var events = await StreamCollector.CollectAsync(client.StreamAsync(new LlmRequest("m", [LlmUserMessage.Text("hi")], "", [])));

        await AssertSingleAuthErrorAsync(events, handler);
    }
}
