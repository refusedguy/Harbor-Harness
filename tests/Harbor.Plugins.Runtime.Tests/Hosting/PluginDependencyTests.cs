using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Compilation;
using Harbor.Plugins.Hosting;
using Harbor.Plugins.Instantiation;
using Harbor.Plugins.Registration;
using Harbor.Plugins.Runtime.Tests.TestSupport;
using Harbor.Plugins.Storage;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Plugins.Runtime.Tests.Hosting;

/// <summary>
///     Deferred activation (#1733): a plugin declares what it needs, and the host holds it
///     back — registered, not activated — until the dependency is met.
/// </summary>
/// <remarks>
///     The four shapes the issue names are the four things worth proving: a dependency that
///     is not ready, a dependency that arrives, a plugin that declares nothing, and a cycle.
/// </remarks>
public sealed class PluginDependencyTests
{
    /// <summary>
    ///     A dependency that is not ready does not fail the load: the plugin is loaded and
    ///     KNOWN, but nothing was initialized and nothing was contributed. The user sees it
    ///     in the host's inventory and in the log.
    /// </summary>
    [Test]
    public async Task Plugin_WithUnmetDependency_IsPendingAndContributesNothing()
    {
        using var fixture = await PluginTestFixture.CreateAsync("dep-wait").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        var logger = new CapturingLogger<PluginHost>();
        var resolver = new SwitchableResolver();
        var source = new InMemoryPluginSource(
            new[] { new PluginScript("dep.cs", SamplePluginSource.WithDependencies("Wait", "dep-wait", "mcp-session")) });

        var pluginHost = BuildHost(fixture, source, resolver, logger);
        var result = await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        await Assert.That(result.IsSuccess).IsTrue()
            .Because("a plugin waiting for a dependency is not a failure — the load continues");
        await Assert.That(result.Value.Count).IsEqualTo(0)
            .Because("a pending plugin is not activated, so it is not in the active list");
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(0)
            .Because("the plugin was not activated, so it contributed no tool");
        await Assert.That(pluginHost.PendingPlugins.Count).IsEqualTo(1);
        await Assert.That(pluginHost.PendingPlugins[0].Name).IsEqualTo("dep-wait");
        await Assert.That(pluginHost.PendingPlugins[0].UnsatisfiedDependencies).Contains("mcp-session");
        await Assert.That(logger.Contains("registered but not activated")).IsTrue()
            .Because("the state has to be visible — a plugin that waits is a fact, not a silence");
    }

    /// <summary>
    ///     The dependency arrives: the next activation pass brings the plugin up and its
    ///     contribution lands.
    /// </summary>
    [Test]
    public async Task Plugin_WhoseDependencyAppears_IsActivatedOnTheNextPass()
    {
        using var fixture = await PluginTestFixture.CreateAsync("dep-arrive").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        var logger = new CapturingLogger<PluginHost>();
        var resolver = new SwitchableResolver();
        var source = new InMemoryPluginSource(
            new[] { new PluginScript("dep.cs", SamplePluginSource.WithDependencies("Arrive", "dep-arrive", "mcp-session")) });

        var pluginHost = BuildHost(fixture, source, resolver, logger);
        await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        await Assert.That(host.RegisteredTools.Count).IsEqualTo(0)
            .Because("the dependency is not ready yet, so the plugin stays inactive");

        resolver.Satisfy("mcp-session");
        int activated = pluginHost.ActivatePending(host);

        await Assert.That(activated).IsEqualTo(1)
            .Because("the dependency is met now, so the plugin activates");
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(1)
            .Because("an activated plugin contributes what it always contributed");
        await Assert.That(pluginHost.PendingPlugins.Count).IsEqualTo(0);
        await Assert.That(pluginHost.ActivePlugins.Count).IsEqualTo(1);
    }

    /// <summary>
    ///     A plugin that declares nothing behaves exactly as it did before: it activates
    ///     during the load, and the resolver is never even asked.
    /// </summary>
    [Test]
    public async Task Plugin_WithoutDependencies_IsActivatedLikeBefore()
    {
        using var fixture = await PluginTestFixture.CreateAsync("dep-none").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        var logger = new CapturingLogger<PluginHost>();
        var resolver = new SwitchableResolver();
        var source = new InMemoryPluginSource(
            new[] { new PluginScript("dep.cs", SamplePluginSource.WithDependencies("None", "dep-none")) });

        var pluginHost = BuildHost(fixture, source, resolver, logger);
        var result = await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(1);
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(1);
        await Assert.That(pluginHost.PendingPlugins.Count).IsEqualTo(0);
        await Assert.That(resolver.CallCount).IsEqualTo(0)
            .Because("a plugin with no dependencies needs no resolution at all");
    }

