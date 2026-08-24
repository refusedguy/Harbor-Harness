using System.Runtime.CompilerServices;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Core.Sessions;
using Harbor.Storage.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Core.Tests;

/// <summary>
///     F17/F19 (deep2-core): compaction error paths.
///     F17 — an Esc during LLM summarisation must fail as "cancelled", never as a
///     generic compaction failure (which made the loop engage destructive
///     truncation fallback). F19 — a silent summarizer must not persist an EMPTY
///     summary anchor that silently discards the entire compressed history.
/// </summary>
public class CompactionErrorPathTests
{
    private static readonly ModelInfo HugeModel = new(
        "test-model", "test", "Test Model", 1_000_000, 1_024, false, false, true, Pricing.Unknown, "openai");

    private static CompactionService CreateService(ILlmClient client)
    {
        var providers = new ProviderRegistry();
        providers.Register(ProviderId.Create("test"), () => client);
        return new CompactionService(
            new TokenTracker(),
            providers,
            NullLogger<CompactionService>.Instance);
    }

    private static List<AgentMessage> FourLongMessages() =>
    [
        new UserMessage(Guid.NewGuid().ToString("N"), "session-1", DateTimeOffset.UtcNow,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "code", "test-model"),
        new AssistantMessage(Guid.NewGuid().ToString("N"), "session-1", DateTimeOffset.UtcNow,
            new[] { new TextPart("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb") }, StopReason.Stop, new Usage(0, 0), "test-model"),
        new UserMessage(Guid.NewGuid().ToString("N"), "session-1", DateTimeOffset.UtcNow,
            "cccccccccccccccccccccccccccccccccccccccc", "code", "test-model"),
        new AssistantMessage(Guid.NewGuid().ToString("N"), "session-1", DateTimeOffset.UtcNow,
            new[] { new TextPart("dddddddddddddddddddddddddddddddddddd") }, StopReason.Stop, new Usage(0, 0), "test-model")
    ];

    private static CompactionService Configured(CompactionService svc)
    {
        svc.KeepRecentTokens = 60;
        svc.TailTurns = 0;
        return svc;
    }

    [Test]
    public async Task CompactAsync_SilentStreamNoDeltas_ReturnsFailureInsteadOfEmptyAnchor()
    {
        var svc = Configured(CreateService(new SilentLlmClient()));

        var result = await svc.CompactAsync("session-1", FourLongMessages(), HugeModel);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("empty summary");
    }

    [Test]
    public async Task CompactAsync_CancelledDuringSummarization_FailsAsCancelledNotAsFailure()
    {
        var svc = Configured(CreateService(new HangingLlmClient()));
        using var cts = new CancellationTokenSource();

        Task<CSharpFunctionalExtensions.Result<CompactionResult>> pending =
            svc.CompactAsync("session-1", FourLongMessages(), HugeModel, cts.Token);
        cts.Cancel();

        CSharpFunctionalExtensions.Result<CompactionResult> result = await pending;

        await Assert.That(result.IsFailure).IsTrue();
        // The distinguishing marker: cancellation is reported as cancelled, so the
        // AgentLoop error branch can tell it apart from a real summarizer failure.
        await Assert.That(result.Error).Contains("cancelled");
        await Assert.That(result.Error).DoesNotContain("Compaction failed");
    }

    /// <summary>Streams nothing at all — no deltas, no error, no finish.</summary>
    private sealed class SilentLlmClient : ILlmClient
    {
        public ProviderId ProviderId => ProviderId.Create("test");

        public async IAsyncEnumerable<LlmEvent> StreamAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>(new[] { HugeModel }));
    }

    /// <summary>Hangs until cancelled — mirrors a real in-flight summarisation.</summary>
    private sealed class HangingLlmClient : ILlmClient
    {
        public ProviderId ProviderId => ProviderId.Create("test");

        public async IAsyncEnumerable<LlmEvent> StreamAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            yield break;
        }

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>(new[] { HugeModel }));
    }
}
