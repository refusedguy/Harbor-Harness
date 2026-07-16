using Harbor.Tui.Abstractions.ViewModels;
using Harbor.Tui.Abstractions.Views;
namespace Harbor.Tui.Abstractions.Plugins;
/// <summary>
///     Plugin contract for extending the Harbor TUI layer with custom views, view models,
///     and render-time behavior. This is the TUI analogue of <c>IToolPlugin</c> /
///     <c>IProviderPlugin</c> from <c>Harbor.Abstractions.Plugins</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>What a TUI plugin can do:</b>
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Register a new view</b> — append a custom panel to any
///             <see cref="TuiViewPlacement" /> (status bar, chat history, sidebar, overlay, …).
///             The renderer will repaint it on the events selected by
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
///         <b>Decoupling contract:</b> TUI plugins MUST NOT reference <c>Harbor.Core</c>. All
///         agent state flows in through <see cref="Harbor.Abstractions.Events.AgentEvent" />; all
///         rendering goes through <see cref="Renderers.ITuiRenderContext" />.
///     </para>
///     <para>
///         <b>Minimal example — a custom sidebar view:</b>
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
///         The host calls <see cref="RegisterTui" /> after constructing the renderer but before
///         <see cref="BaseTuiRenderer.InitializeAsync" />, so plugins always win over builtins.
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
