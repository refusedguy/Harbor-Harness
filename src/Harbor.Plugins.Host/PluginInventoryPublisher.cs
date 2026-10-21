// PluginInventoryPublisher.cs — publishes the live plugin composition where a
// reader without an MCP session can see it (#1722).
//
// WHY A FILE AND NOT ONLY THE MCP METHOD
// ---------------------------------------
// The out-of-process host answers `harbor/plugins/inventory` over MCP, and
// that is the LIVE channel: it reads the host's own state at the moment it is
// asked. But the CLI's `harbor plugin inventory` is a host-free verb — it
// resolves no services and starts no agent — so it has no MCP session to ask
// through.
//
// So the host also writes the same document into its plugin root, and rewrites
// it whenever the composition changes: after the load, and after every
// activation pass. The file is the same bytes the MCP method returns, so a
// reader that wants the file and a reader that wants the method read the same
// format.
//
// WHAT IS NOT WRITTEN
// -------------------
// A Harbor with no plugins and no plugin directory writes nothing: creating a
// plugin directory as a side effect of asking about plugins would be a rude
// surprise, and an empty snapshot says nothing that "no plugins" does not.

using Harbor.Plugins.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbor.Plugins.Host;

/// <summary>
///     Writes the live plugin composition into the plugin root.
/// </summary>
internal static class PluginInventoryPublisher
{
    /// <summary>
    ///     Publish the composition the host currently has.
    /// </summary>
    /// <param name="host">The host whose composition is published.</param>
    /// <param name="pluginRoot">The plugin root the file is written into.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public static void Publish(PluginHost host, string pluginRoot, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(pluginRoot);

        PluginInventory inventory = host.Inventory;
        bool rootExists = Directory.Exists(pluginRoot);
        if (!rootExists && inventory.Entries.Count == 0)
        {
            // Nothing to publish and nowhere to publish it.
            return;
        }

        string path = Path.Combine(pluginRoot, PluginInventory.FileName);
        try
        {
            Directory.CreateDirectory(pluginRoot);
            File.WriteAllText(path, inventory.ToJson());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PluginInventoryPublisherLog.PublishFailed(logger, path, ex);
        }
    }
}

/// <summary>
///     SG10: BCL <c>[LoggerMessage]</c> delegates for <see cref="PluginInventoryPublisher" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class PluginInventoryPublisherLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "The plugin inventory could not be published to {Path}")]
    public static partial void PublishFailed(ILogger logger, string path, Exception ex);
}
