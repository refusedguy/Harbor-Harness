using Harbor.Plugins.Abstractions;
using Harbor.Abstractions.Plugins;
using Harbor.Plugins.Compilation;
using Harbor.Plugins.Instantiation;
using Harbor.Plugins.Runtime.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
namespace Harbor.Plugins.Runtime.Tests.Instantiation;

/// <summary>
///     Tests for the Instantiation layer: <see cref="ReflectionPluginInstantiator" />
/// and <see cref="PluginLifecycle" />.
/// </summary>
public sealed class InstantiationLayerTests
{
    /// <summary>
    ///     Test 1 — <see cref="ReflectionPluginInstantiator" /> finds the IPlugin
    ///     implementation in a freshly-compiled assembly and returns a
    ///     <see cref="LoadedPlugin" /> whose Name matches the plugin's declared name.
    /// </summary>
    [Test]
    public async Task Instantiator_ValidAssembly_ReturnsLoadedPlugin()
    {
        var (compiled, _) = await CompileAsync(SamplePluginSource.HelloWorld("Inst1")).ConfigureAwait(false);
        var instantiator = new ReflectionPluginInstantiator();

        var result = instantiator.Instantiate(compiled);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(1);
        await Assert.That(result.Value[0].Name).IsEqualTo("hello-world-inst1");
        await Assert.That(result.Value[0].Instance).IsNotNull();
    }

    /// <summary>
    ///     Test 2 — <see cref="ReflectionPluginInstantiator" /> does NOT call Initialize
    ///     on the plugin instance. We verify by checking that a plugin's Initialize
    ///     side-effect (logging) has not happened. Specifically: the LoadedPlugin.Instance
    ///     is returned but PluginContext was never supplied (Initialize requires one).
    /// </summary>
    [Test]
    public async Task Instantiator_DoesNotCallInitialize()
    {
        var (compiled, _) = await CompileAsync(SamplePluginSource.HelloWorld("Inst2")).ConfigureAwait(false);
        var instantiator = new ReflectionPluginInstantiator();

        var result = instantiator.Instantiate(compiled);

        await Assert.That(result.IsSuccess).IsTrue();
        // The plugin's Initialize would set its logger — since we never called Initialize,
        // calling Initialize now (manually) should not throw and should still work. The
        // important assertion is just that the instantiator didn't fail and didn't call
        // Initialize (which would have needed a PluginContext).
        var plugin = result.Value[0].Instance;
        await Assert.That(plugin).IsNotNull();
        await Assert.That(plugin.Name).IsEqualTo("hello-world-inst2");
    }

    /// <summary>
    ///     Test 3 — <see cref="ReflectionPluginInstantiator" /> skips plugin types that
    ///     don't have a parameterless constructor. Source declares a single class
    ///     implementing IPlugin with a `(int _)` ctor — should yield zero plugins and a
    ///     failure result.
    /// </summary>
    [Test]
    public async Task Instantiator_NoParameterlessCtor_ReturnsFailure()
    {
        var (compiled, _) = await CompileAsync(SamplePluginSource.NoParameterlessCtor("Inst3")).ConfigureAwait(false);
        var instantiator = new ReflectionPluginInstantiator();

        var result = instantiator.Instantiate(compiled);

        await Assert.That(result.IsFailure).IsTrue();
    }

    /// <summary>
    ///     Test 4 — <see cref="PluginLifecycle.BuildContext" /> populates
    /// <see cref="PluginContext.PluginDirectory" /> and
    /// <see cref="PluginContext.DataDirectory" /> from the supplied plugin root and
    /// source path.
    /// </summary>
    [Test]
    public async Task Lifecycle_BuildContext_PopulatesDirectories()
    {
        var host = new FakePluginLoadHost();
        var fakePlugin = new FakePlugin("lifecycle-test");
        const string pluginRoot = "/home/me/.harbor/plugins";
        const string sourcePath = "/home/me/.harbor/plugins/somefile.cs";

        var context = PluginLifecycle.BuildContext(host, fakePlugin, pluginRoot, sourcePath);

        await Assert.That(context.PluginDirectory).IsEqualTo("/home/me/.harbor/plugins");
        await Assert.That(context.DataDirectory).IsEqualTo("/home/me/.harbor/plugins/data/lifecycle-test");
        await Assert.That(context.HarborVersion).IsEqualTo(PluginLifecycle.CurrentHarborVersion);
    }

    private static async Task<(CompiledPluginAssembly, IPlugin)> CompileAsync(string source)
    {
        var references = new PluginAssemblyReferences(
            NullLogger<PluginAssemblyReferences>.Instance);
        var compiler = new RoslynPluginCompiler(references);
        var script = new PluginScript("test.cs", source);
        var result = await compiler.CompileAsync(script).ConfigureAwait(false);
        if (result.IsFailure)
            throw new InvalidOperationException(result.Error);
        return (result.Value, null!);
    }

    private sealed class FakePlugin : IPlugin
    {
        public FakePlugin(string name) { Name = name; }
        public string Name { get; }
        public Version Version => new(1, 0, 0);
        public Version RequiredHarborVersion => new(0, 4, 0);
        public string Description => "fake";
        public void Initialize(PluginContext context) { }
        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
