using CSharpFunctionalExtensions;
using Harbor.Abstractions.Dsl;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.App.Cli.Commands;
using Harbor.TestKit;
using Harbor.Ui.Framework.Projection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     Issue #3800: the <c>providers</c> verb formats its lines through
///     <see cref="PanelDescriptorProjector" /> instead of writing them in the
///     verb body. These tests pin both the byte-for-byte output and the call
///     into the projector.
/// </summary>
/// <remarks>
///     The bare <c>NotInParallel</c> is load-bearing: the console capture in
///     <see cref="CafVerbTestBase" /> swaps process-wide streams, so a
///     concurrent test would steal the projected lines.
/// </remarks>
[NotInParallel]
public class ProviderListProjectionTests : CafVerbTestBase
{
    [Test]
    public async Task Descriptor_ReturnsSameLinesAsLegacyLoop()
    {
        var ids = new[] { ProviderId.Create("alpha"), ProviderId.Create("beta"), ProviderId.Create("gamma") };
        var registry = new StubProviderRegistry(["alpha", "beta", "gamma"], ["alpha", "gamma"]);
        var state = ProviderListState.From(registry, ids);
        var projector = ProviderListPanelDescriptor.CreateProjector();

        var lines = (IReadOnlyList<string>)projector.Project(state)!;

        // These are the exact strings the former foreach wrote with Console.WriteLine.
        await Assert.That(lines).IsEquivalentTo(new[]
        {
            "Providers (3):",
            "  [OK] alpha",
            "  [FAIL] beta",
            "  [OK] gamma"
        });
    }

    [Test]
    public async Task Descriptor_EmptyRegistry_PrintsHeaderOnly()
    {
        var lines = (IReadOnlyList<string>)ProviderListPanelDescriptor.CreateProjector()
            .Project(new ProviderListState([]))!;

        await Assert.That(lines.Count).IsEqualTo(1);
        await Assert.That(lines[0]).IsEqualTo("Providers (0):");
    }

    [Test]
    public async Task RunListProviders_PrintsProjectedLinesUnchanged()
    {
        var registry = new StubProviderRegistry(["solo"], ["solo"]);
        var projector = ProviderListPanelDescriptor.CreateProjector();

        var (exit, output, error) = await CaptureAsync(() =>
            ProviderVerbs.RunListProvidersAsync(NullLogger.Instance, registry, projector)).ConfigureAwait(false);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(error).IsEmpty();
        await Assert.That(output).IsEqualTo(
            string.Join(Environment.NewLine, "Providers (1):", "  [OK] solo") + Environment.NewLine);
    }

    [Test]
    public async Task RunListProviders_CallsProjectorWithProviderListState()
    {
        var registry = new StubProviderRegistry(["solo"], ["solo"]);
        var chain = new PanelDescriptorRegistry();
        var spy = new SpyDescriptor();
        chain.AddGlobal(spy);
        var projector = new PanelDescriptorProjector(chain);

        var (exit, output, _) = await CaptureAsync(() =>
            ProviderVerbs.RunListProvidersAsync(NullLogger.Instance, registry, projector)).ConfigureAwait(false);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(spy.Calls).IsEqualTo(1);
        await Assert.That(spy.State).IsTypeOf<ProviderListState>();
        await Assert.That(output).IsEqualTo("from-projector" + Environment.NewLine);
    }

    private sealed class StubProviderRegistry(string[] ids, string[] available) : IProviderRegistry
    {
        private readonly HashSet<string> _available = new(available, StringComparer.Ordinal);

        public IReadOnlyList<ProviderId> GetRegisteredProviderIds() =>
            ids.Select(ProviderId.Create).ToArray();

        public Result<ILlmClient> GetClient(ProviderId providerId) =>
            _available.Contains(providerId.Value)
                ? Result.Success<ILlmClient>(new ScriptedLlmClient())
                : Result.Failure<ILlmClient>("no client for " + providerId.Value);

        public Task<Result<IReadOnlyList<ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsCachedAsync(
            ProviderId providerId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void Register(ProviderId providerId, Func<ILlmClient> factory) => throw new NotSupportedException();

        public Result Unregister(ProviderId providerId) => throw new NotSupportedException();
    }

    private sealed class SpyDescriptor : IPanelDescriptor
    {
        internal int Calls;
        internal object? State;

        public Type StateType => typeof(ProviderListState);

        public bool Match(object state) => state is ProviderListState;

        public object? Build(object state)
        {
            Calls++;
            State = state;
            return new[] { "from-projector" };
        }
    }
}
