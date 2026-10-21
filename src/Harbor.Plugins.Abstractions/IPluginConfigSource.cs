// IPluginConfigSource.cs: where the host reads a plugin's configuration
// section from (#1734).
//
// WHY THIS IS A SEAM AND NOT A SERVICE LOOKUP
// --------------------------------------------
// The plugin layers may not reach into the configuration family: the layer that
// owns the configuration file is below the plugin pipeline in the dependency
// graph, and a plugin layer that named it would pull the whole configuration
// family (the config store, the auth store, the provider catalogue) into every
// plugin that loads. So the pipeline asks for a section through this port, and
// the composition root decides where the sections come from.
//
// It is the same shape as `IPluginDependencyResolver` (#1733): a question the
// host answers for the pipeline, injected by the composition root. The plugin
// host answers it from `~/.harbor/config.json`; a test answers it from a
// dictionary; a host with no configuration at all answers "nothing configured"
// for every plugin.
//
// WHAT THE IMPLEMENTOR PROMISES
// ------------------------------
// The returned element must stay valid after the call returns. A section read
// out of a document that the implementation then disposes would be a dangling
// reference, and the plugin pipeline would read a value that is no longer
// there. The implementation that reads the configuration file clones what it
// returns for exactly this reason.

using System.Text.Json;

namespace Harbor.Plugins.Abstractions;

/// <summary>
///     Reads the configuration section of a plugin.
/// </summary>
public interface IPluginConfigSource
{
    /// <summary>
    ///     The section the user wrote for this plugin, or <see langword="null" />
    ///     when the user configured nothing for it.
    /// </summary>
    /// <param name="pluginName">The plugin's id.</param>
    /// <returns>The plugin's section, or <see langword="null" />.</returns>
    public JsonElement? Section(string pluginName);
}
