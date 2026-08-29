using Harbor.Abstractions.Plugins;
namespace Harbor.Plugins.Abstractions;
/// <summary>
///     A live <see cref="IPlugin" /> instance plus the metadata the registration layer
///     needs to wire it into the host. Produced by <see cref="IPluginInstantiator" />.
/// </summary>
/// <param name="Instance">The live plugin instance.</param>
/// <param name="Name">Stable plugin id from <see cref="IPlugin.Name" />.</param>
/// <param name="Version">Semantic version from <see cref="IPlugin.Version" />.</param>
/// <param name="PluginType">The concrete <see cref="Type" /> that was instantiated.</param>
/// <param name="SourcePath">Source identity (path / resource name / synthetic id).</param>
/// <param name="SourceHash">SHA-256 hex hash of the source text.</param>
/// <param name="LoadedFromCache">
///     <see langword="true" /> if the compiled assembly was loaded from disk cache.
/// </param>
public sealed record LoadedPlugin(
    IPlugin Instance,
    string Name,
    Version Version,
    Type PluginType,
    string SourcePath,
    string SourceHash,
    bool LoadedFromCache,
    IReadOnlySet<PluginCapability> DeclaredCapabilities)
{
    /// <summary>
    ///     Convenience ctor for callers that don't track capability manifests
    ///     (legacy tests, custom instantiators) — grants an empty set (fail-closed).
    /// </summary>
    public LoadedPlugin(
        IPlugin Instance,
        string Name,
        Version Version,
        Type PluginType,
        string SourcePath,
        string SourceHash,
        bool LoadedFromCache)
        : this(Instance, Name, Version, PluginType, SourcePath, SourceHash, LoadedFromCache, FrozenCapabilities)
    {
    }

    /// <summary>
    ///     Human-readable identifier used in log lines and the <c>/plugins</c>
    ///     slash-command. Format: <c>name@version (file)</c>.
    /// </summary>
    public string DisplayName => $"{Name}@{Version} ({Path.GetFileName(SourcePath)})";

    private static readonly IReadOnlySet<PluginCapability> FrozenCapabilities =
        new HashSet<PluginCapability>();
}
