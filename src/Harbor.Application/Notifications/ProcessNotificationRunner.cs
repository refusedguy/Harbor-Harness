using System.Diagnostics;
using Harbor.Abstractions.Notifications;
using Microsoft.Extensions.Logging;

namespace Harbor.Application.Notifications;

/// <summary>
///     <see cref="INotificationProcessRunner" /> over a real child process. The
///     spawn lives here, beside <c>ProcessGitQuery</c> and
///     <c>WorkspaceInspector</c>, so the Presentation assembly holding the OS
///     backends forks nothing (#665 /
///     <c>PRESENTATION-MUST-NOT-SPAWN-SUBPROCESSES</c>).
/// </summary>
/// <remarks>
/// <para>
///     This type is the only place in the notification path that knows what a
///     process is. It also owns the process LIFETIME, which the old in-backend
///     code did not: the backends started a <c>Process</c>, waited three seconds
///     and walked away, so a notifier that hung stayed hung and its handle was
///     left for the finalizer. Here the child is disposed deterministically, and
///     it is killed either when the timeout expires or when the caller's token
///     fires — the second case is the one the old code had no way to express,
///     since the token never reached it.
/// </para>
/// <para>
///     No stderr redirection, unlike the code this replaces. It was set there
///     and never read, which is strictly worse than not setting it: a notifier
///     chatty enough to fill the pipe blocks forever, the wait times out, and
///     the process is killed for writing a diagnostic nobody would have seen.
///     Letting the child inherit our stderr is both the simpler answer and the
///     more useful one — "libnotify not installed" reaches the log the way the
///     original author intended.
/// </para>
/// <para>
///     It is a plain runner, not an <c>ITool</c>: the agent's own command
///     execution goes through <c>bash</c> and <c>PermissionRuleset</c>, and
///     firing a notification about that run is not a capability the model
///     requested. See <see cref="INotificationProcessRunner" /> for why that
///     decision is safe to make here rather than in the gates.
/// </para>
/// </remarks>
public sealed class ProcessNotificationRunner(ILogger<ProcessNotificationRunner> logger) : INotificationProcessRunner
{
    /// <summary>
    ///     Ceiling on one notifier invocation. The old per-backend constant was
    ///     the same three seconds; it lives here now because this is the class
    ///     that can actually enforce it.
    /// </summary>
    private const int TimeoutMs = 3_000;

    /// <inheritdoc />
    public void Run(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            // The renderer is shutting down. Not an error, and not worth a
            // warning: there is nobody left to notify.
            logger.LogDebug("Not launching {FileName}: the caller was cancelled.", fileName);
            return;
        }

        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            using Process? process = Process.Start(psi);
            if (process is null)
            {
                logger.LogDebug("Not launching {FileName}: the OS started no process.", fileName);
                return;
            }

            // Cancellation kills the child rather than abandoning it — a renderer
            // on its way down must not leave a notifier running. Registering on
            // the token does that without making this async, and without the
            // sync-over-async a CancellationToken-aware WaitForExit would have
            // forced here: Process offers no token overload of the synchronous
            // WaitForExit(int) at all.
            using CancellationTokenRegistration onCancel =
                cancellationToken.Register(() => TryKill(process, fileName));

            if (process.WaitForExit(TimeoutMs))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    // The wait ended because the child was killed, not because it
                    // finished. Same shutdown as the pre-check above, one step later.
                    logger.LogDebug("Stopped waiting for {FileName}: the caller was cancelled.", fileName);
                }

                return;
            }

            TryKill(process, fileName);
            logger.LogDebug("{FileName} did not exit within {TimeoutMs}ms; killed.", fileName, TimeoutMs);
        }
        catch (OperationCanceledException ex)
        {
            // Unreachable through WaitForExit, which cannot throw it — but the
            // catch is the difference between "cancellation is handled" and
            // "cancellation is handled today, by an implementation detail of a
            // BCL overload". The exception is passed along because a catch clause
            // that logs without it is a diagnostic that cannot be acted on (S6667).
            logger.LogDebug(ex, "Stopped waiting for {FileName}: the caller was cancelled.", fileName);
        }
        catch (Exception ex)
        {
            // The notifier is missing, not executable, or otherwise refused to
            // start. Expected on a machine without libnotify / without a
            // notification daemon — never surfaced to the caller, because a
            // missing toast must not fail the run it was announcing.
            logger.LogWarning(ex, "Desktop notifier {FileName} failed; is it installed?", fileName);
        }
    }

    private void TryKill(Process process, string fileName)
    {
        try
        {
            process.Kill();
        }
        catch (Exception ex)
        {
            // Already gone, or we are not allowed to signal it. Nothing to do
            // either way — the caller's next action is unaffected.
            logger.LogDebug(ex, "Could not kill the timed-out {FileName} notifier.", fileName);
        }
    }
}
