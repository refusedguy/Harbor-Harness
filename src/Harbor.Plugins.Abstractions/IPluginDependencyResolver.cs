namespace Harbor.Plugins.Abstractions;

/// <summary>
///     Decides whether a dependency a plugin declared is ready right now (#1733).
/// </summary>
/// <remarks>
///     <para>
///         A plugin declares what it needs as a NAME (<see cref="Harbor.Abstractions.Plugins.IPlugin.Dependencies" />)
///         and the host says whether that name is satisfied. The names are opaque strings on
///         purpose: a plugin author names a capability, and the composition root decides what
///         the name means and when it becomes true. Nothing here reflects over a type, so the
///         seam survives NativeAOT (#626).
///     </para>
///     <para>
///         A name the resolver does not know is NOT satisfied. Unknown is not ready — a
///         capability the host never published must not read as "available", or a plugin would
///         activate against an infrastructure that is not there and fail at the moment it
///         matters.
///     </para>
/// </remarks>
public interface IPluginDependencyResolver
{
    /// <summary>
    ///     Whether the named dependency is satisfied right now.
    /// </summary>
    /// <param name="name">
    ///     The dependency name, exactly as the plugin declared it.
    /// </param>
    /// <returns>
    ///     <see langword="true" /> when the dependency is ready and the plugin may be
    ///     activated; <see langword="false" /> when it is not — including for a name the
    ///     resolver does not know.
    /// </returns>
    public bool IsSatisfied(string name);
}