    /// <summary>
    ///     A dependency on ANOTHER PLUGIN's name: the consumer waits for the plugin it
    ///     names and comes up with it, even when the consumer was loaded FIRST.
    /// </summary>
    [Test]
    public async Task Plugin_DependingOnAnotherPlugin_ActivatesWithIt()
    {
        using var fixture = await PluginTestFixture.CreateAsync("dep-peer").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        var logger = new CapturingLogger<PluginHost>();
        var resolver = new SwitchableResolver();
        var source = new InMemoryPluginSource(
            new[]
            {
                // The consumer is loaded FIRST on purpose: it has to wait for the plugin
                // that comes after it.
                new PluginScript("consumer.cs", SamplePluginSource.WithDependencies("Consumer", "dep-consumer", "dep-provider")),
                new PluginScript("provider.cs", SamplePluginSource.WithDependencies("Provider", "dep-provider"))
            });

        var pluginHost = BuildHost(fixture, source, resolver, logger);
        var result = await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(2)
            .Because("the consumer waits for the plugin it names, and the load's own activation "
                   + "pass brings it up once the provider is active");
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(2);
        await Assert.That(pluginHost.PendingPlugins.Count).IsEqualTo(0);
    }

    /// <summary>
    ///     Two plugins that require EACH OTHER can never both activate. The load must
    ///     return — no hang — and the cycle must be reported instead of waited on silently.
    /// </summary>
    [Test]
    public async Task Plugins_ThatRequireEachOther_StayPendingAndTheLoadReturns()
    {
        using var fixture = await PluginTestFixture.CreateAsync("dep-cycle").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        var logger = new CapturingLogger<PluginHost>();
        var resolver = new SwitchableResolver();
        var source = new InMemoryPluginSource(
            new[]
            {
                new PluginScript("a.cs", SamplePluginSource.WithDependencies("CycleA", "dep-cycle-a", "dep-cycle-b")),
                new PluginScript("b.cs", SamplePluginSource.WithDependencies("CycleB", "dep-cycle-b", "dep-cycle-a"))
            });

        var pluginHost = BuildHost(fixture, source, resolver, logger);
        var result = await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        await Assert.That(result.IsSuccess).IsTrue()
            .Because("a cycle is not a load failure — it is two plugins that wait for each other");
        await Assert.That(result.Value.Count).IsEqualTo(0);
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(0);
        await Assert.That(pluginHost.PendingPlugins.Count).IsEqualTo(2);

        int activated = pluginHost.ActivatePending(host);

        await Assert.That(activated).IsEqualTo(0)
            .Because("no member of the cycle can go first — the pass terminates instead of waiting");
        await Assert.That(pluginHost.PendingPlugins.Count).IsEqualTo(2);
        await Assert.That(logger.Contains("circular dependency")).IsTrue()
            .Because("the reason a plugin never comes up has to be visible");
    }

    private static PluginHost BuildHost(
        PluginTestFixture fixture,
        IPluginSource source,
        IPluginDependencyResolver? dependencies,
        CapturingLogger<PluginHost> logger)
    {
        var references = new PluginAssemblyReferences(
            NullLogger<PluginAssemblyReferences>.Instance);

        return new PluginHostBuilder()
            .WithSource(source)
            .WithCompiler(new CachingCompiler(
                new RoslynPluginCompiler(references),
                fixture.CacheDir,
                NullLogger<CachingCompiler>.Instance))
            .WithInstantiator(new ReflectionPluginInstantiator())
            .WithRegistrar(new SafePluginRegistrar(
                new PluginRegistrar(fixture.PluginsDir, NullLogger<PluginRegistrar>.Instance, NullLoggerFactory.Instance),
                NullLogger.Instance))
            .WithOptions(o =>
            {
                o.PluginRoot = fixture.PluginsDir;
                o.Dependencies = dependencies;
            })
            .Build(logger);
    }

    /// <summary>
    ///     A resolver whose answers the test controls: a name is satisfied only after the
    ///     test says so.
    /// </summary>
    private sealed class SwitchableResolver : IPluginDependencyResolver
    {
        private readonly HashSet<string> _satisfied = new(StringComparer.Ordinal);

        /// <summary>How many times the host asked about a dependency.</summary>
        public int CallCount { get; private set; }

        /// <inheritdoc />
        public bool IsSatisfied(string name)
        {
            CallCount++;
            return _satisfied.Contains(name);
        }

        /// <summary>Make a dependency name satisfied from now on.</summary>
        public void Satisfy(string name) => _satisfied.Add(name);
    }
}
