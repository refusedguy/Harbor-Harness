using System.Collections.Immutable;
using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     Immutable UI state for the application chrome (navigation, modals, toasts).
/// </summary>
/// <remarks>
///     <para>
///         Produced only by <see cref="ChromeReducer" /> — never mutated inside a
///         renderer. Renderers project this into their framework-specific chrome
///         widgets (Avalonia, WPF, Blazor, SpectreTui).
///     </para>
///     <para>
///         Designed for NativeAOT and zero-reflection: all members are value types
///         or <see cref="ImmutableArray{T}" />. No <see cref="List{T}" />, no
///         reflection-based binding.
///     </para>
/// </remarks>
public sealed record ChromeViewState
{
    /// <summary>Id of the currently active session, or null if none.</summary>
    public SessionId? ActiveSessionId { get; init; }

    /// <summary>Navigation history stack. Top of stack is the current route.</summary>
    public ImmutableStack<Route> NavigationStack { get; init; } = ImmutableStack<Route>.Empty;

    /// <summary>Currently active modal, or null if no modal is shown.</summary>
    public Modal? ActiveModal { get; init; }

    /// <summary>Active toast notifications, oldest first.</summary>
    public ImmutableArray<Toast> Toasts { get; init; } = ImmutableArray<Toast>.Empty;

    /// <summary>
    ///     Push a new route onto the navigation stack, returning a new immutable snapshot.
    /// </summary>
    public ChromeViewState PushRoute(Route route) => this with { NavigationStack = NavigationStack.Push(route) };

    /// <summary>
    ///     Pop the current route from the navigation stack, returning a new immutable snapshot.
    /// </summary>
    public ChromeViewState PopRoute() => this with { NavigationStack = NavigationStack.Pop() };

    /// <summary>
    ///     Return a snapshot with a modal activated.
    /// </summary>
    public ChromeViewState ShowModal(Modal modal) => this with { ActiveModal = modal };

    /// <summary>
    ///     Return a snapshot with the active modal dismissed.
    /// </summary>
    public ChromeViewState DismissModal() => this with { ActiveModal = null };

    /// <summary>
    ///     Append a toast notification, returning a new immutable snapshot.
    /// </summary>
    public ChromeViewState AddToast(Toast toast) => this with { Toasts = Toasts.Add(toast) };

    /// <summary>
    ///     Discriminated union of navigation routes.
    /// </summary>
    public abstract record Route
    {
        /// <summary>Chat view for the given session.</summary>
        /// <param name="SessionId">The session to display.</param>
        public sealed record Chat(SessionId SessionId) : Route;

        /// <summary>Settings view.</summary>
        public sealed record Settings : Route;

        /// <summary>Agent log view.</summary>
        public sealed record AgentLog : Route;

        /// <summary>Provider picker view.</summary>
        public sealed record ProviderPicker : Route;

        /// <summary>Onboarding view.</summary>
        public sealed record Onboarding : Route;
    }

    /// <summary>
    ///     Discriminated union of modal dialogs.
    /// </summary>
    public abstract record Modal
    {
        /// <summary>Confirmation dialog.</summary>
        /// <param name="Title">Dialog title.</param>
        /// <param name="Message">Dialog message.</param>
        /// <param name="OnConfirm">Action id to dispatch on confirmation.</param>
        public sealed record Confirm(string Title, string Message, string OnConfirm) : Modal;

        /// <summary>Alert dialog.</summary>
        /// <param name="Title">Dialog title.</param>
        /// <param name="Message">Dialog message.</param>
        public sealed record Alert(string Title, string Message) : Modal;
    }

    /// <summary>
    ///     Toast notification.
    /// </summary>
    /// <param name="Message">Toast message text.</param>
    /// <param name="Severity">Toast severity level.</param>
    /// <param name="CreatedAt">UTC timestamp when the toast was created.</param>
    /// <param name="Id">Stable unique identifier for the toast.</param>
    public sealed record Toast(string Message, ToastSeverity Severity, DateTimeOffset CreatedAt, string Id);

    /// <summary>Toast severity levels.</summary>
    public enum ToastSeverity
    {
        Info,
        Success,
        Warning,
        Error
    }
}
