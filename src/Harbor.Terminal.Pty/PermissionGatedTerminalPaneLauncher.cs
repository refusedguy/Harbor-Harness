using CSharpFunctionalExtensions;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Terminal;

namespace Harbor.Terminal.Pty;

/// <summary>
///     <see cref="ITerminalPaneLauncher" /> over a real PTY, gated by
///     <see cref="PermissionRuleset" />. Issue #672: the spawn that
///     <c>TerminalPaneViewModel</c> used to perform itself now happens here, in
///     Infrastructure, behind a seam the ViewModel cannot bypass.
/// </summary>
/// <remarks>
/// <para>
///     <b>Why the spawn lives here and not in Application.</b> The process spawn
///     was already in this assembly — <see cref="PtyProcess" /> has no other home —
///     and docs/ARCHITECTURE_LAYERS.md §3 assigns subprocess to Infrastructure.
///     Application cannot reference this assembly (it is a layer above), so the
///     alternative was to fork a second spawn path, which is the duplication this
///     issue exists to remove.
/// </para>
/// <para>
///     <b>The gate, and why it is two rulesets and not one.</b> A launch is refused
///     unless <c>terminal</c> evaluates to <see cref="PermissionAction.Allow" />:
///     the pane's own knob, with no safety policy attached, so an explicit
///     <c>{"terminal": {"*": "allow"}}</c> is honoured predictably. It is
///     deliberately NOT spelled <c>bash</c>: <c>BashSafetyPolicy</c> allows a
///     command only when the whole argv matches an allow rule token-for-token, and
///     an interactive shell is a program launch rather than a command line, so a
///     <c>bash</c> rule could not enable it except by naming the exact shell path.
///     Because that trade would have left a footgun — deny <c>bash</c>, allow
///     <c>terminal</c>, and the agent is fenced out while the desktop app is not —
///     the same command line is ALSO evaluated as <c>bash</c>, and a
///     <see cref="PermissionAction.Deny" /> there is final. Denying shells denies
///     the pane, whatever the pane's own rule says.
/// </para>
/// <para>
///     <b>There is no bypass.</b> This is the only implementation of the contract
///     the composition root registers, and it has no ungated entry point: the
///     refusal happens before <see cref="PtyProcess.TryStart" /> is reached.
/// </para>
/// </remarks>
public sealed class PermissionGatedTerminalPaneLauncher : ITerminalPaneLauncher
{
    /// <summary>Permission name this launcher requires an explicit <c>Allow</c> for.</summary>
    public const string PanePermission = "terminal";

    /// <summary>Permission name whose <c>Deny</c> is final, whatever the pane rule says.</summary>
    private const string ShellPermission = "bash";

    private readonly PermissionRuleset _ruleset;

    /// <summary>Construct a launcher that gates on <paramref name="ruleset" />.</summary>
    /// <param name="ruleset">
    ///     The ruleset to consult. Required: a launcher with no ruleset would have
    ///     to guess, and guessing "allowed" is the bug this issue is about.
    /// </param>
    public PermissionGatedTerminalPaneLauncher(PermissionRuleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        _ruleset = ruleset;
    }

    /// <inheritdoc />
    public Result<ITerminalPane> Launch(
        TerminalPaneRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (cancellationToken.IsCancellationRequested)
        {
            return Result.Failure<ITerminalPane>(
                "terminal-cancelled: the launch was cancelled before a session was started; "
                + "no process was started.");
        }

        string commandLine = RenderCommandLine(request);

        // A shell denial is final and is checked FIRST, so the refusal a user reads
        // names the stronger reason when both rulesets object.
        PermissionAction shellVerdict = _ruleset.Evaluate(ShellPermission, commandLine);
        if (shellVerdict == PermissionAction.Deny)
        {
            return Denied(commandLine, PanePermission, PermissionAction.Deny, "the shell rules deny it");
        }

        PermissionAction paneVerdict = _ruleset.Evaluate(PanePermission, commandLine);
        if (paneVerdict != PermissionAction.Allow)
        {
            return Denied(commandLine, PanePermission, paneVerdict, "it is not explicitly allowed");
        }

        Result<PtyProcess> started = PtyProcess.TryStart(new PtyStartSpec(
            request.Shell,
            request.Args,
            request.WorkingDirectory,
            Cols: request.Cols,
            Rows: request.Rows));

        // Explicit early return rather than a ternary: the CFE0001 guard cannot see
        // that a ternary's `started.Value` is reached only on the IsFailure == false
        // branch, and shipped code is held to that guard at error severity (§ROP).
        if (started.IsFailure)
        {
            return started.ConvertFailure<ITerminalPane>();
        }

        return Result.Success<ITerminalPane>(new PtyTerminalPane(started.Value));
    }

    /// <summary>
    ///     The refusal text. It names the permission and the verdict because the
    ///     pane renders this verbatim — a refusal the user cannot act on is a
    ///     support ticket.
    /// </summary>
    private static Result<ITerminalPane> Denied(
        string commandLine,
        string permission,
        PermissionAction verdict,
        string reason) =>
        Result.Failure<ITerminalPane>(
            $"terminal-denied: launching `{commandLine}` was refused because {reason} "
            + $"(permission '{permission}' evaluated to {verdict}); no process was started. "
            + $"Grant it with {{\"{permission}\": {{\"*\": \"allow\"}}}} in your Harbor permission config.");

    /// <summary>
    ///     The command line the ruleset is asked about — the same string the child
    ///     is started with, so the gate and the process cannot drift apart.
    /// </summary>
    private static string RenderCommandLine(TerminalPaneRequest request)
    {
        var commandLine = new System.Text.StringBuilder(request.Shell);
        foreach (string arg in request.Args)
        {
            commandLine.Append(' ').Append(arg);
        }

        return commandLine.ToString();
    }
}
