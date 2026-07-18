using Harbor.Plugins.Abstractions;
using CSharpFunctionalExtensions;
using Harbor.Plugins.Compilation;
using Harbor.Plugins.Hosting;
using Harbor.Plugins.Instantiation;
using Harbor.Plugins.Registration;
using Harbor.Plugins.Storage;
using Harbor.Plugins.Runtime.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
namespace Harbor.Plugins.Runtime.Tests.Hosting;

/// <summary>
///     Tests for the Hosting layer: <see cref="PluginHost" /> and
/// <see cref="PluginHostBuilder" />.
/// </summary>
public sealed class HostingLayerTests
{
    /// <summary>
    ///     Test 1 — <see cref="PluginHostBuilder.Build" /> throws when a required layer
    ///     is missing. Verifies the builder enforces completeness at composition time.
    /// </summary>
    [Test]
    public async Task Builder_MissingSource_Throws()
    {
        var builder = new PluginHostBuilder()
            .WithCompiler(new InlineCompiler())
            .WithInstantiator(new ReflectionPluginInstantiator())
            .WithRegistrar(new InlineRegistrar("/x"));
        InvalidOperationException? thrown = null;
        try { builder.Build(); }
        catch (InvalidOperationException ex) { thrown = ex; }
        await Assert.That(thrown).IsNotNull();
    }

    /// <summary>
    ///     Test 2 — <see cref="PluginHost.LoadAllAsync" /> end-to-end with an in-memory
    ///     source, real Roslyn compiler, real instantiator, real registrar: a hello
    ///     plugin compiles, instantiates, and registers its tool into the host.
    /// </summary>
    [Test]
    public async Task Host_LoadAllAsync_RegistersToolFromInMemorySource()
    {
        using var fixture = await PluginTestFixture.CreateAsync("host-e2e").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        var references = new PluginAssemblyReferences(
            NullLogger<PluginAssemblyReferences>.Instance);

        var pluginHost = new PluginHostBuilder()
            .WithSource(new InMemoryPluginSource(
                new[] { new PluginScript("test.cs", SamplePluginSource.HelloWorld("HostE2E")) }))
            .WithCompiler(new CachingCompiler(
                new RoslynPluginCompiler(references),
                fixture.CacheDir,
                NullLogger<CachingCompiler>.Instance))
            .WithInstantiator(new ReflectionPluginInstantiator())
            .WithRegistrar(new SafePluginRegistrar(
                new PluginRegistrar(fixture.PluginsDir, NullLogger<PluginRegistrar>.Instance),
                NullLogger.Instance))
            .WithOptions(o => o.PluginRoot = fixture.PluginsDir)
            .Build(NullLogger<PluginHost>.Instance);

        var result = await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(1);
        await Assert.That(result.Value[0].Name).IsEqualTo("hello-world-hoste2e");
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(1);
    }

    /// <summary>
    ///     Test 3 — <see cref="PluginHost.LoadAllAsync" /> with ContinueOnError=true
    ///     (default) skips a broken plugin and continues with the next one. The result
    ///     is success with the loadable plugin in the list, the broken one omitted.
    /// </summary>
    [Test]
    public async Task Host_ContinueOnError_SkipsBrokenPluginAndContinues()
    {
        using var fixture = await PluginTestFixture.CreateAsync("host-coe").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        var references = new PluginAssemblyReferences(
            NullLogger<PluginAssemblyReferences>.Instance);

        var source = new InMemoryPluginSource();
        source.Add("broken.cs", SamplePluginSource.Broken());
        source.Add("good.cs", SamplePluginSource.HelloWorld("HostCOE"));

        var pluginHost = new PluginHostBuilder()
            .WithSource(source)
            .WithCompiler(new CachingCompiler(
                new RoslynPluginCompiler(references),
                fixture.CacheDir,
                NullLogger<CachingCompiler>.Instance))
            .WithInstantiator(new ReflectionPluginInstantiator())
            .WithRegistrar(new SafePluginRegistrar(
                new PluginRegistrar(fixture.PluginsDir, NullLogger<PluginRegistrar>.Instance),
                NullLogger.Instance))
            .WithOptions(o =>
            {
                o.PluginRoot = fixture.PluginsDir;
                o.ContinueOnError = true;
            })
            .Build(NullLogger<PluginHost>.Instance);

        var result = await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(1);
        await Assert.That(result.Value[0].Name).IsEqualTo("hello-world-hostcoe");
    }

    /// <summary>
    ///     Test 4 — <see cref="PluginHost.LoadAllAsync" /> with ContinueOnError=false
    ///     aborts on the first compile failure and returns the failure result.
    /// </summary>
    [Test]
    public async Task Host_FailFast_AbortsOnFirstCompileFailure()
    {
        using var fixture = await PluginTestFixture.CreateAsync("host-ff").ConfigureAwait(false);
        Directory.CreateDirectory(fixture.CacheDir);

        var host = new FakePluginLoadHost();
        var references = new PluginAssemblyReferences(
            NullLogger<PluginAssemblyReferences>.Instance);

        var source = new InMemoryPluginSource();
        source.Add("broken.cs", SamplePluginSource.Broken());

        var pluginHost = new PluginHostBuilder()
            .WithSource(source)
            .WithCompiler(new CachingCompiler(
                new RoslynPluginCompiler(references),
                fixture.CacheDir,
                NullLogger<CachingCompiler>.Instance))
            .WithInstantiator(new ReflectionPluginInstantiator())
            .WithRegistrar(new SafePluginRegistrar(
                new PluginRegistrar(fixture.PluginsDir, NullLogger<PluginRegistrar>.Instance),
                NullLogger.Instance))
            .WithOptions(o =>
            {
                o.PluginRoot = fixture.PluginsDir;
                o.ContinueOnError = false;
            })
            .Build(NullLogger<PluginHost>.Instance);

        var result = await pluginHost.LoadAllAsync(host).ConfigureAwait(false);

        await Assert.That(result.IsFailure).IsTrue();
    }

    /// <summary>
    ///     Inline compiler that returns a fixed assembly — used by the builder-missing-source
    ///     test so we don't pay the Roslyn startup cost.
    /// </summary>
    private sealed class InlineCompiler : IPluginCompiler
    {
        public Task<CompilationResult> CompileAsync(PluginScript script, CancellationToken ct = default)
            => Task.FromResult(CompilationResult.Failure("inline — never used"));
    }

    private sealed class InlineRegistrar : IPluginRegistrar
    {
        private readonly string _pluginRoot;
        public InlineRegistrar(string pluginRoot) { _pluginRoot = pluginRoot; }
        public Result Register(LoadedPlugin plugin, IPluginLoadHost host) => Result.Success();
    }
}
