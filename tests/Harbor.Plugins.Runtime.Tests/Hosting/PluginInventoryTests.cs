using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Compilation;
using Harbor.Plugins.Hosting;
using Harbor.Plugins.Instantiation;
using Harbor.Plugins.Registration;
using Harbor.Plugins.Runtime.Tests.TestSupport;
using Harbor.Plugins.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Plugins.Runtime.Tests.Hosting;

/// <summary>
///     The live plugin composition (#1722): a machine-readable snapshot of what the
///     host loaded, what phase each plugin is in, which axes it carries and what
///     it registered.
/// </summary>
/// <remarks>
///     <para>
///         The three questions worth proving are the three a user asks when a
///         tool does not show up: did the plugin COMPILE, did it REGISTER, and
///         what did the host REFUSE. The snapshot answers all three, and it is
///         a projection of the state the host already keeps — so the phase in
///         the snapshot is the phase the host gave the plugin.
///     </para>
/// </remarks>
public sealed class PluginInventoryTests
{
    /// <summary>
    ///     A plugin that loaded is ACTIVE, carries the axis it implements, and its
    ///     registration is recorded: the door, the subject and the answer.
    /// </summary>
    [Test]
    public async Task Inventory_AfterALoad_SaysThePluginIsActiveAndCarriesItsAxis()
    {
        using var fixture = await PluginTestFixture.CreateAsync("inv-active").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        var source = new InMemoryPluginSource(
            new[] { new PluginScript("inv.cs", SamplePluginSource.HelloWorld("InvActive")) });

        var pluginHost = BuildHost(fixture, source);
        await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        PluginInventory inventory = pluginHost.Inventory;
        await Assert.That(inventory.Entries.Count).IsEqualTo(1);

        PluginInventoryEntry entry = inventory.Entries[0];
        await Assert.That(entry.Phase).IsEqualTo(PluginInventoryPhase.Active)
            .Because("a plugin the host registered is active — the snapshot is a projection of the host's own state");
        await Assert.That(entry.Name).IsEqualTo("hello-world-invactive");
        await Assert.That(entry.Version).IsEqualTo("1.0.0");
        await Assert.That(entry.Axes).Contains("IToolPlugin")
            .Because("the axes come from the one marker table the registrar dispatches on");
        await Assert.That(entry.FailureReason).IsNull();
        await Assert.That(entry.Registrations.Count).IsEqualTo(1)
            .Because("the plugin registered one tool, so the host saw one door call");

        PluginInventoryRegistration registration = entry.Registrations[0];
        await Assert.That(registration.Door).IsEqualTo("RegisterTool");
        await Assert.That(registration.Outcome).IsEqualTo(PluginInventoryOutcome.Registered);
        await Assert.That(registration.Subject).IsEqualTo("hello_invactive")
            .Because("the snapshot says WHAT was registered, not only that something was");
        await Assert.That(registration.Reason).IsNull();
    }

    /// <summary>
    ///     A plugin that waits for a dependency is PENDING, and the snapshot names
    ///     the dependency it is waiting for (#1733).
    /// </summary>
    [Test]
    public async Task Inventory_SaysAPluginWaitingForADependency_IsPending()
    {
        using var fixture = await PluginTestFixture.CreateAsync("inv-pending").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        var source = new InMemoryPluginSource(
            new[] { new PluginScript("inv.cs", SamplePluginSource.WithDependencies("InvPending", "inv-pending", "mcp-session")) });

        var pluginHost = BuildHost(fixture, source, new SwitchableResolver());
        await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        PluginInventoryEntry entry = pluginHost.Inventory.Entries[0];
        await Assert.That(entry.Phase).IsEqualTo(PluginInventoryPhase.Pending)
            .Because("a plugin that waits is loaded but not activated, and the snapshot has to say which");
        await Assert.That(entry.UnsatisfiedDependencies).Contains("mcp-session");
        await Assert.That(entry.Dependencies).Contains("mcp-session");
        await Assert.That(entry.Registrations.Count).IsEqualTo(0)
            .Because("a pending plugin contributes nothing, so no door was called for it");
    }

