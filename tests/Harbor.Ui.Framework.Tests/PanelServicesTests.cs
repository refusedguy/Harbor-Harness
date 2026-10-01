using System.Reflection;
using Harbor.Abstractions.Git;
using Harbor.Ui.Framework.Diagnostics;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     #470 — <see cref="PanelServices" /> is the explicit replacement for the
///     <c>IServiceProvider</c> that used to ride in <see cref="PanelContext" /> on
///     every frame. These tests pin the two properties every panel depends on: the
///     bag is never null, and a field is non-null exactly when the host registered
///     that service. The "is it actually registered in the composition root" half
///     lives in <c>Harbor.Hosting.Tests.PanelServicesCompositionTests</c>, which
///     composes a real container.
/// </summary>
public class PanelServicesTests
{
    private static PanelContext Ctx(PanelServices? services) =>
        new(new UiState(), 80, 24, services);

    /// <summary>A context built without a bag still hands the panel a usable (empty) one.</summary>
    [Test]
    public async Task PanelContext_WithoutServices_ExposesEmptyBag()
    {
        PanelServices deps = Ctx(null).Deps;

        await Assert.That(deps).IsNotNull();
        await Assert.That(deps).IsSameReferenceAs(PanelServices.Empty);
        await Assert.That(deps.Store).IsNull();
        await Assert.That(deps.PanelRegistry).IsNull();
        await Assert.That(deps.Diagnostics).IsNull();
        await Assert.That(deps.SessionStore).IsNull();
        await Assert.That(deps.Sessions).IsNull();
    }

    /// <summary>The per-frame contract must not carry a container any more.</summary>
    [Test]
    public async Task PanelContext_ExposesNoServiceProvider()
    {
        bool IsLocator(Type candidate) =>
            candidate == typeof(IServiceProvider) || typeof(IServiceProvider).IsAssignableFrom(candidate);

        var locatorParams = typeof(PanelContext)
            .GetConstructors().Single().GetParameters()
            .Where(p => IsLocator(p.ParameterType))
            .Select(p => p.Name ?? p.ParameterType.Name)
            .ToArray();

        var locatorProperties = typeof(PanelContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => IsLocator(p.PropertyType))
            .Select(p => p.Name)
            .ToArray();

        await Assert.That(locatorParams).IsEmpty();
        await Assert.That(locatorProperties).IsEmpty();
    }

    /// <summary>
    ///     The composition-root projection is total: every registered service lands
    ///     in its field, and a field whose service was never registered is null
    ///     rather than an exception. That is the difference between "the host did
    ///     not offer it" and "the panel will NRE on a later frame".
    /// </summary>
    [Test]
    public async Task FromContainer_FillsRegisteredServices_AndNullsTheRest()
    {
        var store = new UiStore();
        var registry = new PanelRegistry();
        var diagnostics = new InMemoryDiagnosticsPanel();
        var sessions = new StubGateway();
        var git = new StubGitQuery();
        var container = new StubContainer()
            .Add<UiStore>(store)
            .Add<IPanelRegistry>(registry)
            .Add<IDiagnosticsPanel>(diagnostics)
            .Add<IPanelSessionGateway>(sessions)
            .Add<IGitQuery>(git);

        PanelServices deps = PanelServices.FromContainer(container);

        await Assert.That(deps.Store).IsSameReferenceAs(store);
        await Assert.That(deps.PanelRegistry).IsSameReferenceAs(registry);
        await Assert.That(deps.Diagnostics).IsSameReferenceAs(diagnostics);
        await Assert.That(deps.Sessions).IsSameReferenceAs(sessions);
        // #666 added the Git field; registering it here keeps this test's claim
        // ("every registered service lands in its field") true of the bag as it
        // is now, rather than true of a bag that no longer exists.
        await Assert.That(deps.Git).IsSameReferenceAs(git);
        // ISessionStore was never registered above → honestly null, no throw.
        await Assert.That(deps.SessionStore).IsNull();
    }

    /// <summary>An empty container degrades every field to null — no exception, no lie.</summary>
    [Test]
    public async Task FromContainer_EmptyContainer_YieldsAllNull()
    {
        PanelServices deps = PanelServices.FromContainer(new StubContainer());

        await Assert.That(deps.Store).IsNull();
        await Assert.That(deps.PanelRegistry).IsNull();
        await Assert.That(deps.Diagnostics).IsNull();
        await Assert.That(deps.SessionStore).IsNull();
        await Assert.That(deps.Sessions).IsNull();
        await Assert.That(deps.Git).IsNull();
    }

    /// <summary>
    ///     <see cref="PanelServices.WithStore" /> is the per-session rebinding hook:
    ///     it swaps the store without touching the rest of the bag.
    /// </summary>
    [Test]
    public async Task WithStore_ReplacesOnlyTheStore()
    {
        var original = new UiStore();
        var replacement = new UiStore();
        var registry = new PanelRegistry();
        var deps = new PanelServices { Store = original, PanelRegistry = registry };

        PanelServices rebound = deps.WithStore(replacement);

        await Assert.That(rebound.Store).IsSameReferenceAs(replacement);
        await Assert.That(rebound.PanelRegistry).IsSameReferenceAs(registry);
        await Assert.That(deps.Store).IsSameReferenceAs(original);
    }

    /// <summary>A null container is a caller bug, not a degraded panel.</summary>
    [Test]
    public async Task FromContainer_Null_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            PanelServices.FromContainer(null!);
            return Task.CompletedTask;
        });
    }

    /// <summary>Minimal <see cref="IServiceProvider" /> — the panel test project takes no DI package.</summary>
    private sealed class StubContainer : IServiceProvider
    {
        private readonly Dictionary<Type, object> _map = new();

        public StubContainer Add<T>(T instance) where T : class
        {
            _map[typeof(T)] = instance;
            return this;
        }

        public object? GetService(Type serviceType) =>
            _map.TryGetValue(serviceType, out object? value) ? value : null;
    }

    private sealed class StubGateway : IPanelSessionGateway
    {
        public string? GetDirectory(string sessionId) => null;

        public string? GetStatusText(string sessionId) => null;

        public string? GetBranch(string sessionId) => null;

        public bool GetIsDirty(string sessionId) => false;

        public bool? GetIsSubagent(string sessionId) => null;

        public Task<bool> OpenPanelSessionAsync(string sessionId) => Task.FromResult(false);
    }

    /// <summary>
    ///     #666 added <see cref="IGitQuery" /> to the bag for the jump palette. This
    ///     bag's test only cares that the projection reaches the container, so the
    ///     double answers with nothing and is identified by reference.
    /// </summary>
    private sealed class StubGitQuery : IGitQuery
    {
        public IReadOnlyList<GitWorktreeInfo> ListWorktrees(
            string directory,
            CancellationToken cancellationToken = default)
            => Array.Empty<GitWorktreeInfo>();

        public GitWorkspaceStatus GetStatus(string directory, CancellationToken cancellationToken = default)
            => GitWorkspaceStatus.None;
    }
}
