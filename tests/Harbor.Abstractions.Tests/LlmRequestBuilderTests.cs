using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;

namespace Harbor.Abstractions.Tests;

public class LlmRequestBuilderTests
{
    private static IReadOnlyList<LlmMessage> Messages() => [LlmUserMessage.Text("hi")];

    private static IReadOnlyList<ToolDefinition> Tools() => [];

    [Test]
    public async Task ForTurn_SetsTurnOptions_AndEphemeralCache()
    {
        var request = LlmRequestBuilder.ForTurn(
            "model",
            Messages(),
            "system",
            Tools(),
            maxOutputTokens: 1024,
            temperature: 0.7m,
            reasoningEffort: ReasoningEffort.High).Build();

        await Assert.That(request.Model).IsEqualTo("model");
        await Assert.That(request.SystemPrompt).IsEqualTo("system");
        await Assert.That(request.MaxOutputTokens).IsEqualTo(1024);
        await Assert.That(request.Temperature).IsEqualTo(0.7m);
        await Assert.That(request.ReasoningEffort).IsEqualTo(ReasoningEffort.High);
        await Assert.That(request.CacheStrategy).IsEqualTo(CacheStrategy.Ephemeral);
        await Assert.That(request.ToolChoice).IsNull();
        await Assert.That(request.TopP).IsNull();
    }

    [Test]
    public async Task ForTurn_EmptySystemPrompt_DisablesCache()
    {
        var request = LlmRequestBuilder.ForTurn(
            "model",
            Messages(),
            string.Empty,
            Tools(),
            maxOutputTokens: null,
            temperature: null,
            reasoningEffort: null).Build();

        await Assert.That(request.CacheStrategy).IsEqualTo(CacheStrategy.None);
        await Assert.That(request.MaxOutputTokens).IsNull();
    }

    [Test]
    public async Task ForSummary_SetsSummaryPreset()
    {
        var request = LlmRequestBuilder.ForSummary("summary-model", "prompt", "summarize", 512).Build();

        await Assert.That(request.Model).IsEqualTo("summary-model");
        await Assert.That(request.SystemPrompt).IsEqualTo("summarize");
        await Assert.That(request.Temperature).IsEqualTo(0.3m);
        await Assert.That(request.MaxOutputTokens).IsEqualTo(512);
        await Assert.That(request.CacheStrategy).IsEqualTo(CacheStrategy.Ephemeral);
        await Assert.That(request.Tools).IsEmpty();
        await Assert.That(request.Messages.Count).IsEqualTo(1);
    }

    [Test]
    public async Task With_StepByStep_OverridesPreset()
    {
        var headers = new Dictionary<string, string> { ["x-test"] = "1" };
        var request = LlmRequestBuilder.ForSummary("m", "p", "s", 256)
            .WithTemperature(0.9m)
            .WithTopP(0.5m)
            .WithTopK(10)
            .WithToolChoice(new ToolChoice.None())
            .WithExtraHeaders(headers)
            .WithSessionAffinity("affinity")
            .Build();

        await Assert.That(request.Temperature).IsEqualTo(0.9m);
        await Assert.That(request.TopP).IsEqualTo(0.5m);
        await Assert.That(request.TopK).IsEqualTo(10);
        await Assert.That(request.ToolChoice).IsTypeOf<ToolChoice.None>();
        await Assert.That(request.ExtraHeaders!["x-test"]).IsEqualTo("1");
        await Assert.That(request.SessionAffinity).IsEqualTo("affinity");
        // Untouched preset values survive step-by-step overrides.
        await Assert.That(request.MaxOutputTokens).IsEqualTo(256);
        await Assert.That(request.CacheStrategy).IsEqualTo(CacheStrategy.Ephemeral);
    }

    [Test]
    public async Task Ctor_Defaults_MatchLlmRequestDefaults()
    {
        var request = new LlmRequestBuilder("m", Messages(), "s", Tools()).Build();

        await Assert.That(request.ToolChoice).IsNull();
        await Assert.That(request.MaxOutputTokens).IsNull();
        await Assert.That(request.Temperature).IsNull();
        await Assert.That(request.TopP).IsNull();
        await Assert.That(request.TopK).IsNull();
        await Assert.That(request.ReasoningEffort).IsNull();
        await Assert.That(request.CacheStrategy).IsEqualTo(CacheStrategy.None);
        await Assert.That(request.ExtraHeaders).IsNull();
        await Assert.That(request.SessionAffinity).IsNull();
    }
}
