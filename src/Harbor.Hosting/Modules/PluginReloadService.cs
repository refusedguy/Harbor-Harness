#if HARBOR_WITH_PLUGINS
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Abstractions;
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
///     One installed plugin file for the <c>/plugins</c> panel: both scopes,
///     enabled and <c>.cs.disabled</c> files, overlaid with the version the
///     loader bound (when it did). Plain DTO — no plugin types leak out.
/// </summary>
/// <param name="Name">Display file name (<c>Alpha.cs</c>, never the <c>.disabled</c> suffix).</param>
/// <param name="Scope">Install scope (<c>global</c> / <c>project</c>).</param>
/// <param name="FullPath">Absolute source path (toggle target).</param>
/// <param name="Enabled">False for <c>.cs.disabled</c> files (the <c>*.cs</c> loader glob skips them).</param>
/// <param name="Version">Bound version, or null when never loaded this session.</param>
public sealed record InstalledPlugin(string Name, string Scope, string FullPath, bool Enabled, string? Version);

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
    private readonly IToolRegistry _tools;
    private readonly IProviderRegistry _providers;
    private readonly IAgentRegistry _agents;
    private readonly PanelRegistry _panels;
    private readonly IEventBus _eventBus;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _harborDir;
    private readonly ILogger<PluginReloadService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    ///     Construct the service. Registered by <see cref="RegistriesModule" /> with the
    ///     resolved harbor directory and host configuration snapshot.
    ///     Live registry singletons arrive via ctor (#63) — no per-reload
    ///     service location.
    /// </summary>
    public PluginReloadService(
        IToolRegistry tools,
        IProviderRegistry providers,
        IAgentRegistry agents,
        PanelRegistry panels,
        IEventBus eventBus,
        ILoggerFactory loggerFactory,
        string harborDir,
        IConfiguration configuration,
        ILogger<PluginReloadService> logger)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _panels = panels ?? throw new ArgumentNullException(nameof(panels));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _harborDir = harborDir ?? throw new ArgumentNullException(nameof(harborDir));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private readonly IConfiguration _configuration;

    private readonly object _installedLock = new();
    private readonly Dictionary<string, string> _loadedVersions = new(StringComparer.Ordinal);

    /// <summary>
    ///     Seed the loaded-version map (e.g. with the startup load result so
    ///     the <c>/plugins</c> panel reports startup-bound plugins as loaded
    ///     too, not just reload-bound ones). Additive — entries accumulate.
    /// </summary>
    public void NoteLoaded(IReadOnlyList<LoadedPlugin> loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        lock (_installedLock)
        {
            for (int i = 0; i < loaded.Count; i++)
            {
                _loadedVersions[loaded[i].SourcePath] = loaded[i].Version.ToString();
            }
        }
    }

    /// <summary>
    ///     List installed plugin files across both scopes for the
    ///     <c>/plugins</c> panel. Enumerates <c>*.cs</c> (enabled) plus
    ///     <c>*.cs.disabled</c> (toggled off — the loader glob skips them)
    ///     and overlays the version bound by any reload pass so far.
    ///     Missing scopes read as empty; unreadable scopes are skipped.
    /// </summary>
    public IReadOnlyList<InstalledPlugin> ListInstalled()
    {
        string globalPluginsDir = Path.Combine(_harborDir, "plugins");
        string projectPluginsDir = Path.Combine(Directory.GetCurrentDirectory(), ".harbor", "plugins");

        var files = new List<InstalledPlugin>();
        CollectScope(files, globalPluginsDir, "global");
        CollectScope(files, projectPluginsDir, "project");

        Dictionary<string, string> versions;
        lock (_installedLock)
        {
            versions = new Dictionary<string, string>(_loadedVersions, StringComparer.Ordinal);
        }

        var result = new List<InstalledPlugin>(files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i];
            versions.TryGetValue(f.FullPath, out string? version);
            result.Add(f with { Version = version });
        }

        result.Sort(static (a, b) =>
        {
            int byScope = string.CompareOrdinal(a.Scope, b.Scope);
            return byScope != 0 ? byScope : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return result;
    }

    private static void CollectScope(List<InstalledPlugin> files, string dir, string scope)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (string pattern in new[] { "*.cs", "*.cs.disabled" })
        {
            string[] matches;
            try
            {
                matches = Directory.GetFiles(dir, pattern);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            for (int i = 0; i < matches.Length; i++)
            {
                string fullPath = Path.GetFullPath(matches[i]);
                bool enabled = !fullPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                string name = Path.GetFileName(enabled ? fullPath : fullPath[..^".disabled".Length]);
                files.Add(new InstalledPlugin(name, scope, fullPath, enabled, Version: null));
            }
        }
    }

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
        string globalPluginsDir = Path.Combine(_harborDir, "plugins");
        string projectPluginsDir = Path.Combine(Directory.GetCurrentDirectory(), ".harbor", "plugins");

        // Late-loaded plugins cannot mutate the already-built container — an empty
        // collection keeps that contract explicit instead of pretending otherwise.
        var (loadHost, runtime) = PluginRuntimeComposer.Compose(
            new ServiceCollection(),
            _configuration,
            _loggerFactory,
            _eventBus,
            _tools,
            _providers,
            _agents,
            _panels,
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

        lock (_installedLock)
        {
            #pragma warning disable CFE0001
            // CFE0001 false positive. Baseline: docs/ROP-API-INVENTORY.md 5.
            // if (IsFailure) { log; return summary; } early-return guard
            foreach (var p in result.Value)
            #pragma warning restore CFE0001
            {
                _loadedVersions[p.SourcePath] = p.Version.ToString();
            }
        }

        _logger.LogInformation("Plugin reload complete: {Count} plugin(s) loaded", result.Value.Count);
        return new PluginReloadSummary(result.Value.Count, notes);
    }
}
#endif
