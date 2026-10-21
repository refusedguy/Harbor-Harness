using Harbor.Plugins.Abstractions;

namespace Harbor.Plugins.Hosting;
/// <summary>
///     Tunable options for <see cref="PluginHost" />. Built up by
///     <see cref="PluginHostBuilder" /> in the composition root.
/// </summary>
public sealed class PluginHostOptions
{
    /// <summary>
    ///     Directory used by <see cref="Compilation.CachingCompiler" /> to store compiled
    ///     DLLs keyed by source SHA-256. Default: <c>~/.harbor/plugins/cache</c>.
    /// </summary>
    public string CacheDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".harbor", "plugins", "cache");

    /// <summary>
    ///     Root directory of the plugin storage (e.g. <c>~/.harbor/plugins</c>). Used by
    ///     <see cref="Registration.PluginRegistrar" /> to derive per-plugin data directories.
    /// </summary>
    public string PluginRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".harbor", "plugins");

    /// <summary>
    ///     When <see langword="true" /> (default), a failure to compile, instantiate, or
    ///     register one plugin is logged and the host continues with the next. When
    ///     <see langword="false" />, the first failure aborts <see cref="PluginHost.LoadAllAsync" />.
    /// </summary>
    public bool ContinueOnError { get; set; } = true;

    /// <summary>
    ///     The resolver that says whether a declared dependency is satisfied (#1733).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Null — the default — means the host publishes NO capability: every declared
    ///         dependency stays unsatisfied, and a plugin that declares one waits (registered,
    ///         not activated) instead of activating against an infrastructure that is not
    ///         there. A composition root that has capabilities to publish wires them here.
    ///     </para>
    /// </remarks>
    public IPluginDependencyResolver? Dependencies { get; set; }

    /// <summary>
    ///     The Harbor version the host runs as (#1739).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The host checks a plugin's manifest BEFORE it compiles the plugin
    ///         (#1739), and that check needs the running version. The version is
    ///         injected by the composition root — the host does not read it from
    ///         anywhere itself, because the layer that owns the running version is
    ///         a lower plugin layer the host may not bind.
    ///     </para>
    ///     <para>
    ///         Null — the default — means the host does not know the running
    ///         version, so the early check is skipped and the registration gate
    ///         stays the authoritative one: an incompatible plugin is still
    ///         refused, just after it was compiled.
    ///     </para>
    /// </remarks>
    public Version? HarborVersion { get; set; }

    /// <summary>
    ///     The id of the route this host is on — the id the route registry declares
    ///     (#1723) — so the live inventory says which route the composition it
    ///     reports belongs to (#1722).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Null — the default — means the composition root did not say, and the
    ///         inventory says so with a null route rather than guessing. A guess
    ///         would be a second answer to "which route is this", and the route
    ///         registry is where that answer lives.
    ///     </para>
    /// </remarks>
    public string? RouteId { get; set; }
}
