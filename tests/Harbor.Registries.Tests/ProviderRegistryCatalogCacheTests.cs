using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Microsoft.Extensions.Logging.Abstractions;
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
/// <remarks>
///     <para>
///         <b>TTL ageing (#1878).</b> The cache expiry is read through the
///         registry's injected <see cref="TimeProvider" />, so the tests below
///         age an entry by moving the clock instead of waiting five real
///         minutes. <c>Microsoft.Extensions.TimeProvider.Testing</c> is not
///         pinned in this repository, so the clock is a hand-written
///         <see cref="TimeProvider" /> subclass — BCL only, no new dependency.
///     </para>
/// </remarks>
public class ProviderRegistryCatalogCacheTests
{
    /// <summary>
    ///     Controllable clock (#1878): the catalogue TTL is read through the
    ///     registry's injected <see cref="TimeProvider" />, so a test ages the
    ///     cache by moving this clock instead of sleeping.
    /// </summary>
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan delta) => _now += delta;

        public override DateTimeOffset GetUtcNow() => _now;
    }

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

    // ── #1878: the TTL is read through the injected clock ──

    /// <summary>
    ///     An entry inside its TTL is served from the cache: no second
    ///     round-trip. Before #1878 the expiry was read from
    ///     <c>DateTimeOffset.UtcNow</c>, so this half of the contract could
    ///     only be observed by waiting five real minutes.
    /// </summary>
    [Test]
    public async Task GetModelsCachedAsync_BeforeTtl_ServesFromCache()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance, clock);
        var pid = ProviderId.Create("aged");
        var client = new CountingCatalogClient(
            pid,
            Result.Success<IReadOnlyList<ModelInfo>>(new[] { TestModel("aged", "aged-model") }));
        registry.Register(pid, () => client);

        var first = await registry.GetModelsCachedAsync(pid);

        // One second short of the 5-minute TTL: the entry is still fresh.
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        var second = await registry.GetModelsCachedAsync(pid);

        await Assert.That(first.IsSuccess).IsTrue();
        await Assert.That(second.IsSuccess).IsTrue();
        await Assert.That(client.ModelCalls).IsEqualTo(1)
            .Because("a catalogue entry inside its TTL is served from the cache — no second round-trip");
    }

    /// <summary>
    ///     An entry past its TTL is refetched: the cache must not serve a
    ///     stale catalog past the five-minute window.
    /// </summary>
    [Test]
    public async Task GetModelsCachedAsync_AfterTtl_Refetches()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance, clock);
        var pid = ProviderId.Create("stale");
        var client = new CountingCatalogClient(
            pid,
            Result.Success<IReadOnlyList<ModelInfo>>(new[] { TestModel("stale", "stale-model") }));
        registry.Register(pid, () => client);

        _ = await registry.GetModelsCachedAsync(pid);

        // One second past the 5-minute TTL: the entry has expired.
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        var after = await registry.GetModelsCachedAsync(pid);

        await Assert.That(after.IsSuccess).IsTrue();
        await Assert.That(client.ModelCalls).IsEqualTo(2)
            .Because("past the TTL the catalogue is refetched — the cache must not serve a stale catalog");
    }

    /// <summary>
    ///     The fan-out reads the same TTL through the same clock, and a cache
    ///     miss costs exactly one round-trip per provider — the parallel
    ///     fan-out must not double-fetch, before or after expiry.
    /// </summary>
    [Test]
    public async Task GetAllModelsAsync_AfterTtl_RefetchesOncePerProvider()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance, clock);
        var firstPid = ProviderId.Create("fanout-a");
        var secondPid = ProviderId.Create("fanout-b");
        var firstClient = new CountingCatalogClient(
            firstPid,
            Result.Success<IReadOnlyList<ModelInfo>>(new[] { TestModel("fanout-a", "fanout-a-model") }));
        var secondClient = new CountingCatalogClient(
            secondPid,
            Result.Success<IReadOnlyList<ModelInfo>>(new[] { TestModel("fanout-b", "fanout-b-model") }));
        registry.Register(firstPid, () => firstClient);
        registry.Register(secondPid, () => secondClient);

        var initial = await registry.GetAllModelsAsync();

        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        var afterExpiry = await registry.GetAllModelsAsync();

        await Assert.That(initial.IsSuccess).IsTrue();
        await Assert.That(afterExpiry.IsSuccess).IsTrue();
        await Assert.That(afterExpiry.Value.Count).IsEqualTo(2);
        await Assert.That(firstClient.ModelCalls).IsEqualTo(2)
            .Because("the first fan-out fetched once, the post-TTL fan-out fetched once more — never twice per fan-out");
        await Assert.That(secondClient.ModelCalls).IsEqualTo(2)
            .Because("the first fan-out fetched once, the post-TTL fan-out fetched once more — never twice per fan-out");
    }
}
