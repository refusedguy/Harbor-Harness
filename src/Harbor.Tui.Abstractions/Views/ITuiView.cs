using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Tui;
using Harbor.Tui.Abstractions.Renderers;
using Harbor.Tui.Abstractions.ViewModels;

namespace Harbor.Tui.Abstractions.Views;

/// <summary>
/// Base view contract — a visual component rendered by the TUI.
/// Implements the View part of MVVM. Views are stateless visualizations
/// of their bound ViewModel state.
/// </summary>
public interface ITuiView : IDisposable
{
    /// <summary>Unique view identifier (e.g. "status-bar", "chat-history", "diff-preview").</summary>
    string Id { get; }

    /// <summary>Display name shown in /views command.</summary>
    string DisplayName { get; }

    /// <summary>Where this view appears in the layout.</summary>
    TuiViewPlacement Placement { get; }

    /// <summary>Bound view model (if any).</summary>
    ITuiViewModel? ViewModel { get; set; }

    /// <summary>Render the view to the output context.</summary>
    Task RenderAsync(ITuiRenderContext context, CancellationToken ct = default);

    /// <summary>Handle a key press when this view has focus. Returns true if handled.</summary>
    bool HandleKey(KeyPress key) => false;

    /// <summary>Optional: handle an agent event (e.g. update displayed tokens).</summary>
    Task OnEventAsync(AgentEvent @event, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Strongly-typed view base.
/// </summary>
public abstract class TuiViewBase<TViewModel> : ITuiView
    where TViewModel : class, ITuiViewModel
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract TuiViewPlacement Placement { get; }

    public TViewModel? ViewModel { get; set; }
    ITuiViewModel? ITuiView.ViewModel
    {
        get => ViewModel;
        set => ViewModel = value as TViewModel;
    }

    public abstract Task RenderAsync(ITuiRenderContext context, CancellationToken ct = default);

    public virtual bool HandleKey(KeyPress key) => false;
    public virtual Task OnEventAsync(AgentEvent @event, CancellationToken ct = default) => Task.CompletedTask;

    public virtual void Dispose() { }
}

/// <summary>
/// Where a view appears in the layout.
/// </summary>
public enum TuiViewPlacement
{
    /// <summary>Top status bar (single line).</summary>
    StatusBar,

    /// <summary>Main chat history area.</summary>
    ChatHistory,

    /// <summary>Bottom input editor.</summary>
    Input,

    /// <summary>Footer (single line, below input).</summary>
    Footer,

    /// <summary>Floating overlay (modal).</summary>
    Overlay,

    /// <summary>Right sidebar.</summary>
    SidebarRight,

    /// <summary>Left sidebar.</summary>
    SidebarLeft,
}
