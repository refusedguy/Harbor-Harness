using Harbor.Ui.Framework.State;
namespace Harbor.Ui.Framework.Sessions;
/// <summary>
///     Platform-agnostic contract for binding the active chat view-model
///     to a different session's <see cref="UiStore" />. Implemented by
///     each desktop app (Avalonia / WPF / MAUI / Blazor) to wrap the
///     platform-specific dispatcher + chat-VM lookup in a single call.
/// </summary>
public interface IChatViewBinder
{
    /// <summary>
    ///     Rebind the active chat view-model to a different session's
    ///     <see cref="UiStore" />. Implementations MUST marshal the call
    ///     onto the UI thread (e.g. <c>Dispatcher.UIThread.Post</c> on
    ///     Avalonia) because <c>RebindToStore</c> mutates
    ///     <c>ObservableCollection</c>s bound to the view.
    /// </summary>
    /// <param name="store">The new session's UiStore.</param>
    public void Rebind(UiStore store);
}
