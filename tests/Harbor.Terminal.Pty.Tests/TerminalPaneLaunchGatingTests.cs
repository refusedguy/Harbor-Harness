using CSharpFunctionalExtensions;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Terminal;
using Harbor.Terminal.Pty;

namespace Harbor.Terminal.Pty.Tests;

/// <summary>
///     Issue #672: the floating terminal pane used to fork <c>$SHELL -i</c> from a
///     Presentation assembly, outside the <c>ITool</c>/<c>PermissionRuleset</c> seam.
///     The spawn now lives behind <see cref="ITerminalPaneLauncher" />, and this
///     suite pins the property the refactor was for: <b>a launch that is not
///     permitted does not start a process</b>.
/// </summary>
/// <remarks>
/// <para>
///     Modelled on <c>GitServicePermissionGatingTests</c> (#537), with one deliberate
///     difference in the DECISION it pins. The git badge is read-only chrome the
///     user opened, and is deliberately NOT gated. An interactive shell is the most
///     privileged thing this harness can start — it carries the full inherited
///     environment and waits for input — so the pane IS gated, and the default is
///     refusal. Both halves are asserted below so neither can quietly become the
///     other.
/// </para>
/// <para>
///     These tests assert the GATE, never a live shell: the denied cases must not
///     reach <c>posix_spawnp</c> at all, which is the whole claim. A test that
///     proved the gate by actually starting a shell would prove it by starting a
///     shell, which is the thing under suspicion.
/// </para>
/// </remarks>
public class TerminalPaneLaunchGatingTests
{
    private static readonly TerminalPaneRequest Request =
        TerminalPaneRequest.InteractiveShell("/tmp", "/bin/sh");

    private static PermissionRuleset Allowing() => new(
    [
        new(PermissionGatedTerminalPaneLauncher.PanePermission, "*", PermissionAction.Allow),
    ]);

    // ── the gate: no permission, no process ───────────────────────────────

    [Test]
    public async Task Launch_WithoutPermission_IsRefused()
    {
        // Empty ruleset: every action falls through to Ask. This is the state the
        // desktop composition root ships in, and the case #672 is about — a pane
        // that used to start unconditionally.
        var launcher = new PermissionGatedTerminalPaneLauncher(PermissionRuleset.Empty);

        Result<ITerminalPane> result = launcher.Launch(Request);

        await Assert.That(result.IsFailure).IsTrue()
            .Because(
                "an interactive shell is the most privileged launch this harness performs; with no "
                + "permission rule granting it, the launcher must refuse BEFORE any spawn is attempted");
        await Assert.That(result.Error).Contains("terminal-denied", StringComparison.Ordinal)
            .Because("the refusal must be identifiable, and the pane renders this string verbatim");
        await Assert.That(result.Error).Contains("no process was started", StringComparison.Ordinal)
            .Because("the refusal message is the only thing the user sees; it must state the outcome");
    }

    [Test]
    public async Task Launch_DefaultRuleset_Refuses_The_Pane()
    {
        // The shipped default. PermissionRuleset.Default has no `terminal` rule, and
        // an unmatched permission is Ask — which this launcher treats as a refusal.
        // Pinned so "the pane is denied until someone opts in" cannot become "the
        // pane is allowed unless someone objects".
        var launcher = new PermissionGatedTerminalPaneLauncher(PermissionRuleset.Default);

        Result<ITerminalPane> result = launcher.Launch(Request);

        await Assert.That(result.IsFailure).IsTrue()
            .Because(
                "PermissionRuleset.Default grants no `terminal` rule. Before #672 the pane started anyway; "
                + "the point of the seam is that it no longer does");
    }

    [Test]
    public async Task Launch_PaneAllowed_But_ShellDenied_IsStillRefused()
    {
        // The footgun this design closes: a `terminal` allow must not become a way
        // to reach a shell the operator has denied. A shell denial is final and is
        // checked first, so the message names the stronger reason.
        var launcher = new PermissionGatedTerminalPaneLauncher(new PermissionRuleset(
        [
            new(PermissionGatedTerminalPaneLauncher.PanePermission, "*", PermissionAction.Allow),
            new("bash", "*", PermissionAction.Deny),
        ]));

        Result<ITerminalPane> result = launcher.Launch(Request);

        await Assert.That(result.IsFailure).IsTrue()
            .Because(
                "denying `bash` must deny the terminal pane too — otherwise the desktop app becomes an "
                + "unfenced shell the moment anyone adds a `terminal` allow");
        await Assert.That(result.Error).Contains("Deny", StringComparison.Ordinal)
            .Because("the refusal must report the verdict that actually stopped the launch");
    }

