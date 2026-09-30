// CommonConfigReaderAdapterTests.cs — guard for issue #470, extended by #453.
//
// WHY THIS FILE EXISTS
// --------------------
// `CommonConfigReaderAdapter` projects `ICommonConfigStore`
// (Harbor.Desktop.Abstractions) onto the read-only `ICommonConfigModelRefReader`
// (Ui.Framework), because Ui.Framework cannot reference Desktop.Abstractions
// without a cycle.
//
// It was written as `CommonConfigReaderAdapter(IServiceProvider services)` holding
// `private readonly IServiceProvider _services` and calling
// `_services.GetService<ICommonConfigStore>()` on EVERY read — which
// `SessionFactory` calls on every session creation and every agent-definition
// resolution. One optional dependency, a container kept alive to fetch it, and a
// lookup repeated per call. The adapter's entire job is forwarding to that one
// store, so the container was pure ceremony.
//
// The Avalonia composition root then had to hand the raw container to it:
// `new CommonConfigReaderAdapter(sp)`. A bridge that exists to bridge two
// interfaces should depend on the interface it forwards to.
//
// This file lands BEFORE the fix, per the repo's guard-first rule: the shape test
// below is red against the locator version.
//
// WHAT #453 ADDED, AND WHY THE BEHAVIOUR TESTS CHANGED SHAPE
// -----------------------------------------------------------
// The seam used to hand over `(string? ProviderId, string? ModelId)?` — a pair of
// optional strings inside another optional, spelling four states the type could
// not tell apart — and the adapter carried a private copy of "is this pair
// whole?": `if (IsNullOrEmpty(p) || IsNullOrEmpty(m)) return null;`. It now
// returns `Maybe<ModelRef>` and lets `ModelRef.Qualify` answer that question
// once. So the three behaviour tests below assert a `ModelRef` rather than two
// strings, and two of them are stronger than they were:
//
//   * `Adapter_ForwardsAWholeReference` — the qualified reference reaches the
//     caller, so a redundant `kilocode/` prefix is stripped by the seam rather
//     than reaching a running Session (the #678 win, now enforced at the seam).
//   * `Adapter_ReturnsNone_WhenEitherHalfIsUnusable` — still one state, and it
//     now also covers a provider id that is not a valid id, which the old
//     `IsNullOrEmpty` test would have passed through.
//   * `Adapter_ReturnsNone_WhenStoreFails` — the degradation the pre-#470 adapter
//     provided when `GetService` returned nothing, preserved across both changes.
//
// The shape test cannot use the positive-control trick
// `ServiceLocatorBoundaryRules` uses in Harbor.Architecture.Tests: apps/ are
// composition roots and are deliberately outside that project's reference graph.
// What it does instead is assert the constructor parameter list outright — one
// parameter, typed `ICommonConfigStore` — which is both the requirement and its
// own non-vacuity proof (a container parameter is what used to be there).
//
// A fourth fact is asserted here rather than only in the architecture guard: the
// adapter accepts a CancellationToken and the #453 body honoured it. It used to
// drop it on the floor — `_store.LoadAsync()` took no argument — so cancelling a
// session creation did nothing.

using System.Reflection;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.App.Avalonia.Services;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Ui.Framework.Configuration;
using TUnit;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     #470 — the Avalonia side of the config bridge takes its one collaborator
///     directly instead of holding the container, and forwards exactly as before.
///     #453 — and forwards one qualified <see cref="ModelRef" /> rather than a
///     pair of nullable strings it had to keep whole by hand.
/// </summary>
public sealed class CommonConfigReaderAdapterTests
{
    /// <summary>
    ///     The adapter's constructor takes exactly one parameter, and it is the
    ///     store it forwards to. Before #470 it took an
    ///     <see cref="IServiceProvider" />.
    /// </summary>
    [Test]
    public async Task Adapter_HasNoServiceLocator()
    {
        ParameterInfo[] parameters = typeof(CommonConfigReaderAdapter)
            .GetConstructors().Single().GetParameters();

        await Assert.That(parameters.Length).IsEqualTo(1);
        await Assert.That(parameters[0].ParameterType).IsEqualTo(typeof(ICommonConfigStore));
    }

    /// <summary>
    ///     A config carrying a usable pair comes back as ONE qualified reference.
    ///     The redundant <c>kilocode/</c> prefix is the shape
    ///     <c>HARBOR_MODEL</c> and the settings screen write; the model half on its
    ///     own is what the onboarding wizard writes. Both qualify to the same
    ///     reference, and neither reaches a Session unnormalized.
    /// </summary>
    [Test]
    public async Task Adapter_ForwardsAWholeReference()
    {
        ICommonConfigModelRefReader prefixed = new CommonConfigReaderAdapter(
            new StubCommonConfigStore(new CommonConfig
            {
                DefaultProvider = "kilocode",
                DefaultModel = "kilocode/kilo-auto/free",
            }));
        ICommonConfigModelRefReader bare = new CommonConfigReaderAdapter(
            new StubCommonConfigStore(new CommonConfig
            {
                DefaultProvider = "kilocode",
                DefaultModel = "kilo-auto/free",
            }));

        Maybe<ModelRef> fromPrefixed = await prefixed.ReadModelRefAsync();
        Maybe<ModelRef> fromBare = await bare.ReadModelRefAsync();

        await Assert.That(fromPrefixed.HasValue).IsTrue();
        await Assert.That(fromBare.HasValue).IsTrue();
        await Assert.That(fromPrefixed.Value.ProviderId.Value).IsEqualTo("kilocode");
        await Assert.That(fromPrefixed.Value.ModelId).IsEqualTo("kilo-auto/free");
        await Assert.That(fromBare.Value.ProviderId.Value).IsEqualTo("kilocode");
        await Assert.That(fromBare.Value.ModelId).IsEqualTo("kilo-auto/free");
    }

