// ConfigFilePluginConfigSource.cs: where the out-of-process plugin host
// reads a plugin's configuration section from (#1734).
//
// WHY THE HOST READS THE FILE ITSELF
// ----------------------------------
// The plugin host runs in its own process: the AOT core spawns it as an MCP
// stdio server, and it is the process that compiles and runs the plugins. The
// user's configuration lives in `~/.harbor/config.json`, and the host is the
// only process on that side of the boundary that can read it without a round
// trip to the core.
//
// So the host reads the configuration file ONCE, at startup, and hands the
// per-plugin sections to the plugin pipeline through `IPluginConfigSource`.
// The pipeline never touches the file, and the file is never read twice.
//
// WHAT HAPPENS WHEN THE FILE IS NOT THERE
// ----------------------------------------
// Nothing. A user who has never configured a plugin has no config.json, and a
// plugin that declares no schema starts with the settings its own schema
// declares. A configuration file that cannot be read is reported and then
// treated as "no sections": the plugins still load, with their defaults.

using System.Text.Json;
using Harbor.Application.Configuration;
using Harbor.Plugins.Abstractions;
using Microsoft.Extensions.Logging;

namespace Harbor.Plugins.Host;

/// <summary>
///     The plugin configuration sections read from <c>~/.harbor/config.json</c>.
/// </summary>
internal sealed class ConfigFilePluginConfigSource : IPluginConfigSource
{
    private readonly IReadOnlyDictionary<string, JsonElement> _sections;

    /// <summary>
    ///     Construct a source over the sections read from the configuration file.
    /// </summary>
    /// <param name="sections">The per-plugin sections, keyed by plugin id.</param>
    internal ConfigFilePluginConfigSource(IReadOnlyDictionary<string, JsonElement> sections)
    {
        _sections = sections;
    }

    /// <inheritdoc />
    public JsonElement? Section(string pluginName)
    {
        if (pluginName is null)
        {
            return null;
        }

        return _sections.TryGetValue(pluginName, out JsonElement section) ? section : null;
    }

    /// <summary>
    ///     Read the configuration file and take the per-plugin sections out of it.
    /// </summary>
    /// <param name="logger">Logger for the read failures.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     A source over the sections the file holds. A file that is missing or
    ///     unreadable yields a source with no sections, because a plugin that
    ///     declares no schema does not need one.
    /// </returns>
    internal static async Task<IPluginConfigSource> LoadAsync(
        ILogger logger,
        CancellationToken ct = default)
    {
        var store = new JsonConfigStore(logger: null);
        var loaded = await store.LoadAsync(ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            ProgramLog.ConfigLoadFailed(logger, loaded.Error);
            return new ConfigFilePluginConfigSource(
                new Dictionary<string, JsonElement>(StringComparer.Ordinal));
        }

        HarborConfig config = loaded.Value;
        var sections = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonElement> section in config.Plugins)
        {
            sections[section.Key] = section.Value;
        }

        return new ConfigFilePluginConfigSource(sections);
    }
}
