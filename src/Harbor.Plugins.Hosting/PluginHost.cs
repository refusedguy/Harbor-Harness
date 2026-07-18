using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Compilation;
using Harbor.Plugins.Instantiation;
using Harbor.Plugins.Registration;
using Harbor.Plugins.Storage;
using Microsoft.Extensions.Logging;
namespace Harbor.Plugins.Hosting;

/// <summary>
///     Composition root for the layered plugin runtime. Iterates the supplied
/// <see cref="IPluginSource" />, compiles each script via <see cref="IPluginCompiler" />,
/// instantiates <see cref="Harbor.Abstractions.Plugins.IPlugin" /> types via
/// <see cref="IPluginInstantiator" />, and wires them into the host via
/// <see cref="IPluginRegistrar" />.
/// </summary>
/// <remarks>
///     <para>
///         The host itself is stateless beyond constructor-injected dependencies. All
///         per-plugin state lives in the layers below. This makes the host trivially
/// testable with in-memory doubles.
///     </para>
///     <para>
///         Failures at any stage are logged. Whether they abort the run or not depends
///         on <see cref="PluginHostOptions.ContinueOnError" /> (default: continue).
///     </para>
/// </remarks>
public sealed class PluginHost
{
    private readonly IPluginSource _source;
    private readonly IPluginCompiler _compiler;
    private readonly IPluginInstantiator _instantiator;
    private readonly IPluginRegistrar _registrar;
    private readonly PluginHostOptions _options;
    private readonly ILogger<PluginHost> _logger;

    /// <summary>
    ///     Construct a new plugin host.
    /// </summary>
    /// <param name="source">Where plugin scripts come from.</param>
    /// <param name="compiler">CS → Assembly compiler.</param>
    /// <param name="instantiator">Assembly → live IPlugin instantiator.</param>
    /// <param name="registrar">Live IPlugin → host registration sink.</param>
    /// <param name="options">Host options.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public PluginHost(
        IPluginSource source,
        IPluginCompiler compiler,
        IPluginInstantiator instantiator,
        IPluginRegistrar registrar,
        PluginHostOptions options,
        ILogger<PluginHost> logger)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
        _instantiator = instantiator ?? throw new ArgumentNullException(nameof(instantiator));
        _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    ///     Load every script produced by the source, compiling + instantiating +
    ///     registering each in turn. The host log surfaces per-plugin failures; the
    ///     returned <see cref="Result" /> is failure only if the run itself aborted
    ///     (e.g. ContinueOnError=false and a plugin threw).
    /// </summary>
    /// <param name="host">The host registration sink.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     Success with the list of <see cref="LoadedPlugin" />s that were successfully
    ///     registered, or failure with an error message.
    /// </returns>
    public async Task<Result<IReadOnlyList<LoadedPlugin>>> LoadAllAsync(
        IPluginLoadHost host,
        CancellationToken ct = default)
    {
        if (host is null)
            throw new ArgumentNullException(nameof(host));

        var loaded = new List<LoadedPlugin>();
        await foreach (var script in _source.GetScriptsAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var compiled = await _compiler.CompileAsync(script, ct).ConfigureAwait(false);
            if (compiled.IsFailure)
            {
                _logger.LogError("Plugin compilation failed for {Path}: {Error}", script.Path, compiled.Error);
                if (!_options.ContinueOnError)
                    return Result.Failure<IReadOnlyList<LoadedPlugin>>(compiled.Error);
                continue;
            }

            var instantiated = _instantiator.Instantiate(compiled.Value);
            if (instantiated.IsFailure)
            {
                _logger.LogError("Plugin instantiation failed for {Path}: {Error}", script.Path, instantiated.Error);
                if (!_options.ContinueOnError)
                    return Result.Failure<IReadOnlyList<LoadedPlugin>>(instantiated.Error);
                continue;
            }

            foreach (var plugin in instantiated.Value)
            {
                var registerResult = _registrar.Register(plugin, host);
                if (registerResult.IsFailure)
                {
                    _logger.LogError("Plugin registration failed for {DisplayName}: {Error}", plugin.DisplayName, registerResult.Error);
                    if (!_options.ContinueOnError)
                        return Result.Failure<IReadOnlyList<LoadedPlugin>>(registerResult.Error);
                    continue;
                }

                loaded.Add(plugin);
                _logger.LogInformation("Loaded plugin {DisplayName}", plugin.DisplayName);
            }
        }

        return Result.Success<IReadOnlyList<LoadedPlugin>>(loaded);
    }
}
