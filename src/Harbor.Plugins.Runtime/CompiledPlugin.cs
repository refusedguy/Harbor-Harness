using Harbor.Abstractions.Plugins;
namespace Harbor.Plugins.Runtime;
/// <summary>
///     Metadata + instance handle for a plugin that has been successfully compiled from
///     CS source and instantiated. Returned by <see cref="CsPluginLoader" /> on success.
/// </summary>
/// <param name="Instance">The live <see cref="IPlugin" /> instance.</param>
/// <param name="Name">The plugin's stable id (from <see cref="IPlugin.Name" />).</param>
/// <param name="Version">The plugin's semantic version (from <see cref="IPlugin.Version" />).</param>
/// <param name="SourcePath">Absolute path of the source .cs file the plugin was loaded from.</param>
/// <param name="SourceHash">SHA-256 hex hash of the source text (cache key).</param>
/// <param name="LoadedFromCache">
///     <see langword="true" /> if the compiled assembly was loaded from the on-disk cache
///     rather than freshly compiled this run.
/// </param>
public sealed record CompiledPlugin(
    IPlugin Instance,
    string Name,
    Version Version,
    string SourcePath,
    string SourceHash,
    bool LoadedFromCache)
{
    /// <summary>
    ///     Human-readable identifier used in log lines, the <c>/plugins</c> slash-command,
    ///     and error reports. Format: <c>name@version (file)</c>.
    /// </summary>
    public string DisplayName => $"{Name}@{Version} ({Path.GetFileName(SourcePath)})";
}
