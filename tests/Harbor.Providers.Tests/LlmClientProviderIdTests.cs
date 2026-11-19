using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Providers.Anthropic;
using Harbor.Providers.Internal;
using Harbor.Providers.Ollama;
using Harbor.Providers.OpenAI;
using Harbor.Providers.OpenAiCompatible;
using Harbor.TestKit;
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
            new TestAuthResolver(),
            new ConfiguredModelCatalog(Array.Empty<ModelInfo>()),
            NullLogger<AnthropicLlmClient>.Instance);

        await Assert.That(client.ProviderId.Value).IsEqualTo("anthropic");
    }

    [Test]
    public async Task OpenAILlmClient_ProviderId_IsOpenAI()
    {
        var client = new OpenAILlmClient(
            new HttpClient(),
            new OpenAIConfig(),
            new TestAuthResolver(),
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
            new TestAuthResolver(),
            FakeModelCatalog.Instance,
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
            new TestAuthResolver(),
            FakeModelCatalog.Instance,
            NullLogger<OpenAiCompatibleLlmClient>.Instance);

        await Assert.That(client.ProviderId.Value).IsEqualTo("myprovider");
    }
}

/// <summary>
///     #3801: behavior tests for the real <see cref="AnthropicLlmClient" />
///     catalogue path. The client no longer owns a C# model list; it asks the
///     config-backed <c>IModelCatalog</c> seam, which the registration path
///     fills from providers/anthropic.json. Loading that file here is the point:
///     a second hand-written C# copy could drift, this test cannot.
/// </summary>
public class AnthropicModelCatalogTests
{
    [Test]
    public async Task GetModelsAsync_ReturnsTheFourModelsInTheProviderJsonOrder()
    {
        var client = ClientFor(LoadAnthropicConfig());

        Result<IReadOnlyList<ModelInfo>> result = await client.GetModelsAsync();

        await Assert.That(result.IsSuccess).IsTrue();
        string[] ids = result.Value.Select(m => m.Id).ToArray();
        await Assert.That(ids.Length).IsEqualTo(4);
        // Indexed on purpose: the old C# catalog had this exact order, and the
        // issue asks to preserve the set AND the order.
        await Assert.That(ids[0]).IsEqualTo("claude-opus-4-20250514");
        await Assert.That(ids[1]).IsEqualTo("claude-sonnet-4-20250514");
        await Assert.That(ids[2]).IsEqualTo("claude-3-5-sonnet-20241022");
        await Assert.That(ids[3]).IsEqualTo("claude-3-5-haiku-20241022");
    }

    [Test]
    public async Task GetModelsAsync_PreservesEveryDeclaredModelProperty()
    {
        var client = ClientFor(LoadAnthropicConfig());

        IReadOnlyList<ModelInfo> models = (await client.GetModelsAsync()).Value;

        await Assert.That(models.Count).IsEqualTo(4);
        foreach (var model in models)
            await Assert.That(model.ProviderId).IsEqualTo("anthropic");

        var opus = models.Single(m => m.Id == "claude-opus-4-20250514");
        await Assert.That(opus.DisplayName).IsEqualTo("Claude Opus 4");
        await Assert.That(opus.ContextWindow).IsEqualTo(200_000);
        await Assert.That(opus.MaxOutputTokens).IsEqualTo(32_000);
        await Assert.That(opus.SupportsReasoning).IsTrue();
        await Assert.That(opus.SupportsVision).IsTrue();
        await Assert.That(opus.SupportsToolUse).IsTrue();
        await Assert.That(opus.PromptTemplate).IsEqualTo("anthropic");
        await Assert.That(opus.Pricing).IsEqualTo(new Pricing(15m, 75m, 1.5m, 18.75m));

        var haiku = models.Single(m => m.Id == "claude-3-5-haiku-20241022");
        await Assert.That(haiku.MaxOutputTokens).IsEqualTo(8192);
        await Assert.That(haiku.SupportsReasoning).IsFalse();
        await Assert.That(haiku.SupportsVision).IsTrue();
        await Assert.That(haiku.SupportsToolUse).IsTrue();
        await Assert.That(haiku.Pricing).IsEqualTo(new Pricing(0.8m, 4m, 0.08m, 1m));
    }

    /// <summary>
    ///     The delegation proof: change what the injected catalogue carries and
    ///     the client's answer changes with it. A leftover hardcoded list would
    ///     keep answering the old four models and fail this test.
    /// </summary>
    [Test]
    public async Task GetModelsAsync_AnswersFromTheInjectedCatalog_NotAHardcodedList()
    {
        var only = new ModelInfo(
            "probe-model", "anthropic", "Probe", 1234, 56, false, false, true, Pricing.Unknown, "anthropic");
        var client = ClientForProviderModels([only]);

        var models = (await client.GetModelsAsync()).Value;

        await Assert.That(models.Count).IsEqualTo(1);
        await Assert.That(models[0].Id).IsEqualTo("probe-model");
        await Assert.That(models[0].ContextWindow).IsEqualTo(1234);
    }

    private static AnthropicLlmClient ClientFor(ProviderConfig config) =>
        ClientForProviderModels(config.Models ?? Array.Empty<ModelInfo>());

    private static AnthropicLlmClient ClientForProviderModels(IReadOnlyList<ModelInfo> models) =>
        new(
            new HttpClient(),
            new AnthropicConfig(),
            new TestAuthResolver(),
            new ConfiguredModelCatalog(models),
            NullLogger<AnthropicLlmClient>.Instance);

    private static ProviderConfig LoadAnthropicConfig()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "providers", "anthropic.json");
            if (!File.Exists(candidate)) continue;

            Result<ProviderConfig> loaded = ProviderConfig.LoadFromFile(candidate);
            if (loaded.IsSuccess) return loaded.Value;
        }

        throw new InvalidOperationException("providers/anthropic.json was not found above the test output directory.");
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
