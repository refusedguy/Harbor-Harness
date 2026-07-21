using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.State;
namespace Harbor.Ui.Framework.Rendering;
/// <summary>
///     Owns the streaming-buffer presentation state derived from a
///     <see cref="UiState" /> snapshot: <see cref="IsStreaming" />,
///     <see cref="IsThinking" />, <see cref="IsAgentRunning" />,
///     <see cref="StatusMessage" />, and <see cref="StreamingBuffer" />.
/// </summary>
/// <remarks>
///     Extracted from <c>ChatViewModel</c> so the streaming-state
///     derivation (a pure function of <see cref="UiState" />) can be
///     unit-tested without spinning up an Avalonia dispatcher or
///     observable object. Registered as a singleton in <c>AppHost</c>.
/// </remarks>
public sealed class ChatStreamingPresenter
{
    /// <summary>
    ///     Compute the streaming-presentation state from a
    ///     <see cref="UiState" /> snapshot and push it into the
    ///     supplied setters.
    /// </summary>
    /// <param name="state">The current UiState.</param>
    /// <param name="setStreaming">Setter for <c>IsStreaming</c>.</param>
    /// <param name="setThinking">Setter for <c>IsThinking</c>.</param>
    /// <param name="setAgentRunning">Setter for <c>IsAgentRunning</c>.</param>
    /// <param name="setStatusMessage">Setter for <c>StatusMessage</c>.</param>
    /// <param name="setStreamingBuffer">Setter for <c>StreamingBuffer</c>.</param>
    public void Apply(
        UiState state,
        Action<bool> setStreaming,
        Action<bool> setThinking,
        Action<bool> setAgentRunning,
        Action<string> setStatusMessage,
        Action<string> setStreamingBuffer)
    {
        setStreaming(state.IsStreaming);
        setThinking(state.IsAgentRunning && !state.IsStreaming);
        setAgentRunning(state.IsAgentRunning);

        setStatusMessage(state.IsAgentRunning
            ? state.IsStreaming ? "Streaming response…" : "Agent is running…"
            : string.Empty);
        setStreamingBuffer(state.Active.TextBuffer ?? string.Empty);
    }

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
