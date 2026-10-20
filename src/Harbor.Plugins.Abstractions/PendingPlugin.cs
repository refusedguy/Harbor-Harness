namespace Harbor.Plugins.Abstractions;

/// <summary>
///     A plugin the host has LOADED but not ACTIVATED, and the dependencies it is
///     waiting for (#1733).
/// </summary>
/// <remarks>
///     <para>
///         The plugin is registered — it is known to the host, it is in the load set, its
///         source was compiled and its instance created — but it has NOT been initialized and
///         has NOT contributed anything: no tool, no provider, no agent, no panel. That is
///         the whole point of the deferred activation: a plugin that needs something the host
///         does not have yet waits, visibly, instead of failing at load time and disappearing
///         into a log line.
///     </para>
///     <para>
///         The unsatisfied dependencies are the names the plugin declared that the host
///         cannot satisfy yet. An empty list means the plugin is not waiting for anything —
///         it is active, and it is not in this inventory.
///     </para>
/// </remarks>
/// <param name="Name">Stable plugin id from <c>IPlugin.Name</c>.</param>
/// <param name="Version">Semantic version from <c>IPlugin.Version</c>.</param>
/// <param name="UnsatisfiedDependencies">
///     The declared dependency names that are not satisfied yet.
/// </param>
public sealed record PendingPlugin(
    string Name,
    Version Version,
    IReadOnlyList<string> UnsatisfiedDependencies);
