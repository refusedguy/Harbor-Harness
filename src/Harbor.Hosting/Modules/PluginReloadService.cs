#if HARBOR_WITH_PLUGINS
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Plugins;
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
///         <b>One inventory, two consumers (#1738).</b> The scope enumeration
///         is NOT this class's own: it reads the same
///         <see cref="IPluginInventory" /> the agent-facing
///         <c>plugin_manager</c> tool reads, so the panel and the agent cannot
///         disagree about what is installed.
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
    private readonly IPluginInventory _inventory;
    private readonly ILogger<PluginReloadService> _logger;

    /// <summary>
    ///     Construct the service. Registered by <see cref="RegistriesModule" />
    ///     over the process-wide plugin inventory.
    /// </summary>
    public PluginReloadService(
        IPluginInventory inventory,
        ILogger<PluginReloadService> logger)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
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
    ///     <c>/plugins</c> panel. Reads the process-wide
    ///     <see cref="IPluginInventory" /> — the same one the agent-facing
    ///     <c>plugin_manager</c> tool reads — and overlays the version bound
    ///     by any reload pass so far. A scope that exists but cannot be read
    ///     reads as an empty list, with the reason logged.
    /// </summary>
    public IReadOnlyList<InstalledPlugin> ListInstalled()
    {
        Result<IReadOnlyList<PluginInventoryEntry>> listed = _inventory.List();
        if (listed.IsFailure)
        {
            PluginReloadServiceLog.InventoryUnavailable(_logger, listed.Error);
            return [];
        }

        Dictionary<string, string> versions;
        lock (_installedLock)
        {
            versions = new Dictionary<string, string>(_loadedVersions, StringComparer.Ordinal);
        }

        IReadOnlyList<PluginInventoryEntry> entries = listed.Value;
        var result = new List<InstalledPlugin>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            PluginInventoryEntry entry = entries[i];
            versions.TryGetValue(entry.FullPath, out string? version);
            result.Add(new InstalledPlugin(
                entry.Name,
                entry.Scope == PluginScope.Global ? "global" : "project",
                entry.FullPath,
                entry.Enabled,
                version));
        }

        result.Sort(static (a, b) =>
        {
            int byScope = string.CompareOrdinal(a.Scope, b.Scope);
            return byScope != 0 ? byScope : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return result;
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
        PluginReloadServiceLog.ReloadOutOfProc(_logger);
        return Task.FromResult(new PluginReloadSummary(
            0,
            ["plugins are served out-of-process by harbor-plugins-host — restart it to pick up changed scripts"]));
    }
}

/// <summary>
///     SG4: BCL <c>[LoggerMessage]</c> delegates for <see cref="PluginReloadService" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class PluginReloadServiceLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Plugin reload is served out-of-process by harbor-plugins-host (#1055s3): restart the host to pick up changed scripts; nothing registered in-process.")]
    public static partial void ReloadOutOfProc(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "The plugin inventory could not be read: {Error}")]
    public static partial void InventoryUnavailable(ILogger logger, string error);
}
#endif
