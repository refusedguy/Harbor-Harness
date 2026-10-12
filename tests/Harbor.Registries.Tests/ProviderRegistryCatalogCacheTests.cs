using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using TUnit.Assertions;
using Harbor.Registries.Providers;

namespace Harbor.Registries.Tests;

/// <summary>
///     Issue #1108 regression tests: <see cref="ProviderRegistry" /> keeps
///     exactly one model catalog cache (short-TTL) shared by
///     <see cref="ProviderRegistry.GetModelsCachedAsync" /> and the
///     <see cref="ProviderRegistry.GetAllModelsAsync" /> fan-out. The fan-out
///     used to keep a second, TTL-free map, so it served forever-stale
///     catalogs and never reused entries fetched through the single-provider
///     path (one extra HTTP round-trip per provider per fan-out).
/// </summary>
public class ProviderRegistryCatalogCacheTests
{
    private sealed class CountingCatalogClient : ILlmClient
    {
        private readonly Result<IReadOnlyList<ModelInfo>> _modelsResult;

        public CountingCatalogClient(ProviderId providerId, Result<IReadOnlyList<ModelInfo>> modelsResult)
        {
            ProviderId = providerId;
            _modelsResult = modelsResult;
        }

        public ProviderId ProviderId { get; }

        public int ModelCalls { get; private set; }

        public async IAsyncEnumerable<LlmEvent> StreamAsync(
            LlmRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
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

    /// <summary>
    ///     An entry fetched through <c>GetModelsCachedAsync</c> must satisfy a
    ///     later fan-out: against the pre-#1108 code the fan-out read its own
    ///     TTL-free map, missed, and fetched a second time.
    /// </summary>
    [Test]
    public async Task GetAllModelsAsync_ReusesEntryFetchedByGetModelsCachedAsync()
    {
        var registry = new ProviderRegistry();
        var pid = ProviderId.Create("shared");
        var client = new CountingCatalogClient(
            pid,
            Result.Success<IReadOnlyList<ModelInfo>>(new[] { TestModel("shared", "shared-model") }));
        registry.Register(pid, () => client);

        var single = await registry.GetModelsCachedAsync(pid);
        var all = await registry.GetAllModelsAsync();

        await Assert.That(single.IsSuccess).IsTrue();
        await Assert.That(all.IsSuccess).IsTrue();
        await Assert.That(all.Value.Count).IsEqualTo(1);
        await Assert.That(client.ModelCalls).IsEqualTo(1)
            .Because("one dataset means one round-trip: the fan-out must reuse the TTL entry");
    }

    /// <summary>
    ///     The fan-out populates the same TTL cache it reads, so a repeat
    ///     fan-out within the window fetches nothing.
    /// </summary>
    [Test]
    public async Task GetAllModelsAsync_SecondCallWithinTtl_DoesNotRefetch()
    {
        var registry = new ProviderRegistry();
        var pid = ProviderId.Create("stable");
        var client = new CountingCatalogClient(
            pid,
            Result.Success<IReadOnlyList<ModelInfo>>(new[] { TestModel("stable", "stable-model") }));
        registry.Register(pid, () => client);

        var first = await registry.GetAllModelsAsync();
        var second = await registry.GetAllModelsAsync();

        await Assert.That(first.IsSuccess).IsTrue();
        await Assert.That(second.IsSuccess).IsTrue();
        await Assert.That(client.ModelCalls).IsEqualTo(1);
    }

    /// <summary>
    ///     One invalidation covers both readers — there is no second map left
    ///     behind holding a stale catalog after
    ///     <c>InvalidateModelCache</c>.
    /// </summary>
    [Test]
    public async Task InvalidateModelCache_ForcesRefetchForBothReaders()
    {
        var registry = new ProviderRegistry();
        var pid = ProviderId.Create("flapping");
        var client = new CountingCatalogClient(
            pid,
            Result.Success<IReadOnlyList<ModelInfo>>(new[] { TestModel("flapping", "flapping-model") }));
        registry.Register(pid, () => client);

        _ = await registry.GetAllModelsAsync();
        registry.InvalidateModelCache(pid);
        var after = await registry.GetModelsCachedAsync(pid);

        await Assert.That(after.IsSuccess).IsTrue();
        await Assert.That(client.ModelCalls).IsEqualTo(2);
    }
}
