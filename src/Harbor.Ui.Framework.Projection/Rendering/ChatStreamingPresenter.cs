using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.State;

namespace Harbor.Ui.Framework.Rendering;
/// <summary>
///     Derives session status and streaming-presentation state from a
///     <see cref="UiState" /> snapshot. Registered as a singleton in
///     <c>AppHost</c>.
/// </summary>
public sealed class ChatStreamingPresenter
{
    /// <summary>
    ///     Derive the session status from a <see cref="UiState" />
    ///     snapshot. Used by <c>ChatViewModel</c> (and tests) to push
    ///     the active session's status dot to the sidebar.
    ///     <para>
    ///         <b>Rules:</b>
    ///         <list type="bullet">
    ///             <item><c>IsAgentRunning</c> → <see cref="SessionStatus.Working" />.</item>
    ///             <item>Status=="error" → <see cref="SessionStatus.Error" />.</item>
    ///             <item>Last line is assistant → <see cref="SessionStatus.Done" />.</item>
    ///             <item>Otherwise → <see cref="SessionStatus.Idle" />.</item>
    ///         </list>
    ///     </para>
    /// </summary>
    /// <param name="state">The current UiState.</param>
    /// <returns>The derived <see cref="SessionStatus" />.</returns>
    public SessionStatus DeriveStatus(UiState state)
    {
        if (state.IsAgentRunning) return SessionStatus.Working;
        if (string.Equals(state.Status, "error", StringComparison.OrdinalIgnoreCase))
            return SessionStatus.Error;
        if (state.Lines.Length > 0 && state.Lines[^1].Role == ChatRole.Assistant)
            return SessionStatus.Done;
        return SessionStatus.Idle;
    }
}
