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
///     #1739: the manifest is read as DATA, so the host can refuse a plugin
///     that cannot run on this Harbor BEFORE it pays to compile it. These tests
///     cover that gate, and the case that must not change: a plugin with no
///     manifest at all.
/// </summary>
public sealed class PluginManifestHostTests
{
    /// <summary>
    ///     A manifest that asks for a Harbor newer than the running one stops the
    ///     plugin at the door: it is not loaded, and it contributes nothing.
    /// </summary>
    [Test]
    public async Task Host_ManifestAskingForANewerHarbor_IsNotLoaded()
    {
        using var fixture = await PluginTestFixture.CreateAsync("manifest-gate-newer").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        var pluginHost = BuildHost(
            fixture,
            Script(
                SamplePluginSource.HelloWorld("ManifestGateNewer"),
                new PluginManifest
                {
                    Name = "hello-world-manifestgatenewer",
                    Version = new Version(1, 0, 0),
                    RequiredHarborVersion = new Version(9, 0, 0),
                    Axes = ["IToolPlugin"],
                }),
            continueOnError: true);

        var result = await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(0)
            .Because("a plugin that needs a Harbor this runtime is not must not appear as loaded");
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(0)
            .Because("a refused plugin contributes nothing to the host");
    }

    /// <summary>
    ///     The refusal says which Harbor the plugin needs and which one is
    ///     running. That is the difference between a version problem the user
    ///     can act on and a plugin that silently did not load.
    /// </summary>
    [Test]
    public async Task Host_ManifestRefusal_NamesTheVersions()
    {
        using var fixture = await PluginTestFixture.CreateAsync("manifest-gate-msg").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        var pluginHost = BuildHost(
            fixture,
            Script(
                SamplePluginSource.HelloWorld("ManifestGateMsg"),
                new PluginManifest
                {
                    Name = "hello-world-manifestgatemsg",
                    RequiredHarborVersion = new Version(9, 0, 0),
                }),
            continueOnError: false);

        var result = await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        await Assert.That(result.IsFailure).IsTrue()
            .Because("under fail-fast a plugin that cannot load fails the load");
        await Assert.That(result.Error).Contains("9.0.0");
    }

    /// <summary>
    ///     A manifest that asks for the running Harbor lets the plugin through —
    ///     the gate is a floor, not a pin.
    /// </summary>
    [Test]
    public async Task Host_ManifestAskingForTheRunningHarbor_LoadsThePlugin()
    {
        using var fixture = await PluginTestFixture.CreateAsync("manifest-gate-ok").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        var pluginHost = BuildHost(
            fixture,
            Script(
                SamplePluginSource.HelloWorld("ManifestGateOk"),
                new PluginManifest
                {
                    Name = "hello-world-manifestgateok",
                    Version = new Version(1, 0, 0),
                    RequiredHarborVersion = PluginLifecycle.CurrentHarborVersion,
                    Axes = ["IToolPlugin"],
                }),
            continueOnError: true);

        var result = await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(1);
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(1);
    }

    /// <summary>
    ///     A manifest the host could not read stops the plugin, and the reason
    ///     is the one the reader gave. A plugin that shipped a declaration the
    ///     host failed to apply must not run.
    /// </summary>
    [Test]
    public async Task Host_ManifestThatCannotBeRead_IsNotLoaded()
    {
        using var fixture = await PluginTestFixture.CreateAsync("manifest-gate-broken").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        var pluginHost = BuildHost(
            fixture,
            new PluginScript(
                "broken.cs",
                SamplePluginSource.HelloWorld("ManifestGateBroken"),
                null,
                "The plugin manifest 'broken.harbor-plugin.json' declares a capability this Harbor does "
                + "not know: Unknown plugin capability 'teleport'."),
            continueOnError: true);

        var result = await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(0)
            .Because("a plugin whose manifest could not be read must not load");
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(0);
    }

    /// <summary>
    ///     A plugin with NO manifest loads exactly as it did before the manifest
    ///     existed. That is the whole backward-compatibility promise.
    /// </summary>
    [Test]
    public async Task Host_PluginWithoutAManifest_LoadsThePlugin()
    {
        using var fixture = await PluginTestFixture.CreateAsync("manifest-gate-absent").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        var pluginHost = BuildHost(
            fixture,
            new PluginScript("gate.cs", SamplePluginSource.HelloWorld("ManifestGateAbsent")),
            continueOnError: true);

        var result = await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(1);
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(1);
    }

    private static PluginScript Script(string source, PluginManifest manifest) =>
        new("gate.cs", source, manifest);

    private static PluginHost BuildHost(
        PluginTestFixture fixture,
        PluginScript script,
        bool continueOnError)
    {
        var references = new PluginAssemblyReferences(
            NullLogger<PluginAssemblyReferences>.Instance);

        return new PluginHostBuilder()
            .WithSource(new InMemoryPluginSource(new[] { script }))
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
                o.ContinueOnError = continueOnError;
            })
            .Build(NullLogger<PluginHost>.Instance);
    }
}
