using System.Runtime.CompilerServices;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Sessions;
using Harbor.Storage.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Core.Tests;

/// <summary>
///     A1: the compaction summarization request carries
///     <see cref="CacheStrategy.Ephemeral" /> — its system prompt is the
///     stable compile-time <c>SummarizationPrompt</c>, a perfect prefix-cache
///     candidate.
/// </summary>
public class CompactionCacheStrategyTests
{
    private static readonly ModelInfo HugeModel = new(
        "test-model",
        "test",
        "Test Model",
        1_000_000,
        1_024,
        false,
        false,
        true,
        Pricing.Unknown,
        "openai");

    [Test]
    public async Task CompactAsync_SummaryRequest_CarriesEphemeralCacheStrategy()
    {
        var client = new CapturingSummaryLlmClient("summary text");
        var providers = new ProviderRegistry();
        providers.Register(ProviderId.Create("test"), () => client);
        var svc = new CompactionService(
            new TokenTracker(),
            providers,
            NullLogger<CompactionService>.Instance)
        {
            KeepRecentTokens = 500,
            TailTurns = 0
        };

        var messages = new List<AgentMessage>
        {
            User("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            Assistant("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            User("cccccccccccccccccccccccccccccccccccccccc"),
            Assistant("dddddddddddddddddddddddddddddddddddd"),
            User("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"),
            Assistant("ffffffffffffffffffffffffffffffffffff")
        };

        var result = await svc.CompactAsync("session-1", messages, HugeModel);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(client.Requests).IsNotNull();
        await Assert.That(client.Requests!.Count).IsEqualTo(1);
        await Assert.That(client.Requests[0].CacheStrategy).IsEqualTo(CacheStrategy.Ephemeral);
    }

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

    /// <summary>Summary client that records every request it receives.</summary>
    private sealed class CapturingSummaryLlmClient(string summary) : ILlmClient
    {
        public List<LlmRequest>? Requests { get; private set; }

        public ProviderId ProviderId => ProviderId.Create("test");

        public async IAsyncEnumerable<LlmEvent> StreamAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            (Requests ??= []).Add(request);
            yield return new TextDeltaEvent("0", summary);
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            yield return new StepFinishEvent(0, "stop", new Usage(0, summary.Length / 4));
        }

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>(new[] { HugeModel }));
    }
}
