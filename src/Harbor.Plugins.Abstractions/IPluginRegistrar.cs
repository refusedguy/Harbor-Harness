namespace Harbor.Plugins.Abstractions;
/// <summary>
///     Wires a live <see cref="LoadedPlugin" /> into the host's registries. This is the
///     only layer that calls <see cref="Harbor.Abstractions.Plugins.IPlugin.Initialize" />
///     and dispatches <c>RegisterTools</c> / <c>RegisterProviders</c> / <c>RegisterAgents</c> /
///     <c>RegisterTuiPlugin</c> / <c>RegisterPanels</c> based on which sub-interfaces the
///     plugin implements.
/// </summary>
/// <remarks>
///     <para>
///         The registrar is intentionally synchronous: <c>Initialize</c> and the
///         <c>Register*</c> methods are sync. Hosts that want failure isolation should wrap the
///         registrar in <see cref="SafePluginRegistrar" />.
///     </para>
/// </remarks>
public interface IPluginRegistrar
{
    /// <summary>
    ///     Initialize the plugin and dispatch its <c>Register*</c> methods into
    ///     <paramref name="host" />.
    /// </summary>
    /// <param name="plugin">The loaded plugin to register.</param>
    /// <param name="host">The host registration sink.</param>
    /// <returns>Success, or failure with an error message.</returns>
    public Result Register(LoadedPlugin plugin, IPluginLoadHost host);
}
