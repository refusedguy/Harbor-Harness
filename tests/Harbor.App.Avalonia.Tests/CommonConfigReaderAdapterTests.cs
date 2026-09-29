// CommonConfigReaderAdapterTests.cs — guard for issue #470.
//
// WHY THIS FILE EXISTS
// --------------------
// `CommonConfigReaderAdapter` bridges `ICommonConfigStore`
// (Harbor.Desktop.Abstractions) to `ICommonConfigReader` (Ui.Framework), because
// Ui.Framework cannot reference Desktop.Abstractions without a cycle.
//
// It was written as `CommonConfigReaderAdapter(IServiceProvider services)` holding
// `private readonly IServiceProvider _services` and calling
// `_services.GetService<ICommonConfigStore>()` on EVERY
// `TryReadProviderModelAsync` — which `SessionFactory` calls on every session
// creation and every agent-definition resolution. One optional dependency, a
// container kept alive to fetch it, and a lookup repeated per call. The adapter's
// entire job is forwarding to that one store, so the container was pure ceremony.
//
// The Avalonia composition root then had to hand the raw container to it:
// `new CommonConfigReaderAdapter(sp)`. A bridge that exists to bridge two
// interfaces should depend on the interface it forwards to.
//
// This file lands BEFORE the fix, per the repo's guard-first rule: the shape test
// below is red against the locator version.
//
// THE SHAPE TEST IS THE POINT
// ---------------------------
// `Adapter_HasNoServiceLocator` is the only test here that would have caught the
// defect. The other three exist so the refactor cannot quietly change behaviour
// while removing the container: the forward, the "nothing persisted yet" answer and
// the "store failed" answer must all survive verbatim.
//
// The shape test cannot use the positive-control trick
// `ServiceLocatorBoundaryRules` uses in Harbor.Architecture.Tests: apps/ are
// composition roots and are deliberately outside that project's reference graph.
// What it does instead is assert the constructor parameter list outright — one
// parameter, typed `ICommonConfigStore` — which is both the requirement and its
// own non-vacuity proof (a container parameter is what used to be there).

using System.Reflection;
using CSharpFunctionalExtensions;
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
    ///     A config carrying both halves is forwarded verbatim as
    ///     (<c>ProviderId</c>, <c>ModelId</c>).
    /// </summary>
    [Test]
    public async Task Adapter_ForwardsProviderAndModel()
    {
        ICommonConfigReader adapter = new CommonConfigReaderAdapter(
            new StubCommonConfigStore(new CommonConfig
            {
                DefaultProvider = "kilocode",
                DefaultModel = "kilo-auto/free",
            }));

        var pair = await adapter.TryReadProviderModelAsync();

        await Assert.That(pair.HasValue).IsTrue();
        await Assert.That(pair!.Value.ProviderId).IsEqualTo("kilocode");
        await Assert.That(pair!.Value.ModelId).IsEqualTo("kilo-auto/free");
    }

    /// <summary>
    ///     Half a config is no config: a blank provider or model answers
    ///     <c>null</c>, which is what <c>SessionFactory</c> reads as "no override".
    /// </summary>
    [Test]
    public async Task Adapter_ReturnsNull_WhenEitherHalfIsUnset()
    {
        ICommonConfigReader noProvider = new CommonConfigReaderAdapter(
            new StubCommonConfigStore(new CommonConfig { DefaultProvider = string.Empty, DefaultModel = "m" }));
        ICommonConfigReader noModel = new CommonConfigReaderAdapter(
            new StubCommonConfigStore(new CommonConfig { DefaultProvider = "p", DefaultModel = string.Empty }));

        await Assert.That((await noProvider.TryReadProviderModelAsync()).HasValue).IsFalse();
        await Assert.That((await noModel.TryReadProviderModelAsync()).HasValue).IsFalse();
    }

    /// <summary>
    ///     A store that fails to read answers <c>null</c> rather than throwing —
    ///     the degradation the pre-#470 adapter provided when
    ///     <c>GetService</c> returned nothing, preserved across the change.
    /// </summary>
    [Test]
    public async Task Adapter_ReturnsNull_WhenStoreFails()
    {
        ICommonConfigReader adapter = new CommonConfigReaderAdapter(
            new StubCommonConfigStore(failure: "config.json is unreadable"));

        var pair = await adapter.TryReadProviderModelAsync();

        await Assert.That(pair.HasValue).IsFalse();
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
}
