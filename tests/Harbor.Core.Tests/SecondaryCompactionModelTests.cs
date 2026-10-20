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

    [Test]
    public async Task Constructor_SecondaryInvalid_LogsWarningWithInvalidValueAndReason()
    {
        // #1582: the parse error used to be swallowed — the feature switched off
        // silently. The raw invalid value + the TryParse reason must reach the
        // Warning log (same observability as the resolution path).
        var log = new CapturingLogger();

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
        var log = new CapturingLogger();

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
}
