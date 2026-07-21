using Harbor.Plugins.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Plugins.Hosting;
/// <summary>
///     Fluent builder for <see cref="PluginHost" />. Wires up the four layers
///     (storage / compilation / instantiation / registration) with sensible defaults and
///     lets the caller swap any layer.
/// </summary>
/// <remarks>
///     <example>
///         <code>
/// var host = new PluginHostBuilder()
///     .WithSource(new FileSystemPluginSource(new[] { "~/.harbor/plugins" }, logger))
///     .WithCompiler(new CachingCompiler(
///         new RoslynPluginCompiler(references),
///         cacheDir,
///         cacheLogger))
///     .WithInstantiator(new ReflectionPluginInstantiator())
///     .WithRegistrar(new SafePluginRegistrar(
///         new PluginRegistrar(pluginRoot, registrarLogger),
///         safeLogger))
///     .Build();
/// await host.LoadAllAsync(loadHost, ct);
///         </code>
///     </example>
/// </remarks>
public sealed class PluginHostBuilder
{
    private readonly PluginHostOptions _options = new();
    private IPluginCompiler? _compiler;
    private IPluginInstantiator? _instantiator;
    private IPluginRegistrar? _registrar;
    private IPluginSource? _source;

    /// <summary>Set the storage layer.</summary>
    public PluginHostBuilder WithSource(IPluginSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        return this;
    }

    /// <summary>Set the compilation layer.</summary>
    public PluginHostBuilder WithCompiler(IPluginCompiler compiler)
    {
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
        return this;
    }

    /// <summary>Set the instantiation layer.</summary>
    public PluginHostBuilder WithInstantiator(IPluginInstantiator instantiator)
    {
        _instantiator = instantiator ?? throw new ArgumentNullException(nameof(instantiator));
        return this;
    }

    /// <summary>Set the registration layer.</summary>
    public PluginHostBuilder WithRegistrar(IPluginRegistrar registrar)
    {
        _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
        return this;
    }

    /// <summary>Tune host options (cache dir, ContinueOnError, etc.).</summary>
    public PluginHostBuilder WithOptions(Action<PluginHostOptions> configure)
    {
        if (configure is null)
            throw new ArgumentNullException(nameof(configure));
        configure(_options);
        return this;
    }

    /// <summary>
    ///     Build the <see cref="PluginHost" />. Throws if any required layer is unset.
    /// </summary>
    public PluginHost Build(ILogger<PluginHost>? logger = null)
    {
        if (_source is null)
            throw new InvalidOperationException("Plugin source is not configured. Call WithSource(...) first.");
        if (_compiler is null)
            throw new InvalidOperationException("Plugin compiler is not configured. Call WithCompiler(...) first.");
        if (_instantiator is null)
            throw new InvalidOperationException("Plugin instantiator is not configured. Call WithInstantiator(...) first.");
        if (_registrar is null)
            throw new InvalidOperationException("Plugin registrar is not configured. Call WithRegistrar(...) first.");

        return new PluginHost(
            _source,
            _compiler,
            _instantiator,
            _registrar,
            _options,
            logger ?? NullLogger<PluginHost>.Instance);
    }
}
