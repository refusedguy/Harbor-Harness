using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Terminal.Abstractions.Views;
namespace Harbor.Terminal.Abstractions.Plugins;
/// <summary>
///     Plugin contract for extending the Harbor TUI layer with custom views, view models,
///     and render-time behavior. This is the TUI analogue of <c>IToolPlugin</c> /
///     <c>IProviderPlugin</c> from <c>Harbor.Abstractions.Plugins</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>ITuiPlugin is a closed seam (#564) — nothing calls <c>RegisterTui</c>.</b> The
///         marker is dispatched and the host door is invoked
///         (<c>PluginRegistrar.Register</c> → <c>host.RegisterTuiPlugin</c>), so
///         this contract passes #620's axis freeze; what is missing is the
///         consumer. <c>IPluginLoadHost.TuiPlugins</c> has no reader anywhere in
///         the product, so <c>RegisterTui</c> is never called, and the items
///         below and the sample above describe a route no shipped renderer takes:
///         a plugin implementing this loads, logs success, and paints nothing. In
///         the canonical CellForge screen it could not paint even if it were
///         called, because that screen is drawn by the cell-diff layout tree and
///         not by the four placements <see cref="BaseTuiRenderer.ShouldRenderPlacement" />
///         queries — and <c>SidebarRight</c>, the placement the sample registers
///         at, is the <c>_ =&gt; false</c> arm of that switch.
///     </para>
///     <para>
///         <b>To add a plugin panel, implement <c>ITuiPanelPlugin</c></b> and
///         register through <c>IPanelRegistry</c> — that axis is live end to end
///         (<c>RegisterPanelProvider</c> → <c>PanelRegistryPluginAdapter</c> →
///         <c>CellForgePanelRegistry</c> → the dock). It paints rows of text, not
///         cells; adding a cell-level widget is a <c>Panel</c> subclass spliced
///         into <c>ChatScreen.Build</c>, which is an in-tree change and not a
///         plugin one (#555 freezes new axes).
///     </para>
///     <para>
///         Kept in the type rather than deleted so the freeze still has a name,
///         and so a future change that does wire a renderer to the collected
///         plugins has a contract to wire. The closure is guarded, not asserted:
///         <c>tests/Harbor.Architecture.Tests/CellForgeWidgetAxisRules.cs</c>
///         fails if the product starts rendering this seam and the documents
///         still call it closed, and fails in the other direction too.
///     </para>
///     <para>
///         <b>What the contract would do if it were wired:</b>
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Register a new view</b> — append a custom panel to any
///             <see cref="TuiViewPlacement" /> (status bar, chat history, sidebar, overlay, …).
///             The renderer would repaint it on the events selected by
///             <see cref="BaseTuiRenderer.ShouldRenderPlacement" />.
///         </item>
///         <item>
///             <b>Override a builtin view</b> — register a view with the same id as a builtin
///             (<c>"status-bar"</c>, <c>"chat-history"</c>, <c>"input"</c>, <c>"diff-preview"</c>)
///             before <see cref="BaseTuiRenderer.InitializeAsync" /> runs. The builtin registration
///             is skipped when an id is already taken (override-before-builtin).
///         </item>
///         <item>
///             <b>Register a custom view model</b> — add state holders that views can bind to
///             by id. The <see cref="ViewModelRegistry" /> auto-binds view ↔ view model by matching
///             <see cref="ITuiView.Id" /> to <see cref="ITuiViewModel.Id" />.
///         </item>
///     </list>
///     <para>
///         <b>Decoupling contract:</b> TUI plugins MUST NOT reference <c>Harbor.Application</c>
///         or <c>Harbor.Registries</c>. All
///         agent state flows in through <see cref="Harbor.Abstractions.Events.AgentEvent" />; all
///         rendering goes through <see cref="Renderers.ITuiRenderContext" />.
///     </para>
///     <para>
///         <b>Minimal example — a custom sidebar view (NOT REACHABLE today, see
///         above):</b>
///     </para>
///     <code>
/// public sealed class ClockPlugin : ITuiPlugin
/// {
///     public string Name => "clock";
///     public Version Version => new(1, 0, 0);
///     public string Description => "Shows a live clock in the right sidebar";
/// 
///     public void RegisterTui(ViewRegistry views, ViewModelRegistry viewModels)
///     {
///         viewModels.Register(new ClockViewModel());
///         views.Register(new ClockView()); // placement = SidebarRight, id = "clock"
///     }
/// }
/// </code>
///     <para>
///         The host WOULD call <see cref="RegisterTui" /> after constructing the
///         renderer but before <see cref="BaseTuiRenderer.InitializeAsync" />, so
///         plugins would win over builtins. It does not: no renderer calls it.
///     </para>
/// </remarks>
public interface ITuiPlugin
{
    /// <summary>Stable, lowercase plugin id (e.g. <c>"clock"</c>).</summary>
    public string Name { get; }

    /// <summary>Semantic version of the plugin.</summary>
    public Version Version { get; }

    /// <summary>Human-readable description shown in <c>/plugins</c>.</summary>
    public string Description { get; }

    /// <summary>
    ///     Register views and view models into the supplied registries. Called once during
    ///     renderer initialization, before builtin views are registered.
    /// </summary>
    /// <remarks>
    ///     <b>Never called.</b> No renderer in the product enumerates
    ///     <c>IPluginLoadHost.TuiPlugins</c>, so this method has no call site and
    ///     a plugin that implements it renders nothing. Implement
    ///     <c>ITuiPanelPlugin</c> and register through <c>IPanelRegistry</c> for a
    ///     panel that is actually painted. See the type-level remarks and #564.
    /// </remarks>
    /// <param name="views">
    ///     The view registry — register <see cref="ITuiView" /> instances
    ///     here.
    /// </param>
    /// <param name="viewModels">
    ///     The view model registry — register
    ///     <see cref="ITuiViewModel" /> instances here.
    /// </param>
    public void RegisterTui(ViewRegistry views, ViewModelRegistry viewModels);
}
