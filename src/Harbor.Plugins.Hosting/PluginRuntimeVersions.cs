using System.Globalization;
using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Instantiation;

namespace Harbor.Plugins.Hosting;

/// <summary>
///     The versions this build speaks to plugins: the Harbor version a plugin's
///     declared floor is checked against, and the plugin manifest schema version
///     this build accepts.
/// </summary>
/// <remarks>
///     <para>
///         The two facts live in different assemblies because they are owned by
///         different layers — the version by the instantiation layer that hands
///         it to every plugin context, the schema version by the contract layer
///         that declares the manifest shape. This type is the one place that
///         reads both, so a caller that needs to STATE the versions (the
///         runtime catalog the model reads, a plugin host banner) does not have
///         to reach into either layer and cannot report one without the other.
///     </para>
/// </remarks>
public static class PluginRuntimeVersions
{
    /// <summary>
    ///     The Harbor version plugins are checked against — the same value every
    ///     plugin context carries, and the floor
    ///     <see cref="PluginCompatibility" /> compares a declared requirement
    ///     with.
    /// </summary>
    public static string HarborVersion { get; } = PluginLifecycle.CurrentHarborVersion.ToString();

    /// <summary>
    ///     The plugin manifest schema version this build accepts.
    /// </summary>
    public static string ManifestSchemaVersion { get; } =
        PluginManifest.SupportedSchemaVersion.ToString(CultureInfo.InvariantCulture);
}
