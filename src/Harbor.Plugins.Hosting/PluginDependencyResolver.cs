using Harbor.Plugins.Abstractions;
using Microsoft.Extensions.Logging;

namespace Harbor.Plugins.Hosting;

/// <summary>
///     The default <see cref="IPluginDependencyResolver" />: a table of named capabilities,
///     each with a probe that says whether it is ready RIGHT NOW (#1733).
/// </summary>
/// <remarks>
///     <para>
///         The composition root owns the table. A plugin declares a NAME; the root decides what
///         the name means and when it becomes true — "the MCP client has connected", "the
///         configuration file exists". The probe is re-read on every check, so a capability that
///         becomes ready later is picked up by the next activation pass.
///     </para>
///     <para>
///         A name that is not in the table is NOT satisfied. Unknown is not ready: a capability
///         the host never published must not read as available, or the plugin would activate
///         against an infrastructure that is not there.
///     </para>
/// </remarks>
public sealed class PluginDependencyResolver : IPluginDependencyResolver
{
    private readonly IReadOnlyDictionary<string, Func<bool>> _probes;
    private readonly ILogger? _logger;

    /// <summary>
    ///     Construct a resolver over a table of named capabilities.
    /// </summary>
    /// <param name="probes">
    ///     The capability table: dependency name → readiness probe. Names are compared
    ///     ordinally, exactly as the plugin spelled them.
    /// </param>
    /// <param name="logger">
    ///     Optional logger. A probe that throws is reported here and treated as "not ready",
    ///     so a broken probe cannot take the whole plugin load down with it.
    /// </param>
    public PluginDependencyResolver(
        IReadOnlyDictionary<string, Func<bool>> probes,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(probes);
        _probes = probes;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsSatisfied(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (!_probes.TryGetValue(name, out Func<bool>? probe))
        {
            return false;
        }

        try
        {
            return probe();
        }
        catch (Exception ex)
        {
            // A probe that throws is a host-side bug. It must not abort the load of every
            // other plugin, and it must not read as "ready" either — so it is reported and
            // the dependency stays unsatisfied.
            _logger?.LogWarning(ex, "The dependency probe for '{Name}' threw; the dependency stays unsatisfied.", name);
            return false;
        }
    }
}
