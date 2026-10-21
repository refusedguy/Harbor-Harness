using Harbor.Application.Configuration;
using Harbor.Plugins.Hosting;
using Harbor.Tools.Builtin;

namespace Harbor.Hosting;

/// <summary>
///     Builds the static half of the runtime catalog the <c>harbor_inspect</c>
///     tool reports: the plugin extension axes and what each route does with
///     them, the provider catalogue, and the versions this build speaks.
/// </summary>
/// <remarks>
///     <para>
///         Every fact here is COPIED out of the declaration that owns it — the
///         axes from <see cref="PluginRouteAxisRegistry" />, the providers from
///         <see cref="ProviderPresets" />, the versions from
///         <see cref="PluginRuntimeVersions" />. Nothing is written down twice:
///         the catalog cannot disagree with the product about which axes exist
///         or which providers are known, because it has no list of its own.
///     </para>
///     <para>
///         The copy exists because the tool lives in
///         <c>Harbor.Tools.Builtin</c>, an Infrastructure assembly the layer
///         matrix keeps below the plugin and configuration assemblies. The
///         composition root is the one place that may see both, so it is the
///         one place that does the copying.
///     </para>
/// </remarks>
internal static class RuntimeApiFactsFactory
{
    /// <summary>
    ///     Read the facts the registries do not own.
    /// </summary>
    internal static RuntimeApiFacts Create()
    {
        IReadOnlyList<PluginRouteAxis> rows = PluginRouteAxisRegistry.All;
        var axes = new List<RuntimeExtensionAxis>(rows.Count);
        foreach (PluginRouteAxis row in rows)
        {
            axes.Add(new RuntimeExtensionAxis(
                PluginRouteAxisRegistry.MarkerOf(row.Door),
                row.Door,
                row.Route,
                CarriageName(row.Carriage),
                row.Reason));
        }

        IReadOnlyList<ProviderPresets.Preset> presets = ProviderPresets.All;
        var providers = new List<RuntimeProviderEntry>(presets.Count);
        foreach (ProviderPresets.Preset preset in presets)
        {
            providers.Add(new RuntimeProviderEntry(
                preset.Id,
                preset.DisplayName,
                preset.DefaultModel,
                preset.RequiresApiKey,
                preset.SetupHint));
        }

        return new RuntimeApiFacts(
            PluginRuntimeVersions.HarborVersion,
            PluginRuntimeVersions.ManifestSchemaVersion,
            axes,
            providers);
    }

    /// <summary>
    ///     The verdict, in the three words the catalog speaks. The reason
    ///     beside it is what makes the word actionable.
    /// </summary>
    private static string CarriageName(PluginAxisCarriage carriage) => carriage switch
    {
        PluginAxisCarriage.Carried => "carried",
        PluginAxisCarriage.Refused => "refused",
        _ => "closed",
    };
}