    [Test]
    public async Task Launch_ShellDeniedByCommandShape_Refuses_The_Pane()
    {
        // The command-shape guard for `bash` (BashSafetyPolicy.PreEvaluate) runs
        // before the rule walk and denies destructive commands however they are
        // spelled. `rm -rf /` is its canonical case, and it reaches the pane even
        // when the pane's own rule allows everything and no `bash` rule exists.
        var launcher = new PermissionGatedTerminalPaneLauncher(new PermissionRuleset(
        [
            new(PermissionGatedTerminalPaneLauncher.PanePermission, "*", PermissionAction.Allow),
        ]));

        Result<ITerminalPane> result = launcher.Launch(
            TerminalPaneRequest.InteractiveShell("/tmp", "/bin/rm -rf /"));

        await Assert.That(result.IsFailure).IsTrue()
            .Because(
                "a destructive command is denied before any rule walk, so an allow-everything `terminal` "
                + "rule cannot be used to launch one");
        await Assert.That(result.Error).Contains("Deny", StringComparison.Ordinal)
            .Because("the refusal must name the verdict that stopped the launch");
    }

    [Test]
    public async Task Launch_Cancelled_IsRefused_And_NeverReachesTheSpawn()
    {
        var launcher = new PermissionGatedTerminalPaneLauncher(Allowing());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Result<ITerminalPane> result = launcher.Launch(Request, cts.Token);

        await Assert.That(result.IsFailure).IsTrue()
            .Because("#672 named the missing cancellation point; a pre-cancelled launch must not spawn");
        await Assert.That(result.Error).Contains("terminal-cancelled", StringComparison.Ordinal);
    }

    // ── the seam is real: cancellation exists and the request is data ────

    [Test]
    public async Task Launch_NullRuleset_IsRejected_NotSilentlyAllowed()
    {
        // A launcher constructed with no ruleset would have to guess, and guessing
        // "allowed" is the bug this issue is about.
        await Assert.That(
                () => new PermissionGatedTerminalPaneLauncher(null!))
            .Throws<ArgumentNullException>()
            .Because("an ungated launcher must not be constructible");
    }

    [Test]
    public async Task Request_InteractiveShell_DeclaresTheArgv_Once()
    {
        // The `-i` is what makes this privileged, so it is stated once in the
        // contract and asserted here; a caller cannot quietly request a
        // non-interactive shell through the same helper.
        TerminalPaneRequest request =
            TerminalPaneRequest.InteractiveShell("/work", "/bin/zsh");

        await Assert.That(request.Shell).IsEqualTo("/bin/zsh");
        await Assert.That(request.Args).IsEquivalentTo(new[] { "-i" });
        await Assert.That(request.WorkingDirectory).IsEqualTo("/work");
    }

    // ── non-vacuity: the permitted path really does reach a spawn ────────

    [Test]
    public async Task Launch_Permitted_Reaches_The_Spawn_RatherThanRefusing_OnTheGate()
    {
        // The gate must not be a wall that refuses everything — a launcher that
        // always denies would satisfy every test above. When the ruleset permits,
        // the launcher must get as far as the PTY layer, and on a POSIX host that
        // means a real process; a missing binary then proves we crossed the gate
        // and failed LATER, at the spawn.
        if (!PtyProcess.IsSupported)
        {
            return;
        }

        var launcher = new PermissionGatedTerminalPaneLauncher(Allowing());
        Result<ITerminalPane> result =
            launcher.Launch(TerminalPaneRequest.InteractiveShell("/tmp", "harbor-no-such-pane-shell"));

        await Assert.That(result.IsFailure).IsTrue()
            .Because("that binary does not exist, so the spawn must fail");
        await Assert.That(result.Error).Contains("terminal-denied", StringComparison.Ordinal).IsFalse()
            .Because(
                "the failure must come from the SPAWN (posix_spawnp), not from the permission gate — "
                + "this is the assertion that proves the gate permits rather than blanket-denying");
    }
}
