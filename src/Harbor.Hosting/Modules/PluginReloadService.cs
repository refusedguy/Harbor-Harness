#if HARBOR_WITH_PLUGINS
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Hosting;
using Harbor.Ui.Framework.Panels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace Harbor.Hosting;

/// <summary>Outcome summary of one reload pass.</summary>
/// <param name="Loaded">Plugins successfully registered in this pass.</param>
/// <param name="Notes">Human-readable per-plugin notes (cache hits, failures, hints).</param>
public sealed record PluginReloadSummary(int Loaded, IReadOnlyList<string> Notes)
{
    /// <summary>Empty success summary.</summary>
    public static PluginReloadSummary Empty() => new(0, []);
}

/// <summary>
///     Hot-reload runner for CS-source plugins against the LIVE registry singletons.
///     Re-runs the exact startup pipeline — discovery → trust gate → cached compile →
///     instantiate → register — at runtime, so anything dropped into the plugin scopes
///     becomes usable without restarting the process.
/// </summary>
/// <remarks>
///     <para>
///         MVP semantics: newly added plugin files load in place. A file whose
///         contribution already exists (same path reloaded, or a name collision with an
///         earlier registration) fails registry registration and is reported as a note —
///         restart the process to fully replace plugins edited on disk. Removal and
///         replace-with-unregister tracking are follow-up work.
///     </para>
///     <para>
///         Trust at reload time consults the same persisted decision store as startup:
///         previously approved project plugins keep loading; NEW or EDITED ones fail
///         closed because the interactive prompt hook is not wired here — approve them
///         via the next interactive start instead.
///     </para>
/// </remarks>
public sealed class PluginReloadService
{
    private readonly IServiceProvider _sp;
    private readonly string _harborDir;
    private readonly ILogger<PluginReloadService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    ///     Construct the service. Registered by <see cref="RegistriesModule" /> with the
    ///     resolved harbor directory and host configuration snapshot.
    /// </summary>
    public PluginReloadService(
        IServiceProvider sp,
        string harborDir,
        IConfiguration configuration,
        ILogger<PluginReloadService> logger)
    {
        _sp = sp ?? throw new ArgumentNullException(nameof(sp));
        _harborDir = harborDir ?? throw new ArgumentNullException(nameof(harborDir));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private readonly IConfiguration _configuration;

    /// <summary>
    ///     Run one full load pass over both plugin scopes. Serialized — concurrent
    ///     invocations queue up and run one after another.
    /// </summary>
    public async Task<PluginReloadSummary> ReloadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReloadCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PluginReloadSummary> ReloadCoreAsync(CancellationToken ct)
    {
        var tools = _sp.GetRequiredService<IToolRegistry>();
        var providers = _sp.GetRequiredService<IProviderRegistry>();
        var agents = _sp.GetRequiredService<IAgentRegistry>();
        var panels = _sp.GetRequiredService<PanelRegistry>();
        var eventBus = _sp.GetRequiredService<IEventBus>();
        var loggerFactory = _sp.GetRequiredService<ILoggerFactory>();

        string globalPluginsDir = Path.Combine(_harborDir, "plugins");
        string projectPluginsDir = Path.Combine(Directory.GetCurrentDirectory(), ".harbor", "plugins");

        // Late-loaded plugins cannot mutate the already-built container — an empty
        // collection keeps that contract explicit instead of pretending otherwise.
        var (loadHost, runtime) = PluginRuntimeComposer.Compose(
            new ServiceCollection(),
            _configuration,
            loggerFactory,
            eventBus,
            tools,
            providers,
            agents,
            panels,
            globalPluginsDir,
            projectPluginsDir,
            trustPrompt: null);

        var result = await runtime.LoadAllAsync(loadHost, ct).ConfigureAwait(false);

        if (result.IsFailure)
        {
            _logger.LogWarning("Plugin reload failed: {Error}", result.Error);
            return new PluginReloadSummary(0, [result.Error]);
        }

        var notes = new List<string>();
        foreach (var p in result.Value)
        {
            notes.Add($"loaded {p.DisplayName} ({p.SourcePath}{(p.LoadedFromCache ? ", cache" : "")})");
        }

        _logger.LogInformation("Plugin reload complete: {Count} plugin(s) loaded", result.Value.Count);
        return new PluginReloadSummary(result.Value.Count, notes);
    }
}
#endif
