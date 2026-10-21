using Harbor.Abstractions.Plugins;
using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Registration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Plugins.Runtime.Tests.TestSupport;

/// <summary>
///     The two things every registration gate needs: a registrar to put the
///     plugin in front of, and the loaded plugin to put in front of it.
/// </summary>
/// <remarks>
///     <para>
///         The manifest gate (#1739), the config gate (#1734) and the version
///         gate (#1721) all ask the same question of the same door — "what
///         does the registrar do with this plugin?" — and each of them carried
///         its own copy of the two calls that build that question. Two copies
///         of one setup is two places to fix one setup, and the copy that
///         drifts is the copy that stops testing the gate.
///     </para>
///     <para>
///         The registrar here is the product's own
///         <see cref="PluginRegistrar" />, with a fixed plugin root and no
///         logger: what these tests are about is the DECISION the registrar
///         takes, not the log line it writes.
///     </para>
/// </remarks>
public static class PluginGateFixture
{
    /// <summary>The plugin root the gates pretend the host published.</summary>
    public const string PluginRoot = "/home/me/.harbor/plugins";

    /// <summary>
    ///     A registrar that checks a plugin against the given Harbor version
    ///     and reads its settings from the given configuration source.
    /// </summary>
    /// <param name="harborVersion">
    ///     The version the plugin is checked against. Null, the default, is the
    ///     running Harbor's own version.
    /// </param>
    /// <param name="config">
    ///     Where the plugin's own configuration section comes from. Null, the
    ///     default, means the host publishes no sections at all.
    /// </param>
    public static PluginRegistrar NewRegistrar(
        Version? harborVersion = null,
        IPluginConfigSource? config = null)
        => new(
            PluginRoot,
            NullLogger<PluginRegistrar>.Instance,
            NullLoggerFactory.Instance,
            harborVersion: harborVersion,
            config: config);

    /// <summary>
    ///     A loaded plugin, with the manifest that belongs to it when there is
    ///     one.
    /// </summary>
    /// <param name="plugin">The plugin the host loaded.</param>
    /// <param name="manifest">
    ///     The declaration that was read beside the plugin. Null means the
    ///     plugin shipped without one.
    /// </param>
    public static LoadedPlugin Loaded(IPlugin plugin, PluginManifest? manifest = null)
        => new(
            plugin,
            plugin.Name,
            plugin.Version,
            plugin.GetType(),
            PluginRoot + "/" + plugin.Name + ".cs",
            "abc",
            false,
            new HashSet<PluginCapability>(),
            manifest);
}
