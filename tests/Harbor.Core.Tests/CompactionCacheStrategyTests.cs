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
public class CompactionCacheStrategyTests : CompactionTestBase
{
    [Test]
    public async Task CompactAsync_SummaryRequest_CarriesEphemeralCacheStrategy()
    {
        var client = new CapturingLlmClient("summary text", [TestModel()]);
        var svc = CreateService(client);

        var result = await svc.CompactAsync("session-1", SixMessageHistory(), TestModel());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(client.Requests).IsNotNull();
        await Assert.That(client.Requests.Count).IsEqualTo(1);
        await Assert.That(client.Requests[0].CacheStrategy).IsEqualTo(CacheStrategy.Ephemeral);
    }
}
