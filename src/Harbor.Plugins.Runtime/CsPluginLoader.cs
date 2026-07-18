using Harbor.Plugins.Abstractions;
using System.Reflection;
using Harbor.Abstractions.Plugins;
using Harbor.Plugins.Compilation;
using Harbor.Plugins.Hosting;
using Harbor.Plugins.Instantiation;
using Harbor.Plugins.Registration;
using Harbor.Plugins.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
namespace Harbor.Plugins.Runtime;

/// <summary>
///     <b>Obsolete facade.</b> Thin wrapper around <see cref="PluginHost" /> that
/// preserves the original <c>CsPluginLoader</c> API for one release while callers
/// migrate to the layered architecture.
/// </summary>
/// <remarks>
///     <para>
///         The original <c>CsPluginLoader</c> mixed 7+ responsibilities (discovery,
///         hashing, caching, Roslyn compilation, reflection instantiation,
///         initialization, registration). The runtime is now split into five layers:
/// <see cref="Storage" /> → <see cref="Compilation" /> →
/// <see cref="Instantiation" /> → <see cref="Registration" /> →
/// <see cref="Hosting" />. New code should depend on the layer interfaces directly
///         and compose them via <see cref="PluginHostBuilder" />.
///     </para>
///     <para>
///         This facade exists solely to keep the existing <c>Harbor.Cli</c> HostBuilder
///         and the existing test suite running without changes. It will be removed in
/// v0.5.
///     </para>
/// </remarks>
// S1133 fires on [Obsolete] asking "remember to remove this deprecated code someday".
// We will — in v0.5, per the [Obsolete] message. Suppress until then.
#pragma warning disable S1133
[Obsolete("Use PluginHostBuilder / PluginHost directly. Will be removed in v0.5.")]
public sealed class CsPluginLoader
#pragma warning restore S1133
{
    private const string PluginsSubDir = "plugins";
    private const string CacheSubDir = "cache";

    private readonly IPluginLoadHost _host;
    private readonly ILogger<CsPluginLoader> _logger;
    private readonly string _globalPluginsDir;
    private readonly string _projectPluginsDir;
    private readonly string _cacheDir;
    private readonly PluginAssemblyReferences _references;

    /// <summary>
    ///     Construct a new legacy loader. Same parameter shape as the original v0.4 API
    ///     — internally builds a <see cref="PluginHost" /> and delegates to it.
    /// </summary>
    public CsPluginLoader(
        IPluginLoadHost host,
        ILogger<CsPluginLoader> logger,
        string? harborDir = null,
        string? projectDir = null,
        PluginAssemblyReferences? references = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        string home = harborDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".harbor");
        _globalPluginsDir = Path.Combine(home, PluginsSubDir);
        _cacheDir = Path.Combine(_globalPluginsDir, CacheSubDir);

        string project = projectDir ?? Directory.GetCurrentDirectory();
        _projectPluginsDir = Path.Combine(project, ".harbor", PluginsSubDir);

        _references = references ?? new PluginAssemblyReferences(
            _host.LoggerFactory.CreateLogger<PluginAssemblyReferences>());
    }

    /// <summary>
    ///     Discover all CS-source plugins in the configured directories. Reads each file
    ///     from disk and wraps it in a <see cref="PluginScript" /> (path, source, hash).
    /// </summary>
    public async Task<IReadOnlyList<PluginScript>> DiscoverScriptsAsync(CancellationToken ct = default)
    {
        var source = new FileSystemPluginSource(
            new[] { _globalPluginsDir, _projectPluginsDir },
            _host.LoggerFactory.CreateLogger<FileSystemPluginSource>());
        var scripts = new List<PluginScript>();
        await foreach (var s in source.GetScriptsAsync(ct).ConfigureAwait(false))
            scripts.Add(s);
        return scripts;
    }

    /// <summary>
    ///     Discover and load all CS-source plugins. Each plugin that fails to compile or
    ///     initialize is logged and skipped — the operation as a whole succeeds as long
    ///     as the discovery step itself succeeded.
    /// </summary>
    public async Task<Result<IReadOnlyList<CompiledPlugin>>> DiscoverAndLoadAsync(CancellationToken ct = default)
    {
        var host = BuildPluginHost();
        var result = await host.LoadAllAsync(_host, ct).ConfigureAwait(false);
        if (result.IsFailure)
            return Result.Failure<IReadOnlyList<CompiledPlugin>>(result.Error);

        var compiled = new List<CompiledPlugin>(result.Value.Count);
        foreach (var lp in result.Value)
        {
            compiled.Add(new CompiledPlugin(
                Instance: lp.Instance,
                Name: lp.Name,
                Version: lp.Version,
                SourcePath: lp.SourcePath,
                SourceHash: lp.SourceHash,
                LoadedFromCache: lp.LoadedFromCache));
        }
        return Result.Success<IReadOnlyList<CompiledPlugin>>(compiled);
    }

    /// <summary>
    ///     Compile (or load from cache) and register a single CS-source plugin.
    /// </summary>
    public async Task<PluginCompilationResult> CompileAndLoadAsync(PluginScript script, CancellationToken ct = default)
    {
        if (script is null)
            throw new ArgumentNullException(nameof(script));

        var inMemory = new InMemoryPluginSource();
        inMemory.Add(script);

        // For the single-script API, use ContinueOnError=false so the underlying
        // compile error propagates to the caller (matches v0.4 behavior where
        // CompileAndLoadAsync returned the actual Roslyn diagnostics).
        var host = new PluginHostBuilder()
            .WithSource(inMemory)
            .WithCompiler(new CachingCompiler(
                new RoslynPluginCompiler(_references),
                _cacheDir,
                _host.LoggerFactory.CreateLogger<CachingCompiler>()))
            .WithInstantiator(new ReflectionPluginInstantiator())
            .WithRegistrar(new SafePluginRegistrar(
                new PluginRegistrar(_globalPluginsDir, _host.LoggerFactory.CreateLogger<PluginRegistrar>()),
                _logger))
            .WithOptions(o =>
            {
                o.PluginRoot = _globalPluginsDir;
                o.ContinueOnError = false;
            })
            .Build(_host.LoggerFactory.CreateLogger<PluginHost>());

        var result = await host.LoadAllAsync(_host, ct).ConfigureAwait(false);
        if (result.IsFailure)
            return PluginCompilationResult.Failure(result.Error, Array.Empty<Diagnostic>());

        if (result.Value.Count == 0)
            return PluginCompilationResult.Failure(
                $"No plugins loaded from '{script.Path}'.", Array.Empty<Diagnostic>());

        // For backwards-compat with single-plugin files, return the first one.
        var lp = result.Value[0];
        return PluginCompilationResult.Success(new CompiledPlugin(
            Instance: lp.Instance,
            Name: lp.Name,
            Version: lp.Version,
            SourcePath: lp.SourcePath,
            SourceHash: lp.SourceHash,
            LoadedFromCache: lp.LoadedFromCache));
    }

    /// <summary>
    ///     Compile (or load from cache) and register ALL IPlugin implementations in a single
    ///     .cs file.
    /// </summary>
    public async Task<Result<IReadOnlyList<CompiledPlugin>>> CompileAndLoadAllAsync(PluginScript script, CancellationToken ct = default)
    {
        if (script is null)
            throw new ArgumentNullException(nameof(script));

        var inMemory = new InMemoryPluginSource();
        inMemory.Add(script);

        var host = new PluginHostBuilder()
            .WithSource(inMemory)
            .WithCompiler(new CachingCompiler(
                new RoslynPluginCompiler(_references),
                _cacheDir,
                _host.LoggerFactory.CreateLogger<CachingCompiler>()))
            .WithInstantiator(new ReflectionPluginInstantiator())
            .WithRegistrar(new SafePluginRegistrar(
                new PluginRegistrar(_globalPluginsDir, _host.LoggerFactory.CreateLogger<PluginRegistrar>()),
                _logger))
            .WithOptions(o => o.PluginRoot = _globalPluginsDir)
            .Build(_host.LoggerFactory.CreateLogger<PluginHost>());

        var result = await host.LoadAllAsync(_host, ct).ConfigureAwait(false);
        if (result.IsFailure)
            return Result.Failure<IReadOnlyList<CompiledPlugin>>(result.Error);

        var compiled = new List<CompiledPlugin>(result.Value.Count);
        foreach (var lp in result.Value)
        {
            compiled.Add(new CompiledPlugin(
                Instance: lp.Instance,
                Name: lp.Name,
                Version: lp.Version,
                SourcePath: lp.SourcePath,
                SourceHash: lp.SourceHash,
                LoadedFromCache: lp.LoadedFromCache));
        }
        return Result.Success<IReadOnlyList<CompiledPlugin>>(compiled);
    }

    private PluginHost BuildPluginHost()
    {
        var source = new FileSystemPluginSource(
            new[] { _globalPluginsDir, _projectPluginsDir },
            _host.LoggerFactory.CreateLogger<FileSystemPluginSource>());
        var compiler = new CachingCompiler(
            new RoslynPluginCompiler(_references),
            _cacheDir,
            _host.LoggerFactory.CreateLogger<CachingCompiler>());
        var instantiator = new ReflectionPluginInstantiator();
        var registrar = new SafePluginRegistrar(
            new PluginRegistrar(_globalPluginsDir, _host.LoggerFactory.CreateLogger<PluginRegistrar>()),
            _logger);

        return new PluginHostBuilder()
            .WithSource(source)
            .WithCompiler(compiler)
            .WithInstantiator(instantiator)
            .WithRegistrar(registrar)
            .WithOptions(o => o.PluginRoot = _globalPluginsDir)
            .Build(_host.LoggerFactory.CreateLogger<PluginHost>());
    }
}
