using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Compilation;
using Harbor.Plugins.Hosting;
using Harbor.Plugins.Instantiation;
using Harbor.Plugins.Registration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Plugins.Runtime.Tests.TestSupport;

/// <summary>
///     Builds the plugin host the runtime tests drive: a real Roslyn compiler, a
///     real instantiator and a real registrar over the supplied source. One
///     builder chain, shared, so a test that needs a host does not grow its own
///     copy of it.
/// </summary>
public static class PluginHostFactory
{
    /// <summary>
    ///     Build a host over <paramref name="source" />.
    /// </summary>
    /// <param name="source">Where the plugin scripts come from.</param>
    /// <param name="fixture">The temp directory the host uses for its cache and its plugin root.</param>
    /// <param name="continueOnError">
    ///     True to skip a broken plugin and carry on, false to fail the whole load.
    /// </param>
    /// <returns>The host.</returns>
    public static PluginHost Build(
        IPluginSource source,
        PluginTestFixture fixture,
        bool continueOnError)
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
                o.ContinueOnError = continueOnError;
            })
            .Build(NullLogger<PluginHost>.Instance);
    }
}
