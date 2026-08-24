namespace Harbor.Hosting;

/// <summary>Feature switches resolved from compile-time symbols (§3.3).</summary>
public sealed record HarborFeatureSet(bool Plugins, bool SpectreTui, bool AllProviders, bool AllTools)
{
    public static HarborFeatureSet Disabled { get; } = new(false, false, false, false);
}

/// <summary>
///     The ONLY place that maps HARBOR_WITH_* compile symbols into
///     <see cref="HarborFeatureSet"/> values — the rest of the hosting code
///     branches on the feature record, not on #if.
/// </summary>
internal static class HarborBuildFeatures
{
    public static HarborFeatureSet Detect
    {
        get
        {
            var plugins = false;
#if HARBOR_WITH_PLUGINS
            plugins = true;
#endif
            var spectreTui = false;
#if HARBOR_WITH_SPECTRE_TUI
            spectreTui = true;
#endif
            var allProviders = false;
#if HARBOR_WITH_ALL_PROVIDERS
            allProviders = true;
#endif
            var allTools = false;
#if HARBOR_WITH_ALL_TOOLS
            allTools = true;
#endif
            return new HarborFeatureSet(plugins, spectreTui, allProviders, allTools);
        }
    }
}
