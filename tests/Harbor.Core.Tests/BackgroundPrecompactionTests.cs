using System.Runtime.CompilerServices;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Sessions;
using Harbor.Registries.Providers;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using FakeLlmClientBase = Harbor.TestKit.Generated.FakeLlmClient;

namespace Harbor.Core.Tests;

/// <summary>
///     #2225 — background precompaction (opencode PRECOMPUTE_LEAD): a summary is
///     warmed while the window still has headroom, then reused by the next
///     <see cref="CompactionService.CompactAsync" /> of the same history without
///     paying for a second summarization.
/// </summary>
public class BackgroundPrecompactionTests
{
    private static ModelInfo Model(int contextWindow) => new(
        "test-model", "test", "Test Model", contextWindow, 1_024,
        false, false, true, Pricing.Unknown, "openai");

    /// <summary>Window = 200k → reserve 30k → threshold 170k → lead band starts at 144.5k.</summary>
    private static ModelInfo WideModel => Model(200_000);

    private static List<AgentMessage> History(int messages) =>
        Enumerable.Range(0, messages)
            .Select(i => (AgentMessage)MessageFixtures.User(new string('x', 4_000) + $"-{i}"))
            .ToList();

    private static (CompactionService Service, CountingSummaryClient Client) CreateService()
    {
        var client = new CountingSummaryClient();
        var providers = new ProviderRegistry();
        providers.Register(ProviderId.Create("test"), () => client);
        var service = new CompactionService(
            new TokenTracker(), providers, NullLogger<CompactionService>.Instance);
        return (service, client);
    }

    [Test]
    public async Task ShouldPrecompute_OnlyInsideTheLeadBand()
    {
        var (service, _) = CreateService();

        // Too short to be worth folding.
        await Assert.That(service.ShouldPrecompute(History(3), WideModel)).IsFalse();

        // 140 × ~1100 = ~154k: under the 170k threshold, over the 144.5k lead.
        await Assert.That(service.ShouldPrecompute(History(140), WideModel)).IsTrue();

        // At/above the threshold the preventive pass summarizes synchronously.
        await Assert.That(service.ShouldPrecompute(History(180), WideModel)).IsFalse();
    }

    [Test]
    public async Task TryPrecomputeAsync_CachesSummary_CompactAsyncReusesIt()
    {
        var (service, client) = CreateService();
        var history = History(140);

        bool warmed = await service.TryPrecomputeAsync("s1", history, WideModel);

        await Assert.That(warmed).IsTrue();
        await Assert.That(client.Calls).IsEqualTo(1)
            .Because("the warm-up is a real summarization call against the provider");

        Result<CompactionResult> result = await service.CompactAsync("s1", history, WideModel);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Summary).IsEqualTo("warmed summary");
        await Assert.That(client.Calls).IsEqualTo(1)
            .Because("same history → the cached summary is reused, no second provider call");
    }

    [Test]
    public async Task CompactAsync_StalePrecomputedSummary_IsRecomputed()
    {
        var (service, client) = CreateService();
        var history = History(140);
        await Assert.That(await service.TryPrecomputeAsync("s2", history, WideModel)).IsTrue();

        // An append behind the warm-up's back invalidates its fingerprint.
        history.Add(MessageFixtures.User("a message appended after the warm-up"));
        var result = await service.CompactAsync("s2", history, WideModel);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(client.Calls).IsEqualTo(2)
            .Because("a stale cache must not replace a changed history's summary");
    }

    /// <summary>Counts summarization calls and returns a fixed summary.</summary>
    private sealed class CountingSummaryClient : FakeLlmClientBase
    {
        public CountingSummaryClient()
        {
            ProviderId = ProviderId.Create("test");
            GetModelsAsyncResult = Result.Success<IReadOnlyList<ModelInfo>>([WideModel]);
        }

        public int Calls { get; private set; }

        public override async IAsyncEnumerable<LlmEvent> StreamAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            yield return TestEvents.TextDelta("0", "warmed summary");
            await Task.Yield();
            yield return TestEvents.StepFinish(usage: new Usage(0, 4));
        }
    }
}
