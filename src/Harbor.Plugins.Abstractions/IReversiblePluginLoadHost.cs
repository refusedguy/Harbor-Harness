using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Plugins.Abstractions;

/// <summary>
///     A load host that can take back what a plugin registered (#1737).
/// </summary>
/// <remarks>
///     <para>
///         This is the OPTIONAL half of <see cref="IPluginLoadHost" />. The doors
///         there answer "did it land?"; the methods here answer "take it back".
///         A host that cannot undo refuses to pretend: the run that needs the undo
///         says so, in the failure text, instead of leaving a tool in the registry
///         that nobody owns.
///     </para>
///     <para>
///         It is a separate interface on purpose. Changing
///         <see cref="IPluginLoadHost" />'s doors to return handles would make
///         every existing implementor carry the undo burden, and the axes that
///         cannot be undone at all — a panel is live UI state, a renderer is a
///         factory — would have to answer something they have no answer for. A
///         separate interface lets a host say which of the two it supports.
///     </para>
///     <para>
///         Each method answers in <see cref="Result" />: success means the
///         registration is gone, failure means it is still there and says why.
///     </para>
/// </remarks>
public interface IReversiblePluginLoadHost
{
    /// <summary>
    ///     Take back a tool registered under <paramref name="name" />.
    /// </summary>
    /// <param name="name">The tool's name.</param>
    /// <returns>Success when the tool is gone, failure when it is still registered.</returns>
    public Result UnregisterTool(ToolName name);

    /// <summary>
    ///     Take back an LLM provider registered under <paramref name="providerId" />.
    /// </summary>
    /// <param name="providerId">The provider's id.</param>
    /// <returns>Success when the provider is gone, failure when it is still registered.</returns>
    public Result UnregisterProvider(ProviderId providerId);

    /// <summary>
    ///     Take back an agent registered under <paramref name="name" />.
    /// </summary>
    /// <param name="name">The agent's name.</param>
    /// <returns>Success when the agent is gone, failure when it is still registered.</returns>
    public Result UnregisterAgent(AgentName name);

    /// <summary>
    ///     Take back a TUI plugin registered under <paramref name="name" />.
    /// </summary>
    /// <param name="name">The TUI plugin's name.</param>
    /// <returns>Success when the TUI plugin is gone, failure when it is still registered.</returns>
    public Result UnregisterTuiPlugin(string name);

    /// <summary>
    ///     Take back a panel provider registered under <paramref name="panelId" />.
    /// </summary>
    /// <param name="panelId">The panel's id.</param>
    /// <returns>Success when the panel is gone, failure when it is still registered.</returns>
    public Result UnregisterPanelProvider(string panelId);

    /// <summary>
    ///     Take back a session-store backend registered under <paramref name="backendId" />.
    /// </summary>
    /// <param name="backendId">The backend's id.</param>
    /// <returns>Success when the backend is gone, failure when it is still registered.</returns>
    public Result UnregisterSessionStore(string backendId);

    /// <summary>
    ///     Take back a TUI backend registered under <paramref name="backendId" />.
    /// </summary>
    /// <param name="backendId">The backend's id.</param>
    /// <returns>Success when the backend is gone, failure when it is still registered.</returns>
    public Result UnregisterTuiBackend(string backendId);
}
