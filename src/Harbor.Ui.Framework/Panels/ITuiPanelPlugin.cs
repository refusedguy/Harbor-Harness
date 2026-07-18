using Harbor.Abstractions.Plugins;
namespace Harbor.Ui.Framework.Panels;

/// <summary>
///     Plugin contract for contributing dockable panels to any TUI renderer that
///     supports the Harbor panel system. Implementations register one or more
///     <see cref="IPanelProvider" /> instances during
///     <see cref="RegisterPanels" />; the host renderer queries them every frame.
/// </summary>
/// <remarks>
///     <para>
///         <b> Lifecycle:</b> the host calls <see cref="IPlugin.Initialize" /> first
///         (for service registration / event subscriptions), then
///         <see cref="RegisterPanels" /> once the <see cref="IPanelRegistry" /> is
///         available. Panels may be registered lazily — even after the renderer has
///         started — and they will appear in the next frame.
///     </para>
///     <para>
///         <b>Decoupling contract:</b> panel plugins MUST NOT reference
///         <c>Harbor.Core</c>. All agent state flows in through
///         <see cref="PanelContext.State" /> (an immutable <c>UiState</c>); all side
///         effects go through <c>UiStore.Dispatch</c> (retrieved from
///         <see cref="PanelContext.Services" />).
///     </para>
///     <para>
///         <b>Widget type:</b> the <see cref="IPanelProvider.Build" /> return type is
///         <see cref="object" /> precisely so a panel plugin can target a specific
///         renderer (e.g. SpectreTUI) without forcing <c>Harbor.Terminal.Abstractions</c>
///         to take a dependency on that renderer's widget framework. A plugin that
///         wants to support multiple renderers ships one assembly per renderer.
///     </para>
/// </remarks>
public interface ITuiPanelPlugin : IPlugin
{
    /// <summary>
    ///     Register this plugin's panels into the supplied registry. Called once after
    ///     <see cref="IPlugin.Initialize" /> and before the first frame that needs the
    ///     panels. May be called more than once if panels are re-registered (registry
    ///     replaces by id).
    /// </summary>
    /// <param name="registry">The host's panel registry.</param>
    void RegisterPanels(IPanelRegistry registry);
}