    /// <summary>
    ///     A plugin that did not compile is FAILED, and the snapshot carries the
    ///     reason the host refused it — the answer to "why is my tool not there".
    /// </summary>
    [Test]
    public async Task Inventory_SaysAPluginThatDidNotCompile_IsFailedAndSaysWhy()
    {
        using var fixture = await PluginTestFixture.CreateAsync("inv-broken").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        var source = new InMemoryPluginSource(
            new[] { new PluginScript("broken.cs", SamplePluginSource.Broken()) });

        var pluginHost = BuildHost(fixture, source);
        await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        PluginInventoryEntry entry = pluginHost.Inventory.Entries[0];
        await Assert.That(entry.Phase).IsEqualTo(PluginInventoryPhase.Failed);
        await Assert.That(entry.FailureReason).IsNotNull()
            .Because("the whole point of the snapshot is that the reason is not in a log line any more");
        await Assert.That(entry.SourcePath).IsEqualTo("broken.cs")
            .Because("the snapshot names the file, so the reader knows which plugin failed");
        await Assert.That(entry.Name).IsEqualTo(string.Empty)
            .Because("a plugin that never compiled never reported a name");
    }

    /// <summary>
    ///     The document carries the DECLARED half of the composition — the axes and
    ///     the per-route verdicts — read out of the registries the product
    ///     declares them in, and not copied here.
    /// </summary>
    [Test]
    public async Task Inventory_Json_CarriesTheAxesAndTheRouteVerdicts()
    {
        using var fixture = await PluginTestFixture.CreateAsync("inv-json").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        var source = new InMemoryPluginSource(
            new[] { new PluginScript("inv.cs", SamplePluginSource.HelloWorld("InvJson")) });

        var pluginHost = BuildHost(fixture, source);
        await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        string json = pluginHost.Inventory.ToJson();

        await Assert.That(json).Contains("\"schemaVersion\": 1");
        await Assert.That(json).Contains("\"marker\": \"IToolPlugin\"")
            .Because("the axes the product declares are part of the document");
        await Assert.That(json).Contains("\"door\": \"RegisterTool\"");
        await Assert.That(json).Contains("\"carriage\": \"Carried\"")
            .Because("the per-route verdict is what tells a reader why an axis is or is not reachable");
        await Assert.That(json).Contains("\"route\": \"mcp-stdio\"");
        await Assert.That(json).Contains("\"phase\": \"active\"")
            .Because("the phase of the plugin is what the reader came for");
    }

    /// <summary>
    ///     The route the host is on is the one the composition root named — and a
    ///     host that was not told says so rather than guessing.
    /// </summary>
    [Test]
    public async Task Inventory_CarriesTheRouteTheCompositionRootNamed()
    {
        using var fixture = await PluginTestFixture.CreateAsync("inv-route").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var pluginHost = BuildHost(fixture, new InMemoryPluginSource([]), routeId: "mcp-stdio");
        await Assert.That(pluginHost.Inventory.Route).IsEqualTo("mcp-stdio");

        var unnamed = BuildHost(fixture, new InMemoryPluginSource([]));
        await Assert.That(unnamed.Inventory.Route).IsNull()
            .Because("a guess would be a second answer to 'which route is this', and the route registry owns that answer");
    }

    private static PluginHost BuildHost(
        PluginTestFixture fixture,
        IPluginSource source,
        IPluginDependencyResolver? dependencies = null,
        string? routeId = null)
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
                o.RouteId = routeId;
            })
            .Build(NullLogger<PluginHost>.Instance);
    }

    /// <summary>
    ///     A resolver whose answers the test controls: a name is satisfied only
    ///     after the test says so.
    /// </summary>
    private sealed class SwitchableResolver : IPluginDependencyResolver
    {
        private readonly HashSet<string> _satisfied = new(StringComparer.Ordinal);

        /// <inheritdoc />
        public bool IsSatisfied(string name) => _satisfied.Contains(name);
    }
}
