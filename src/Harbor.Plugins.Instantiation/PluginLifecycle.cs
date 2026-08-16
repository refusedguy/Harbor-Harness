using Harbor.Abstractions.Plugins;
using Harbor.Plugins.Abstractions;
namespace Harbor.Plugins.Instantiation;
/// <summary>
///     Lifecycle helpers for live <see cref="IPlugin" /> instances. The registration
///     layer delegates here for <see cref="IPlugin.Initialize" /> + shutdown orchestration
///     so that lifecycle is independent of <c>Register*</c> dispatch.
/// </summary>
public static class PluginLifecycle
{
    /// <summary>
    ///     The current Harbor version reported to plugins via
    ///     <see cref="PluginContext.HarborVersion" />. Bumped with each Harbor release.
    /// </summary>
    public static readonly Version CurrentHarborVersion = new(0, 4, 0);

    /// <summary>
    ///     Build a <see cref="PluginContext" /> for the supplied plugin, deriving
    ///     <see cref="PluginContext.PluginDirectory" /> and
    ///     <see cref="PluginContext.DataDirectory" /> from the host's plugin root and the
    ///     plugin's <see cref="IPlugin.Name" />.
    /// </summary>
    /// <param name="host">The host registration sink (supplies services, config, etc.).</param>
    /// <param name="plugin">The plugin to build a context for.</param>
    /// <param name="pluginRoot">Host plugin root directory (e.g. <c>~/.harbor/plugins</c>).</param>
    /// <param name="sourcePath">Source identity — directory part becomes <c>PluginDirectory</c>.</param>
    /// <returns>A populated <see cref="PluginContext" />.</returns>
    public static PluginContext BuildContext(
        IPluginLoadHost host,
        IPlugin plugin,
        string pluginRoot,
        string sourcePath)
    {
        if (host is null)
            throw new ArgumentNullException(nameof(host));
        if (plugin is null)
            throw new ArgumentNullException(nameof(plugin));

        string pluginDir = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        string dataDir = Path.Combine(pluginRoot, "data", plugin.Name);

        return new PluginContext
        {
            Services = host.Services,
            Configuration = host.Configuration,
            LoggerFactory = host.LoggerFactory,
            EventBus = host.EventBus,
            PluginDirectory = pluginDir,
            DataDirectory = dataDir,
            HarborVersion = CurrentHarborVersion
        };
    }

    /// <summary>
    ///     Call <see cref="IPlugin.Initialize" /> on the supplied plugin. Returns
    ///     <see cref="Result.Success" /> on success, or failure with the thrown exception's
    ///     message. The caller is responsible for logging the failure.
    /// </summary>
    public static Result Initialize(IPlugin plugin, PluginContext context)
    {
        if (plugin is null)
            throw new ArgumentNullException(nameof(plugin));
        if (context is null)
            throw new ArgumentNullException(nameof(context));

        try
        {
            plugin.Initialize(context);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Initialize threw: {ex.Message}");
        }
    }

    /// <summary>
    ///     Call <see cref="IPlugin.ShutdownAsync" /> on the supplied plugin, swallowing
    ///     exceptions and returning them as a failure result. The caller is responsible
    ///     for logging.
    /// </summary>
    public static async Task<Result> ShutdownAsync(IPlugin plugin, CancellationToken ct = default)
    {
        if (plugin is null)
            throw new ArgumentNullException(nameof(plugin));

        try
        {
            await plugin.ShutdownAsync(ct).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"ShutdownAsync threw: {ex.Message}");
        }
    }
}
