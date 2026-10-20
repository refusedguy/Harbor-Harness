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
///     Issue #1878, cross-path coverage: the model-catalog TTL belongs to
///     the shared entry, not to the call path that happened to write it.
/// </summary>
/// <remarks>
///     <para>
///         The seam itself — <see cref="ProviderRegistry" /> reading its TTL
///         through an injected <see cref="TimeProvider" /> — and the
///         single-path expiry are covered in
///         <see cref="ProviderRegistryCatalogCacheTests" /> (#1891). What is
///         covered here is the part that one path at a time cannot see: the
///         fan-out and the single-provider call share ONE cache, so an entry
///         written by one of them is served, and expires, for the other.
///     </para>
///     <para>
///         <b>Why a hand-rolled clock.</b> <see cref="TimeProvider" /> is BCL
///         since .NET 8 and the repository already carries several hand-rolled
///         fakes of it. <c>Microsoft.Extensions.TimeProvider.Testing</c> would
///         add a centrally-pinned dependency to buy an <c>Advance()</c> method
///         this file is a dozen lines wide without.
///     </para>
///     <para>
///         <b>Why the clock starts in the past.</b> The fake starts in 2001,
///         far from the wall clock. An entry written through the seam but
///         compared against <c>DateTimeOffset.UtcNow</c> would read as stale
///         in 2001 and refetch forever, so the tests below would fail rather
///         than pass by accident.
///     </para>
/// </remarks>
public class ProviderRegistryCatalogCacheTtlTests
{
    /// <summary>
    ///     The registry's catalog window, mirrored from the production
    ///     constant, so the boundary tests move against the window rather
    ///     than against a number they repeat.
    /// </summary>
    private static readonly TimeSpan CatalogWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     A clock the test owns; the registry reads it instead of the wall
    ///     clock. The tick is swapped atomically because the fan-out reads it
    ///     from several tasks at once.
    /// </summary>
    private sealed class CatalogClock(DateTimeOffset start) : TimeProvider
    {
        private long _utcTicks = start.UtcTicks;

        public void Advance(TimeSpan delta) => Interlocked.Add(ref _utcTicks, delta.Ticks);

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            NoopTimer.Instance;

        private sealed class NoopTimer : ITimer
        {
            public static readonly NoopTimer Instance = new();
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() { }
            public ValueTask DisposeAsync() => default;
        }
    }

    /// <summary>
    ///     A provider whose catalog call is counted and which hands out a
    ///     different catalog on every call, so a test can see WHICH catalog
    ///     was served and not only how many times it asked.
    /// </summary>
    private sealed class CatalogSource : ILlmClient
    {
        private readonly Queue<ModelInfo[]> _catalogs;
        private int _roundTrips;

        public CatalogSource(ProviderId providerId, params ModelInfo[][] catalogs)
        {
            ProviderId = providerId;
            _catalogs = new Queue<ModelInfo[]>(catalogs);
        }

        public ProviderId ProviderId { get; }

        public int RoundTrips => Volatile.Read(ref _roundTrips);

        public async IAsyncEnumerable<LlmEvent> StreamAsync(
            LlmRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _roundTrips);
            ModelInfo[] catalog = _catalogs.Count > 1 ? _catalogs.Dequeue() : _catalogs.Peek();
            return Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>(catalog));
        }
    }

    private static ModelInfo Model(string id) =>
        new(id, "ttl-provider", id, 128_000, 4096, false, false, false, Pricing.Unknown, "openai");

    private static ProviderRegistry RegistryFor(CatalogClock clock, ProviderId pid, CatalogSource source)
    {
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance, clock);
        registry.Register(pid, () => source);
        return registry;
    }

    /// <summary>
    ///     An entry the fan-out wrote is served to the single-provider call
    ///     without a second round-trip: one dataset, one freshness story, in
    ///     that direction too. The pre-#1108 bug was two caches, and this is
    ///     the half of it the single-path tests cannot see.
    /// </summary>
    [Test]
    public async Task GetModelsCachedAsync_AfterTheFanOutPopulatedTheCache_ReusesTheEntry()
    {
        var clock = new CatalogClock(new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero));
        var pid = ProviderId.Create("ttl-provider");
        var source = new CatalogSource(pid, new[] { Model("from-fanout") });
        var registry = RegistryFor(clock, pid, source);

        var fanOut = await registry.GetAllModelsAsync();
        var single = await registry.GetModelsCachedAsync(pid);

        await Assert.That(fanOut.IsSuccess).IsTrue();
        await Assert.That(single.IsSuccess).IsTrue();
        await Assert.That(single.Value[0].Id).IsEqualTo("from-fanout");
        await Assert.That(source.RoundTrips).IsEqualTo(1)
            .Because("the fan-out wrote the shared entry, so the single-provider call must reuse it");
    }

    /// <summary>
    ///     The window belongs to the shared entry, so an entry the fan-out
    ///     wrote expires for the single-provider reader as well — and the
    ///     catalog that reader gets is the refetched one, not the stale one.
    /// </summary>
    [Test]
    public async Task GetModelsCachedAsync_AfterTheFanOutWroteTheEntry_PastTheWindow_ServesTheRefetchedCatalog()
    {
        var clock = new CatalogClock(new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero));
        var pid = ProviderId.Create("ttl-provider");
        var source = new CatalogSource(pid, new[] { Model("stale") }, new[] { Model("fresh") });
        var registry = RegistryFor(clock, pid, source);

        _ = await registry.GetAllModelsAsync();
        clock.Advance(CatalogWindow + TimeSpan.FromSeconds(1));
        var single = await registry.GetModelsCachedAsync(pid);

        await Assert.That(single.IsSuccess).IsTrue();
        await Assert.That(single.Value[0].Id).IsEqualTo("fresh")
            .Because("the window belongs to the shared dataset, so the single-provider reader refetches too");
        await Assert.That(source.RoundTrips).IsEqualTo(2);
    }

    /// <summary>
    ///     The mirror: an entry the single-provider path wrote expires for the
    ///     fan-out as well, and the fan-out serves the refetched catalog.
    /// </summary>
    [Test]
    public async Task GetAllModelsAsync_AfterTheSingleProviderPathWroteTheEntry_PastTheWindow_ServesTheRefetchedCatalog()
    {
        var clock = new CatalogClock(new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero));
        var pid = ProviderId.Create("ttl-provider");
        var source = new CatalogSource(pid, new[] { Model("stale") }, new[] { Model("fresh") });
        var registry = RegistryFor(clock, pid, source);

        _ = await registry.GetModelsCachedAsync(pid);
        clock.Advance(CatalogWindow + TimeSpan.FromSeconds(1));
        var fanOut = await registry.GetAllModelsAsync();

        await Assert.That(fanOut.IsSuccess).IsTrue();
        await Assert.That(fanOut.Value[0].Id).IsEqualTo("fresh")
            .Because("the window belongs to the shared dataset, so the fan-out refetches too");
        await Assert.That(source.RoundTrips).IsEqualTo(2);
    }
}
