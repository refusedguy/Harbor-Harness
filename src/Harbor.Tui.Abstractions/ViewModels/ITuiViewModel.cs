using System.ComponentModel;
using Harbor.Abstractions.Events;
namespace Harbor.Tui.Abstractions.ViewModels;
/// <summary>
///     Base view model contract — state + logic for a view.
///     Implements the ViewModel part of MVVM. ViewModels are testable,
///     renderer-agnostic, and notify views of state changes via INotifyPropertyChanged.
/// </summary>
/// <remarks>
///     <para>
///         View models are the single subscription point for agent events in the TUI. The base
///         renderer fans every <see cref="AgentEvent" /> out to all registered view models via
///         <see cref="UpdateFromEventAsync" />; views then read VM state at render time.
///     </para>
///     <para>
///         Implementations SHOULD use <c>CommunityToolkit.Mvvm</c>'s <c>ObservableObject</c> base
///         class and the <c>[ObservableProperty]</c> source generator to get INPC for free.
///     </para>
/// </remarks>
public interface ITuiViewModel : INotifyPropertyChanged
{
    /// <summary>
    ///     Unique ID for binding. Must match the <see cref="ITuiView.Id" /> of the view this view
    ///     model binds to.
    /// </summary>
    public string Id { get; }

    /// <summary>
    ///     Update state from an agent event. Called by the base renderer for every event.
    /// </summary>
    /// <param name="event">The event to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task UpdateFromEventAsync(AgentEvent @event, CancellationToken ct = default);
}

/// <summary>
///     Marker attribute to bind a view model property to a specific view by ID.
///     Used by the view registry for declarative binding.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class BindsToViewAttribute : Attribute
{

    /// <summary>
    ///     Construct a <see cref="BindsToViewAttribute" /> for the given view id.
    /// </summary>
    /// <param name="viewId">The view id to bind to.</param>
    public BindsToViewAttribute(string viewId)
    {
        ViewId = viewId;
    }
    /// <summary>
    ///     The view id this property is bound to.
    /// </summary>
    public string ViewId { get; }
}
