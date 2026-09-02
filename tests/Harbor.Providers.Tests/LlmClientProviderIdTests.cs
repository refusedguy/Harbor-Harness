using Harbor.Providers.Anthropic;
using Harbor.Providers.Ollama;
using Harbor.Providers.OpenAI;
using Harbor.Providers.OpenAiCompatible;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Providers.Tests;
/// <summary>
///     Tests that each ILlmClient implementation reports the correct ProviderId.
///     No HTTP requests are made — ProviderId is set in the constructor.
/// </summary>
public class LlmClientProviderIdTests
{
    [Test]
    public async Task AnthropicLlmClient_ProviderId_IsAnthropic()
    {
        var client = new AnthropicLlmClient(
            new HttpClient(),
            new AnthropicConfig(),
            StubAuthResolver.Instance,
            NullLogger<AnthropicLlmClient>.Instance);

        await Assert.That(client.ProviderId.Value).IsEqualTo("anthropic");
    }

    [Test]
    public async Task OpenAILlmClient_ProviderId_IsOpenAI()
    {
        var client = new OpenAILlmClient(
            new HttpClient(),
            new OpenAIConfig(),
            StubAuthResolver.Instance,
            NullLogger<OpenAILlmClient>.Instance);

        await Assert.That(client.ProviderId.Value).IsEqualTo("openai");
    }

    [Test]
    public async Task OllamaLlmClient_ProviderId_IsOllama()
    {
        var client = new OllamaLlmClient(
            new HttpClient(),
            new OllamaConfig(),
            NullLogger<OllamaLlmClient>.Instance);

        await Assert.That(client.ProviderId.Value).IsEqualTo("ollama");
    }

    [Test]
    public async Task OpenAiCompatibleLlmClient_ProviderId_MatchesConfig()
    {
        var config = new ProviderConfig
        {
            Id = "kilocode",
            DisplayName = "Kilo Code",
            BaseUrl = "https://api.kilocode.ai/v1"
        };

        var client = new OpenAiCompatibleLlmClient(
            new HttpClient(),
            config,
            StubAuthResolver.Instance,
            StubModelCatalog.Instance,
            NullLogger<OpenAiCompatibleLlmClient>.Instance);

        await Assert.That(client.ProviderId.Value).IsEqualTo("kilocode");
    }

    [Test]
    public async Task OpenAiCompatibleLlmClient_ProviderId_NormalizesCase()
    {
        // ProviderId.Create lowercases the value, so "MyProvider" becomes "myprovider".
        var config = new ProviderConfig
        {
            Id = "MyProvider",
            DisplayName = "My Provider",
            BaseUrl = "https://api.example.com/v1"
        };

        var client = new OpenAiCompatibleLlmClient(
            new HttpClient(),
            config,
            StubAuthResolver.Instance,
            StubModelCatalog.Instance,
            NullLogger<OpenAiCompatibleLlmClient>.Instance);

        await Assert.That(client.ProviderId.Value).IsEqualTo("myprovider");
    }
}

/// <summary>
///     Tests for the static AnthropicModels catalog (no network calls).
/// </summary>
public class AnthropicModelsTests
{
    [Test]
    public async Task All_ContainsFourModels() => await Assert.That(AnthropicModels.All.Count).IsEqualTo(4);

    [Test]
    public async Task All_ContainsClaudeOpus4()
    {
        string[] ids = AnthropicModels.All.Select(m => m.Id).ToArray();
        await Assert.That(ids).Contains("claude-opus-4-20250514");
    }

    [Test]
    public async Task All_ContainsClaudeSonnet4()
    {
        string[] ids = AnthropicModels.All.Select(m => m.Id).ToArray();
        await Assert.That(ids).Contains("claude-sonnet-4-20250514");
    }

    [Test]
    public async Task All_ContainsClaudeSonnet35()
    {
        string[] ids = AnthropicModels.All.Select(m => m.Id).ToArray();
        await Assert.That(ids).Contains("claude-3-5-sonnet-20241022");
    }

    [Test]
    public async Task All_ContainsClaudeHaiku35()
    {
        string[] ids = AnthropicModels.All.Select(m => m.Id).ToArray();
        await Assert.That(ids).Contains("claude-3-5-haiku-20241022");
    }

    [Test]
    public async Task All_EveryModel_HasAnthropicProviderId()
    {
        foreach (var model in AnthropicModels.All)
        {
            await Assert.That(model.ProviderId).IsEqualTo("anthropic");
        }
    }

    [Test]
    public async Task ClaudeOpus4_HasCorrectContextWindow() => await Assert.That(AnthropicModels.ClaudeOpus4.ContextWindow).IsEqualTo(200_000);

    [Test]
    public async Task ClaudeOpus4_SupportsReasoningAndVisionAndTools()
    {
        await Assert.That(AnthropicModels.ClaudeOpus4.SupportsReasoning).IsTrue();
        await Assert.That(AnthropicModels.ClaudeOpus4.SupportsVision).IsTrue();
        await Assert.That(AnthropicModels.ClaudeOpus4.SupportsToolUse).IsTrue();
    }
}

/// <summary>
///     Tests for the static OpenAIModels catalog (no network calls).
/// </summary>
public class OpenAIModelsTests
{
    [Test]
    public async Task All_ContainsSixModels() => await Assert.That(OpenAIModels.All.Count).IsEqualTo(6);

    [Test]
    public async Task All_ContainsExpectedModelIds()
    {
        string[] ids = OpenAIModels.All.Select(m => m.Id).OrderBy(s => s).ToArray();
        await Assert.That(ids[0]).IsEqualTo("gpt-4.1");
        await Assert.That(ids[1]).IsEqualTo("gpt-4.1-mini");
        await Assert.That(ids[2]).IsEqualTo("gpt-4o");
        await Assert.That(ids[3]).IsEqualTo("gpt-4o-mini");
        await Assert.That(ids[4]).IsEqualTo("o3");
        await Assert.That(ids[5]).IsEqualTo("o4-mini");
    }

    [Test]
    public async Task All_EveryModel_HasOpenAIProviderId()
    {
        foreach (var model in OpenAIModels.All)
        {
            await Assert.That(model.ProviderId).IsEqualTo("openai");
        }
    }

    [Test]
    public async Task O3_SupportsReasoning() => await Assert.That(OpenAIModels.O3.SupportsReasoning).IsTrue();

    [Test]
    public async Task Gpt4oMini_DoesNotSupportReasoning() => await Assert.That(OpenAIModels.Gpt4oMini.SupportsReasoning).IsFalse();
}
