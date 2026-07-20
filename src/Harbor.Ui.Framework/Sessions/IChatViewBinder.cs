using Harbor.Ui.Framework.Sessions;
using Harbor.Ui.Framework.State;

namespace Harbor.Ui.Framework.Sessions;

/// <summary>
///     Platform-agnostic contract for binding the active chat view-model
///     to a different session's <see cref="UiStore"/>. Implemented by
///     each desktop app (Avalonia / WPF / MAUI / Blazor) to wrap the
///     platform-specific dispatcher + chat-VM lookup in a single call.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists:</b> <c>SessionManager</c> (in Ui.Framework)
///         used to call <c>_services.GetService&lt;ChatViewModel&gt;()</c>
///         directly, which forced it to live in
///         <c>Harbor.App.Avalonia</c> (because <c>ChatViewModel</c> is
///         Avalonia-specific). Extracting this interface lets the
///         session-orchestration logic live in the framework layer where
///         it belongs, while each platform wires its own
///         <c>ChatViewModel</c> + dispatcher through this narrow seam.
///     </para>
///     <para>
///         <b>Rendered-line-count:</b> the binder also exposes the
///         current rendered-line-count snapshot via
///         <see cref="GetRenderedLineCount"/>. <c>SessionManager</c>
///         saves this into <see cref="SessionContext.RenderedLineCount"/>
///         on every session switch so that switching back resumes
///         rendering at the right offset (otherwise the renderer would
///         re-append every line in the transcript on each switch).
///     </para>
/// </remarks>
public interface IChatViewBinder
{
    /// <summary>
    ///     Get the current rendered-line-count from the active chat
    ///     view-model, or 0 if no chat VM is bound yet.
    /// </summary>
    int GetRenderedLineCount();

    /// <summary>
    ///     Rebind the active chat view-model to a different session's
    ///     <see cref="UiStore"/>. Implementations MUST marshal the call
    ///     onto the UI thread (e.g. <c>Dispatcher.UIThread.Post</c> on
    ///     Avalonia) because <c>RebindToStore</c> mutates
    ///     <c>ObservableCollection</c>s bound to the view.
    /// </summary>
    /// <param name="store">The new session's UiStore.</param>
    /// <param name="savedRenderedLineCount">
    ///     The rendered-line-count snapshot saved when the session was
    ///     previously switched away from (0 if first visit).
    /// </param>
    void Rebind(UiStore store, int savedRenderedLineCount);
}
