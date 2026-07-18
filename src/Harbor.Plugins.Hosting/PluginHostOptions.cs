using Harbor.Plugins.Abstractions;
namespace Harbor.Plugins.Hosting;

/// <summary>
///     Tunable options for <see cref="PluginHost" />. Built up by
/// <see cref="PluginHostBuilder" /> in the composition root.
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
    /// <see cref="Registration.PluginRegistrar" /> to derive per-plugin data directories.
    /// </summary>
    public string PluginRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".harbor", "plugins");

    /// <summary>
    ///     When <see langword="true" /> (default), a failure to compile, instantiate, or
    ///     register one plugin is logged and the host continues with the next. When
    /// <see langword="false" />, the first failure aborts <see cref="PluginHost.LoadAllAsync" />.
    /// </summary>
    public bool ContinueOnError { get; set; } = true;
}
