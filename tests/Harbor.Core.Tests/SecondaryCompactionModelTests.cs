using System.Runtime.CompilerServices;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Configuration;
using Harbor.Application.Sessions;
using Harbor.Registries;
using Harbor.Storage.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Core.Tests;

/// <summary>
///     Ф8/A3: CompactionService routes the summarization request to the
///     configured cheap <c>secondaryModel</c> ("provider/model") and falls
///     back to the primary model on ANY resolution failure. HarborConfig
///     round-trips the key through RawConfigDto.
/// </summary>
public class SecondaryCompactionModelTests
{
    private static readonly ModelInfo PrimaryModel = new(
        "test-model",
        "test",
        "Primary Test Model",
        1_000_000,
        1_024,
        false,
        false,
        true,
        Pricing.Unknown,
        "openai");

    private static readonly ModelInfo CheapModel = new(
        "cheap-model",
        "cheap",
        "Cheap Test Model",
        100_000,
        1_024,
        false,
        false,
        true,
        Pricing.Unknown,
        "openai");

    [Test]
    public async Task CompactAsync_NoSecondary_PrimaryClientSummarizes()
    {
        var primary = new CapturingLlmClient("summary", [PrimaryModel]);
        var svc = CreateService(primary, secondaryModel: null);

        var result = await svc.CompactAsync("session-1", History(), HugeWindow(PrimaryModel));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(primary.Requests.Count).IsEqualTo(1);
        await Assert.That(primary.Requests[0].Model).IsEqualTo("test-model");
        var summaryAssistant = (AssistantMessage)result.Value.SummaryMessage;
        await Assert.That(summaryAssistant.Model).IsEqualTo("test-model");
    }

    [Test]
    public async Task CompactAsync_SecondaryConfigured_CheapClientSummarizes()
    {
        var primary = new CapturingLlmClient("primary summary", [PrimaryModel]);
        var cheap = new CapturingLlmClient("cheap summary", [CheapModel]);
        var providers = new ProviderRegistry();
        providers.Register(ProviderId.Create("test"), () => primary);
        providers.Register(ProviderId.Create("cheap"), () => cheap);
        var svc = new CompactionService(
            new TokenTracker(),
            providers,
            NullLogger<CompactionService>.Instance,
            secondaryModel: "cheap/cheap-model")
        {
            KeepRecentTokens = 500,
            TailTurns = 0
        };

        var result = await svc.CompactAsync("session-1", History(), HugeWindow(PrimaryModel));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(cheap.Requests.Count).IsEqualTo(1);
        await Assert.That(cheap.Requests[0].CacheStrategy).IsEqualTo(CacheStrategy.Ephemeral);
        await Assert.That(primary.Requests.Count).IsEqualTo(0);
        var summaryAssistant = (AssistantMessage)result.Value.SummaryMessage;
        await Assert.That(summaryAssistant.Model).IsEqualTo("cheap-model");
    }

    [Test]
    public async Task CompactAsync_SecondaryUnresolvable_FallsBackToPrimary()
    {
        var primary = new CapturingLlmClient("primary summary", [PrimaryModel]);
        // "cheap" provider is NOT registered → resolution must fail cleanly.
        var providers = new ProviderRegistry();
        providers.Register(ProviderId.Create("test"), () => primary);
        var svc = new CompactionService(
            new TokenTracker(),
            providers,
            NullLogger<CompactionService>.Instance,
            secondaryModel: "cheap/cheap-model")
        {
            KeepRecentTokens = 500,
            TailTurns = 0
        };

        var result = await svc.CompactAsync("session-1", History(), HugeWindow(PrimaryModel));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(primary.Requests.Count).IsEqualTo(1);
        var summaryAssistant = (AssistantMessage)result.Value.SummaryMessage;
        await Assert.That(summaryAssistant.Model).IsEqualTo("test-model");
    }

    [Test]
    public async Task ConfigNormalize_InvalidSecondaryModel_ReturnsFailure()
    {
        var raw = new RawConfigDto { SecondaryModel = "no-slash-here" };

        var result = ConfigNormalizer.Normalize(raw);

        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task ConfigRoundTrip_SecondaryModel_SurvivesToRawAndBack()
    {
        var config = new HarborConfig { SecondaryModel = "cheap/cheap-model" };
        var raw = config.ToRaw();

        var normalized = ConfigNormalizer.Normalize(raw);

        await Assert.That(normalized.IsSuccess).IsTrue();
        await Assert.That(normalized.Value.SecondaryModel).IsEqualTo("cheap/cheap-model");
    }

    private static CompactionService CreateService(
        CapturingLlmClient primary,
        string? secondaryModel)
    {
        var providers = new ProviderRegistry();
        providers.Register(ProviderId.Create("test"), () => primary);
        return new CompactionService(
            new TokenTracker(),
            providers,
            NullLogger<CompactionService>.Instance,
            secondaryModel)
        {
            KeepRecentTokens = 500,
            TailTurns = 0
        };
    }

    /// <summary>Six-message history large enough for a deterministic head cut.</summary>
    private static List<AgentMessage> History() =>
    [
        User("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
        Assistant("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
        User("cccccccccccccccccccccccccccccccccccccccc"),
        Assistant("dddddddddddddddddddddddddddddddddddd"),
        User("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"),
        Assistant("ffffffffffffffffffffffffffffffffffff")
    ];

    /// <summary>The passed model with a huge window so ShouldCompact-style budgeting never interferes.</summary>
    private static ModelInfo HugeWindow(ModelInfo source) => source with { ContextWindow = 1_000_000 };

    private static UserMessage User(string content) => new(
        Guid.NewGuid().ToString("N"),
        "session-1",
        DateTimeOffset.UtcNow,
        content,
        "code",
        "test-model");

    private static AssistantMessage Assistant(string text) => new(
        Guid.NewGuid().ToString("N"),
        "session-1",
        DateTimeOffset.UtcNow,
        new[] { new TextPart(text) },
        StopReason.Stop,
        new Usage(0, 0),
        "test-model");

    /// <summary>Capturing client bound to a fixed provider id and catalog.</summary>
    private sealed class CapturingLlmClient(string summary, ModelInfo[] catalog) : ILlmClient
    {
        public List<LlmRequest> Requests { get; } = [];

        public ProviderId ProviderId { get; init; } = ProviderId.Create("test");

        public async IAsyncEnumerable<LlmEvent> StreamAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            yield return new TextDeltaEvent("0", summary);
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            yield return new StepFinishEvent(0, "stop", new Usage(0, summary.Length / 4));
        }

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>(catalog));
    }
}
