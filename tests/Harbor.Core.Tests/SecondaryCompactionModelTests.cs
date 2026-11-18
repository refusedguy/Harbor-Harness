using Harbor.TestKit;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using Harbor.Registries.Providers;

namespace Harbor.Core.Tests;

/// <summary>
///     Ф8/A3: CompactionService routes the summarization request to the
///     configured cheap <c>secondaryModel</c> ("provider/model") and falls
///     back to the primary model on ANY resolution failure. HarborConfig
///     round-trips the key through RawConfigDto.
/// </summary>
public class SecondaryCompactionModelTests : CompactionTestBase
{
    private static ModelInfo PrimaryModel => TestModel(displayName: "Primary Test Model");

    private static ModelInfo CheapModel => TestModel(id: "cheap-model", provider: "cheap", displayName: "Cheap Test Model");

    [Test]
    public async Task CompactAsync_NoSecondary_PrimaryClientSummarizes()
    {
        var primary = new CapturingLlmClient("summary", [PrimaryModel]);
        var svc = CreateService(primary);

        var result = await svc.CompactAsync("session-1", SixMessageHistory(), HugeWindow(PrimaryModel));

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
        var svc = CreateService(primary, "cheap/cheap-model", extraProvider: ("cheap", cheap));

        var result = await svc.CompactAsync("session-1", SixMessageHistory(), HugeWindow(PrimaryModel));

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
        var svc = CreateService(primary, "cheap/cheap-model");

        var result = await svc.CompactAsync("session-1", SixMessageHistory(), HugeWindow(PrimaryModel));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(primary.Requests.Count).IsEqualTo(1);
        var summaryAssistant = (AssistantMessage)result.Value.SummaryMessage;
        await Assert.That(summaryAssistant.Model).IsEqualTo("test-model");
    }

    /// <summary>
    ///     #2581: the service is a singleton, so the first resolution must be
    ///     single-flight. Before the fix, N simultaneous first compactions each
    ///     called GetModelsAsync for the same secondary model, and a slow catalog
    ///     delayed all of them. The winner owns the lookup; the others await it.
    /// </summary>
    [Test]
    public async Task ConcurrentFirstCompactions_ResolveSecondaryCatalogOnce()
    {
        var primary = new CapturingLlmClient("primary summary", [PrimaryModel]);
        var cheap = new GatedCatalogLlmClient("cheap summary", [CheapModel]);
        var svc = CreateService(primary, "cheap/cheap-model", extraProvider: ("cheap", cheap));

        const int callers = 8;
        Task<Result<CompactionResult>>[] runs = Enumerable.Range(0, callers)
            .Select(i => Task.Run(() => svc.CompactAsync(
                $"session-{i}", SixMessageHistory(), HugeWindow(PrimaryModel))))
            .ToArray();

        // The winner is blocked in its catalog lookup. Every other caller must
        // be waiting on that one task, not starting a lookup of its own.
        await cheap.CatalogEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cheap.Release();

        Result<CompactionResult>[] outcomes =
            await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(cheap.CatalogCalls).IsEqualTo(1).Because(
            "one catalog lookup serves all first compactions; N lookups means the "
            + "singleton's memoization is still unsynchronised");
        await Assert.That(outcomes.All(o => o.IsSuccess
            && ((AssistantMessage)o.Value.SummaryMessage).Model == "cheap-model")).IsTrue().Because(
            "every compaction still summarizes through the resolved secondary client");
        await Assert.That(primary.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Constructor_SecondaryInvalid_LogsWarningWithInvalidValueAndReason()
    {
        // #1582: the parse error used to be swallowed — the feature switched off
        // silently. The raw invalid value + the TryParse reason must reach the
        // Warning log (same observability as the resolution path).
        var log = new CapturingLogger<CompactionService>();

        _ = CreateService(logger: log, secondaryModel: "no-slash-here");

        await Assert.That(log.Messages.Any(
            m => m.Contains("no-slash-here") && m.Contains("Invalid model reference"))).IsTrue();
    }

    [Test]
    public async Task CompactAsync_SecondaryInvalid_FallsBackToPrimaryClient()
    {
        var primary = new CapturingLlmClient("primary summary", [PrimaryModel]);
        var svc = CreateService(primary, "no-slash-here");

        var result = await svc.CompactAsync("session-1", SixMessageHistory(), HugeWindow(PrimaryModel));

        // Same degradation as before (#1582), but the config error is logged at
        // construction; here just pin the behaviour on a single primary client.
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(primary.Requests.Count).IsEqualTo(1);
        var summaryAssistant = (AssistantMessage)result.Value.SummaryMessage;
        await Assert.That(summaryAssistant.Model).IsEqualTo("test-model");
    }

    [Test]
    public async Task Constructor_SecondaryNotConfigured_LogsNothing()
    {
        // null/blank is "feature off", not "feature broken" — must stay silent
        // and must not pre-fire ModelRef.TryParse's empty-reference error.
        var log = new CapturingLogger<CompactionService>();

        _ = CreateService(logger: log, secondaryModel: " ");

        await Assert.That(log.Messages.Count).IsEqualTo(0);
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

    /// <summary>
    ///     A secondary client whose catalog lookup is gated, so the test can hold
    ///     the first resolution open while the other compactions pile up on it.
    /// </summary>
    private sealed class GatedCatalogLlmClient(string summary, ModelInfo[] catalog) : ILlmClient
    {
        public TaskCompletionSource CatalogEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _catalogCalls;

        public int CatalogCalls => Volatile.Read(ref _catalogCalls);

        public ProviderId ProviderId { get; } = ProviderId.Create("cheap");

        public void Release() => _release.TrySetResult();

        public async Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _catalogCalls);
            CatalogEntered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success<IReadOnlyList<ModelInfo>>(catalog);
        }

        public async IAsyncEnumerable<LlmEvent> StreamAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return TestEvents.TextDelta("0", summary);
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            yield return TestEvents.StepFinish(usage: new Usage(0, summary.Length / 4));
        }
    }
}
