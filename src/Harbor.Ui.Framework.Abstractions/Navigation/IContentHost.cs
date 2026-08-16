using System.Collections.Generic;

namespace Harbor.Ui.Framework.Navigation;

/// <summary>
///     Renderer-agnostic contract for the shell's active-content host.
///     Implementations resolve the current page/view model and expose it
///     through <see cref="ActiveView"/>. Desktop GUIs bind a ContentControl
///     to <see cref="ActiveView"/>; terminal TUIs may render the active view
///     directly.
/// </summary>
public interface IContentHost
{
    /// <summary>
    ///     Gets the currently active view model or view, or <c>null</c> when
    ///     no content is selected.
    /// </summary>
    object? ActiveView { get; }

    /// <summary>
    ///     Switches the active content to the route identified by
    ///     <paramref name="route"/>. If the route is unknown, the host
    ///     logs a warning and leaves the active view unchanged.
    /// </summary>
    void NavigateTo(string route);

    /// <summary>
    ///     Attempts to switch the active content to <paramref name="route"/>.
    ///     Returns <c>true</c> if the route is known and navigation succeeded;
    ///     <c>false</c> otherwise.
    /// </summary>
    bool TryNavigate(string route);

    /// <summary>
    ///     Gets the collection of route strings known to this host.
    /// </summary>
    IReadOnlyList<string> AvailableRoutes { get; }
}
