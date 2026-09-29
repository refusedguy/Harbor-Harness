using CSharpFunctionalExtensions;
using Harbor.Abstractions.Sessions;
using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Runtime.Tests.TestSupport;
using Harbor.Terminal.Abstractions;

namespace Harbor.Plugins.Runtime.Tests;

/// <summary>
///     Issue #581, finding 1: <c>IPluginLoadHost</c> is the one file that enumerates
///     "what can a plugin extend", and it listed five facets — tool, provider, agent, TUI
///     plugin, panel. Storage and TUI renderer had no method at all, and their extension
///     interfaces (<c>ISessionStoreFactory</c>, <c>ITuiRendererFactory</c>) were
///     <c>internal</c>, so the seam was alive and sealed: an error message could name a
///     backend nobody outside <c>Harbor.Hosting</c> could implement.
/// </summary>
/// <remarks>
///     <para>
///         These tests pin the axis being open at the contract level, so a future
///         <c>IPluginLoadHost</c> refactor that drops a door (or makes it refuse) fails here
///         rather than in a plugin author's build. The host-side consumer of these maps — the
///         <c>StorageModule</c> / <c>TuiModule</c> registries — is covered separately by
///         <c>Harbor.Hosting.Tests.BackendRegistrySingleSourceTests</c>.
///     </para>
///     <para>
///         Every factory below returns <c>null!</c> on purpose. The doors store the
///         delegate and never invoke it, so a regression that started constructing the
///         backend eagerly would fail these tests with a NullReferenceException rather than
///         quietly paying a construction cost nobody asked for.
///     </para>
/// </remarks>
public sealed class PluginBackendDoorTests
{
    /// <summary>
    ///     The five pre-existing facets, plus the two added in #581. Named explicitly
    ///     because the whole defect was an omission nobody could see: the list compiled, it
    ///     was just two entries short of the truth.
    /// </summary>
    private static readonly string[] ExpectedFacets =
    [
        nameof(IPluginLoadHost.RegisterTool),
        nameof(IPluginLoadHost.RegisterProvider),
        nameof(IPluginLoadHost.RegisterAgent),
        nameof(IPluginLoadHost.RegisterTuiPlugin),
        nameof(IPluginLoadHost.RegisterPanelProvider),
        nameof(IPluginLoadHost.RegisterSessionStore),
        nameof(IPluginLoadHost.RegisterTuiBackend),
    ];

    private static Func<ISessionStore> UnusedStore => static () => null!;

    private static Func<ITuiRenderer> UnusedRenderer => static () => null!;

    [Test]
    public async Task LoadHost_ExposesEveryRegistrationFacet()
    {
        var facets = typeof(IPluginLoadHost)
            .GetMethods()
            .Where(m => m.Name.StartsWith("Register", StringComparison.Ordinal))
            .Select(m => m.Name)
            .ToArray();

        foreach (string facet in ExpectedFacets)
        {
            await Assert.That(facets).Contains(facet);
        }
    }

    [Test]
    public async Task RegisterSessionStore_StoresUnderTheGivenId_AndRejectsDuplicates()
    {
        var host = new FakePluginLoadHost();

        Result first = host.RegisterSessionStore("redis", UnusedStore);
        Result second = host.RegisterSessionStore("redis", UnusedStore);

        await Assert.That(first.IsSuccess).IsTrue();
        await Assert.That(second.IsFailure).IsTrue();
        await Assert.That(host.RegisteredSessionStores).Contains("redis");
    }

    [Test]
    public async Task RegisterSessionStore_RejectsAnEmptyId()
    {
        var host = new FakePluginLoadHost();

        Result result = host.RegisterSessionStore("   ", UnusedStore);

        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task RegisterTuiBackend_StoresUnderTheGivenId_AndRejectsDuplicates()
    {
        var host = new FakePluginLoadHost();

        Result first = host.RegisterTuiBackend("web", new[] { "webui" }, UnusedRenderer);
        Result second = host.RegisterTuiBackend("web", new[] { "webui" }, UnusedRenderer);

        await Assert.That(first.IsSuccess).IsTrue();
        await Assert.That(second.IsFailure).IsTrue();
        await Assert.That(host.RegisteredTuiBackends).Contains("web");
    }

    [Test]
    public async Task RegisterTuiBackend_RejectsAnEmptyId()
    {
        var host = new FakePluginLoadHost();

        Result result = host.RegisterTuiBackend(string.Empty, Array.Empty<string>(), UnusedRenderer);

        await Assert.That(result.IsFailure).IsTrue();
    }
}
