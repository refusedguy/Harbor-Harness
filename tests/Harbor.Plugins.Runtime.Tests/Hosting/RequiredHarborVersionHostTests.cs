using System.Globalization;
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
///     #1721: the LOAD PATH, not just the registrar. A plugin that needs a newer
///     Harbor than the one running must not reach the host's registries, and the
///     load result must say so instead of reporting a plugin that never arrived.
/// </summary>
public sealed class RequiredHarborVersionHostTests
{
    /// <summary>
    ///     A plugin that declares the Harbor version this build runs loads. Without
    ///     the gate this was the only case that worked; with the gate it must
    ///     still work, or every existing plugin would break.
    /// </summary>
    [Test]
    public async Task Host_PluginDeclaringTheRunningVersion_Loads()
    {
        using var fixture = await PluginTestFixture.CreateAsync("gate-host-ok").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        var pluginHost = BuildHost(
            fixture,
            SamplePluginSource.HelloWorld("GateHostOk", VersionArgs(PluginLifecycle.CurrentHarborVersion)),
            continueOnError: true);

        var result = await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(1);
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(1);
    }

    /// <summary>
    ///     A plugin that declares a NEWER Harbor than the one running is skipped
    ///     and does not appear in the loaded list, which is what the caller
    ///     reports to the user as an active plugin.
    /// </summary>
    [Test]
    public async Task Host_PluginDeclaringANewerHarbor_IsNotLoaded()
    {
        using var fixture = await PluginTestFixture.CreateAsync("gate-host-skip").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        var pluginHost = BuildHost(
            fixture,
            SamplePluginSource.HelloWorld("GateHostSkip", "9, 0, 0"),
            continueOnError: true);

        var result = await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(0)
            .Because("a plugin that needs a newer Harbor did not load, so it must not appear in the "
                   + "loaded list");
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(0)
            .Because("a refused plugin contributes nothing to the host");
    }

    /// <summary>
    ///     Under fail-fast the load itself fails, and the failure names the version
    ///     mismatch. "Plugin did not load" is the answer a user can act on.
    /// </summary>
    [Test]
    public async Task Host_PluginDeclaringANewerHarbor_FailFast_FailsTheLoadWithTheReason()
    {
        using var fixture = await PluginTestFixture.CreateAsync("gate-host-ff").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        var pluginHost = BuildHost(
            fixture,
            SamplePluginSource.HelloWorld("GateHostFf", "9, 0, 0"),
            continueOnError: false);

        var result = await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        await Assert.That(result.IsFailure).IsTrue()
            .Because("the plugin did not load, so the load must not report success");
        await Assert.That(result.Error).Contains("9.0.0")
            .Because("the refusal must name the Harbor version the plugin needs");
    }

    private static PluginHost BuildHost(
        PluginTestFixture fixture,
        string pluginSource,
        bool continueOnError)
    {
        var references = new PluginAssemblyReferences(
            NullLogger<PluginAssemblyReferences>.Instance);

        return new PluginHostBuilder()
            .WithSource(new InMemoryPluginSource(
                new[] { new PluginScript("gate.cs", pluginSource) }))
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

    /// <summary>
    ///     Render a version as the argument list of a <c>Version</c> constructor, so
    ///     the generated plugin source can declare exactly the version under test.
    /// </summary>
    private static string VersionArgs(Version version)
    {
        string major = version.Major.ToString(CultureInfo.InvariantCulture);
        string minor = version.Minor.ToString(CultureInfo.InvariantCulture);
        string build = version.Build < 0
            ? "0"
            : version.Build.ToString(CultureInfo.InvariantCulture);
        return major + ", " + minor + ", " + build;
    }
}
