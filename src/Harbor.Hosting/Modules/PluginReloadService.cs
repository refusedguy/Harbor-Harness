#if HARBOR_WITH_PLUGINS
using Harbor.Plugins.Abstractions;
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
///     Hot-reload view over the CS-source plugin scopes against the LIVE
///     registry singletons.
/// </summary>
/// <remarks>
///     <para>
///         #1055 slice 3: CS plugins compile and execute out-of-process in
///         <c>harbor-plugins-host</c> — the CLI holds no in-process compiler
///         any more. <see cref="ReloadAsync" /> therefore registers nothing:
///         it reports where the tools actually come from and how to pick up
///         changed scripts (restart the host). <see cref="ListInstalled" />
///         keeps enumerating both scopes so the <c>/plugins</c> panel still
///         shows what the host sees.
///     </para>
///     <para>
///         Trust at reload time used to consult the persisted trust store; with
///         no in-process load there is no gate left in this process to consult
///         it. The host runs full-trust (see
///         <c>src/Harbor.Plugins.Host/README.md</c>): only reviewed source
///         files belong in the plugin directories.
///     </para>
/// </remarks>
public sealed class PluginReloadService
{
    private readonly string _harborDir;
    private readonly ILogger<PluginReloadService> _logger;

    /// <summary>
    ///     Construct the service. Registered by <see cref="RegistriesModule" />
    ///     with the resolved harbor directory.
    /// </summary>
    public PluginReloadService(
        string harborDir,
        ILogger<PluginReloadService> logger)
    {
        _harborDir = harborDir ?? throw new ArgumentNullException(nameof(harborDir));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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
    ///     Run one reload pass. Out-of-process (#1055s3) this registers
    ///     nothing: CS plugins are served by <c>harbor-plugins-host</c>, so
    ///     picking up changed scripts means restarting the host. Returns zero
    ///     loaded with that note — never a failure.
    /// </summary>
    public Task<PluginReloadSummary> ReloadAsync(CancellationToken ct = default)
    {
        _ = ct;
        _logger.LogInformation(
            "Plugin reload is served out-of-process by harbor-plugins-host (#1055s3): "
            + "restart the host to pick up changed scripts; nothing registered in-process.");
        return Task.FromResult(new PluginReloadSummary(
            0,
            ["plugins are served out-of-process by harbor-plugins-host — restart it to pick up changed scripts"]));
    }
}
#endif