    /// <summary>
    ///     Half a config is no config, and an unusable provider id is the same
    ///     answer — one state, not three. The pre-#453 test only blanked a half and
    ///     read <c>IsNullOrEmpty</c>; <see cref="ModelRef.Qualify" /> also rejects a
    ///     provider id that is not a valid one, which used to be passed through raw.
    /// </summary>
    [Test]
    public async Task Adapter_ReturnsNone_WhenEitherHalfIsUnusable()
    {
        ICommonConfigModelRefReader noProvider = new CommonConfigReaderAdapter(
            new StubCommonConfigStore(new CommonConfig { DefaultProvider = string.Empty, DefaultModel = "m" }));
        ICommonConfigModelRefReader noModel = new CommonConfigReaderAdapter(
            new StubCommonConfigStore(new CommonConfig { DefaultProvider = "p", DefaultModel = string.Empty }));
        ICommonConfigModelRefReader badProvider = new CommonConfigReaderAdapter(
            new StubCommonConfigStore(new CommonConfig { DefaultProvider = "not a provider!", DefaultModel = "m" }));

        await Assert.That((await noProvider.ReadModelRefAsync()).HasNoValue).IsTrue();
        await Assert.That((await noModel.ReadModelRefAsync()).HasNoValue).IsTrue();
        await Assert.That((await badProvider.ReadModelRefAsync()).HasNoValue).IsTrue();
    }

    /// <summary>
    ///     A store that fails to read answers <c>None</c> rather than throwing —
    ///     the degradation the pre-#470 adapter provided when <c>GetService</c>
    ///     returned nothing, preserved across both changes.
    /// </summary>
    [Test]
    public async Task Adapter_ReturnsNone_WhenStoreFails()
    {
        ICommonConfigModelRefReader adapter = new CommonConfigReaderAdapter(
            new StubCommonConfigStore(failure: "config.json is unreadable"));

        Maybe<ModelRef> reference = await adapter.ReadModelRefAsync();

        await Assert.That(reference.HasNoValue).IsTrue();
    }

    /// <summary>
    ///     #453: the token is honoured end to end. The pre-fix body took a
    ///     CancellationToken and called <c>_store.LoadAsync()</c> with no
    ///     argument, so the one optional dependency on the seam could not
    ///     actually be cancelled.
    /// </summary>
    [Test]
    public async Task Adapter_ForwardsTheCancellationToken()
    {
        var store = new TokenWatchingConfigStore();
        ICommonConfigModelRefReader adapter = new CommonConfigReaderAdapter(store);

        using var cts = new CancellationTokenSource();
        await adapter.ReadModelRefAsync(cts.Token);

        await Assert.That(store.SeenToken).IsEqualTo(cts.Token);
    }

    /// <summary>An <see cref="ICommonConfigStore" /> that answers from a fixture instead of the disk.</summary>
    private sealed class StubCommonConfigStore : ICommonConfigStore
    {
        private readonly CommonConfig? _config;
        private readonly string? _failure;

        internal StubCommonConfigStore(CommonConfig config)
        {
            _config = config;
        }

        internal StubCommonConfigStore(string failure)
        {
            _failure = failure;
        }

        public Task<Result<CommonConfig>> LoadAsync(CancellationToken ct = default)
            => _failure is not null
                ? Task.FromResult(Result.Failure<CommonConfig>(_failure))
                : Task.FromResult(Result.Success(_config!));

        public Task<Result> SaveAsync(CommonConfig config, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<Result> UpdateAsync(Func<CommonConfig, CommonConfig> updater, CancellationToken ct = default)
            => Task.FromResult(Result.Success());
    }

    /// <summary>A store that records the token its caller passed, for <see cref="Adapter_ForwardsTheCancellationToken" />.</summary>
    private sealed class TokenWatchingConfigStore : ICommonConfigStore
    {
        internal CancellationToken SeenToken { get; private set; }

        public Task<Result<CommonConfig>> LoadAsync(CancellationToken ct = default)
        {
            SeenToken = ct;
            return Task.FromResult(Result.Failure<CommonConfig>("no config"));
        }

        public Task<Result> SaveAsync(CommonConfig config, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<Result> UpdateAsync(Func<CommonConfig, CommonConfig> updater, CancellationToken ct = default)
            => Task.FromResult(Result.Success());
    }
}
