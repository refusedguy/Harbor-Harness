namespace Harbor.Abstractions.Notifications;

/// <summary>
///     Runs one desktop-notifier process for a notification backend (issue #665).
/// </summary>
/// <remarks>
/// <para>
///     <b>Why this contract exists.</b> The three OS backends in
///     <c>Harbor.Tui.Notifications</c> each built a
///     <c>System.Diagnostics.ProcessStartInfo</c> and called
///     <c>Process.Start</c> — a Presentation assembly forking
///     <c>notify-send</c> / <c>osascript</c> / <c>msg</c>, three times over.
///     <c>INotificationBackend</c> already sat in the same file and already
///     abstracted the three platforms; what it abstracted was the SHAPE of the
///     call, not its EXECUTION. An interface over shape with no seam underneath
///     it buys nothing that three same-signature classes do not, which is why
///     the defect survived <c>PRESENTATION-MUST-NOT-SPAWN-SUBPROCESSES</c>
///     behind three baseline rows.
/// </para>
/// <para>
///     This is the same move as <c>IGitQuery</c> / <c>ProcessGitQuery</c>
///     (#537): a narrow contract in Domain, the spawn in Application beside
///     <c>BashTool</c>'s neighbours. The backends become injectable, the process
///     lifetime belongs to whoever started it, and the <c>ct</c> that
///     <c>IAgentEventHandler.HandleAsync</c> is already handed stops being
///     dropped on the floor.
/// </para>
/// <para>
///     <b>Not an <c>ITool</c>, deliberately.</b> Desktop notification is UI
///     chrome fired on agent events, not a capability the model asked for: the
///     argument vectors are fixed per platform and the caller is a renderer, not
///     an agent turn. Gating it through <c>PermissionRuleset</c> would mean
///     asking the user to approve a toast about the run they started, and
///     <c>INotificationBackend.Notify</c> is the narrower thing to keep honest:
///     it takes a title, a body and a severity — never a command string. There
///     is no overload through which a model-authored command could reach this
///     contract, and
///     <c>NotificationProcessSeamTests</c> pins the exact argv of each platform
///     so that stays a property of the code rather than a convention.
/// </para>
/// <para>
///     <b>Best effort by contract.</b> A notifier that is missing, times out or
///     exits non-zero is an expected outcome, not an error the agent should see:
///     the implementation logs it and returns. The one thing a caller may rely
///     on is that <see cref="Run" /> does not throw for any of those, so a
///     notification can never take the renderer down with it.
/// </para>
/// </remarks>
public interface INotificationProcessRunner
{
    /// <summary>
    ///     Starts <paramref name="fileName" /> with <paramref name="arguments" />,
    ///     waits for it, and returns once it has exited or the internal timeout
    ///     has elapsed.
    /// </summary>
    /// <remarks>
    ///     Arguments are passed as separate argv entries, never concatenated into
    ///     a command string — there is no shell here and nothing to quote
    ///     against. A cancelled <paramref name="cancellationToken" /> means
    ///     nothing is started at all.
    /// </remarks>
    /// <param name="fileName">Executable to start — for example <c>notify-send</c>.</param>
    /// <param name="arguments">Argument vector, in order.</param>
    /// <param name="cancellationToken">Cancels the launch and the wait.</param>
    void Run(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}
