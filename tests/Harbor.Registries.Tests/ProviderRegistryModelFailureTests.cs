using System.Runtime.CompilerServices;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using TUnit.Assertions;

namespace Harbor.Registries.Tests;

/// <summary>
///     Issue #202 (B3) regression tests: <see cref="ProviderRegistry" /> model
///     fan-out must convert per-provider failures into error records — never
///     throw on <c>.Value</c>, and never cache a failure (a later retry must
///     re-fetch instead of serving a stale error).
/// </summary>
public class ProviderRegistryModelFailureTests
{
    private sealed class StubCatalogClient : ILlmClient
    {
        private readonly Result<IReadOnlyList<ModelInfo>> _modelsResult;

        public StubCatalogClient(ProviderId providerId, Result<IReadOnlyList<ModelInfo>> modelsResult)
        {
            ProviderId = providerId;
            _modelsResult = modelsResult;
        }

        public ProviderId ProviderId { get; }

        public int ModelCalls { get; private set; }

        public async IAsyncEnumerable<LlmEvent> StreamAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
        {
            ModelCalls++;
            return Task.FromResult(_modelsResult);
        }
    }

    private static ModelInfo TestModel(string provider, string id) =>
        new(id, provider, id, 128_000, 4096, false, false, false, Pricing.Unknown, "openai");

    private static Result<IReadOnlyList<ModelInfo>> ModelsFor(string provider, string id) =>
        Result.Success<IReadOnlyList<ModelInfo>>(new[] { TestModel(provider, id) });

    [Test]
    public async Task GetAllModelsAsync_PartialFailure_ReturnsHealthyModelsOnly()
    {
        var registry = new ProviderRegistry();
        var goodPid = ProviderId.Create("good");
        var badPid = ProviderId.Create("bad");
        registry.Register(goodPid, () => new StubCatalogClient(goodPid, ModelsFor("good", "good-model")));
        registry.Register(badPid, () => new StubCatalogClient(badPid, Result.Failure<IReadOnlyList<ModelInfo>>("boom")));

        var result = await registry.GetAllModelsAsync();

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(1);
        await Assert.That(result.Value[0].Id).IsEqualTo("good-model");
    }

    [Test]
    public async Task GetAllModelsAsync_AllFailing_ReturnsFailureMentioningProvider()
    {
        var registry = new ProviderRegistry();
        var badPid = ProviderId.Create("bad");
        registry.Register(badPid, () => new StubCatalogClient(badPid, Result.Failure<IReadOnlyList<ModelInfo>>("boom")));

        var result = await registry.GetAllModelsAsync();

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("bad");
    }

    [Test]
    public async Task GetModelsCachedAsync_Failure_IsNotCached()
    {
        var registry = new ProviderRegistry();
        var pid = ProviderId.Create("flaky");
        var client = new StubCatalogClient(pid, Result.Failure<IReadOnlyList<ModelInfo>>("down"));
        registry.Register(pid, () => client);

        var first = await registry.GetModelsCachedAsync(pid);
        var second = await registry.GetModelsCachedAsync(pid);

        await Assert.That(first.IsFailure).IsTrue();
        await Assert.That(second.IsFailure).IsTrue();
        await Assert.That(client.ModelCalls).IsEqualTo(2);
    }

    [Test]
    public async Task GetModelsCachedAsync_Success_IsCached()
    {
        var registry = new ProviderRegistry();
        var pid = ProviderId.Create("stable");
        var client = new StubCatalogClient(pid, ModelsFor("stable", "stable-model"));
        registry.Register(pid, () => client);

        var first = await registry.GetModelsCachedAsync(pid);
        var second = await registry.GetModelsCachedAsync(pid);

        await Assert.That(first.IsSuccess).IsTrue();
        await Assert.That(second.IsSuccess).IsTrue();
        await Assert.That(second.Value.Count).IsEqualTo(1);
        await Assert.That(client.ModelCalls).IsEqualTo(1);
    }

    [Test]
    public async Task GetModelsCachedAsync_Unregistered_ReturnsFailure()
    {
        var registry = new ProviderRegistry();

        var result = await registry.GetModelsCachedAsync(ProviderId.Create("ghost"));

        await Assert.That(result.IsFailure).IsTrue();
    }
}
