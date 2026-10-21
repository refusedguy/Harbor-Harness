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
///     <para>
///         <b>#1737 — the registration hands back the RUN it created.</b> The run is
///         what makes the registration reversible: it knows exactly which
///         registrations the plugin made, and stopping it takes precisely those
///         back and shuts the plugin down. A FAILED registration leaves nothing
///         behind — whatever landed before the refusal is taken back, so a
///         half-wired plugin does not become a dead tool in the registry.
///     </para>
/// </remarks>
public interface IPluginRegistrar
{
    /// <summary>
    ///     Initialize the plugin, dispatch its <c>Register*</c> methods into
    ///     <paramref name="host" />, and hand back the live run it created.
    /// </summary>
    /// <param name="plugin">The loaded plugin to register.</param>
    /// <param name="host">The host registration sink.</param>
    /// <param name="mode">
    ///     How this run comes to be. The default is <see cref="PluginRunMode.Run" />;
    ///     a host that REPLACES a running version says
    ///     <see cref="PluginRunMode.Update" />, and the run says so afterwards.
    /// </param>
    /// <returns>
    ///     Success with the run that is now live, or failure with an error message.
    ///     A failure leaves nothing registered.
    /// </returns>
    public Result<IPluginRun> Register(
        LoadedPlugin plugin,
        IPluginLoadHost host,
        PluginRunMode mode = PluginRunMode.Run);
}
