// AvaloniaTerminalPaneSpawnRules.cs — source-level guard for issue #672:
// TerminalPaneViewModel forked a real interactive shell (`$SHELL -i`, full
// inherited environment) straight out of a Presentation assembly, with no
// permission gate, no cancellation point and no seam to fake.
//
// THE PRECEDENT THIS FOLLOWS
// --------------------------
// #537 moved `GitService` out of Presentation for exactly this reason and left
// two pieces of machinery behind: the architectural half (Presentation no
// longer forks) and the DECISION half, pinned by `GitServicePermissionGatingTests`
// so "is the UI-chrome git read gated?" cannot quietly become an oversight. This
// file is the architectural half for the terminal pane, and it is a SOURCE-TEXT
// rule for the same reason `AvaloniaFireAndForgetRules` (#569) is: the rule spans
// `apps/Harbor.App.Avalonia`, which this test project does not reference (and must
// not — it is an app, a composition root). A source scan needs no reference edge.
//
// WHY AN EXCEPTION ROW IS NOT THE ANSWER HERE
// -------------------------------------------
// `PresentationCapabilityRules.KnownViolations` grandfathers a real violation
// against an IL probe that reads `System.Diagnostics.Process` in every method
// body. The terminal pane is NOT that shape and must not be given one:
//
//   * `PtyProcess` P/Invokes `posix_openpt`/`posix_spawn` (NativeMethods.cs). It
//     never names `System.Diagnostics.Process`, so the IL probe would not see a
//     hit even if the app assembly were scanned — the capability probe is blind
//     to it, which is precisely why this violation survived #455.
//   * `Harbor.App.Avalonia` is not in `FullLayerMatrixTests.AllSrcAssemblies`
//     (that list is `src/`-only), so it is not a Presentation assembly the
//     capability probe walks at all. A baseline row there would be a row against
//     an assembly the probe never opens — a lie in a table whose whole purpose is
//     to be checkable.
//
// So: no exemption, here or there. The pane is rewritten to go through a seam,
// and this rule keeps it there. `PresentationCapabilityRules` needs no change and
// gains no row that would later have to be deleted.
//
// NON-VACUITY
// -----------
// A source guard that silently matches nothing is worse than no guard. Three
// defences, mirroring AvaloniaFireAndForgetRules:
//
//   * `Scanner_FindsTheGuardedProject` — the walk really finds the shell.
//   * `Detector_FiresOnAKnownFork_AndStaysQuietOnTheSeamCall` — the detector, in
//     isolation, fires on the exact pre-#672 shape and is quiet on the post-#672
//     shape AND on the unrelated `_ =` style discards that legitimately exist.
//   * `Detector_IsNotDefeatedByRenamingTheLocal` — pins that the rule keys on the
//     spawn call, not on a variable name, so it cannot be defeated by a rename.
//
// MECHANISM (#1086, step 2, conveyor)
// -----------------------------------
// The rule below is a ScanRule: one banned shape (the two forbidden patterns
// merged into one alternation), no baseline, five planted controls, a discovery
// floor. Enumeration, stripping, matching and the control/discovery verdicts are
// ScanRunner's; this file keeps the issue prose and the test names.
//
// The merge is mechanical, not a re-decision: the old detector reported one hit
// per line even when both patterns matched it (a single
// `PtyProcess.Start(new PtyStartSpec(...))` is one violation), and a single
// alternation over lines reports the same set — a line matches iff any branch
// matches. The planted pre-#672 snippet IS that double-matching line, so the
// control proves the merged shape reports it exactly once.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     §ARCH guard (issue #672): a Presentation assembly may not fork a terminal
///     session. The desktop floating-terminal pane goes through
///     <c>ITerminalPaneLauncher</c>, which is permission-gated and cancellable.
/// </summary>
public class AvaloniaTerminalPaneSpawnRules
{
    private const string SubId = "PTY-SPAWN";

    /// <summary>
    ///     The forbidden shapes: a direct <c>PtyProcess</c> spawn, or the
    ///     <c>PtyStartSpec</c> that describes one. Keyed on the spawn CALL rather
    ///     than on the type name, so binding the handle to a differently-named
    ///     local does not defeat it. <c>PtyStartSpec</c> is listed because
    ///     constructing one is already the business decision "here is the argv I
    ///     intend to execute", which is exactly what must not happen in a
    ///     ViewModel.
    /// </summary>
    private static readonly Regex ForbiddenShape = new(
        @"PtyProcess\s*\.\s*(TryStart|Start)\s*\("
        + @"|new\s+PtyStartSpec\s*\(",
        RegexOptions.Compiled);

    /// <summary>The rule as data: one banned shape, no baseline, five controls, a floor.</summary>
    private static readonly ScanRule Rule = new()
    {
        Id = "AvaloniaTerminalPaneSpawn",
        Trees = ["apps/Harbor.App.Avalonia"],
        Forbidden =
        [
            new ScanForbidden(
                SubId,
                ForbiddenShape,
                "go through ITerminalPaneLauncher (Harbor.Abstractions.Terminal), which the "
                + "container wires to a permission-gated, cancellable Infrastructure implementation."),
        ],
        Controls =
        [
            // The exact pre-#672 shape, verbatim from the old constructor. One line
            // matches BOTH branches; the merged shape reports it exactly once.
            new ScanControl("Known.cs", """
                public sealed partial class TerminalPaneViewModel
                {
                    private void Open(string cwd)
                    {
                        _pty = PtyProcess.Start(new PtyStartSpec(Title, Args: ["-i"], WorkingDirectory: cwd));
                    }
                }
                """, SubId),
            // The post-#672 shape: the ViewModel only ever names the abstraction.
            new ScanControl("Seam.cs", """
                public sealed partial class TerminalPaneViewModel
                {
                    private void Open(string cwd)
                    {
                        Result<ITerminalPane> started = _launcher.Launch(TerminalPaneRequest.ForDirectory(cwd));
                        if (started.IsFailure) { OutputText = started.Error; return; }
                        _pane = started.Value;
                    }
                }
                """, null),
            // A non-spawn discard, of which this repo has hundreds: the rule must not
            // fire on `_ =` alone, or it cries wolf and gets deleted.
            new ScanControl("Unrelated.cs", """
                public void Flush() => _ = _buffer.Append(text);
                public void Dispatch() => _ = _store.Dispatch(msg);
                """, null),
            // The old code could have been written with any local name. Keying the rule
            // on a variable name would have been the easy way to write this guard, and it
            // would have been worthless.
            new ScanControl("Renamed.cs", """
                public void Open(string cwd)
                {
                    var session = PtyProcess.TryStart(new PtyStartSpec(Shell, Args: ["-i"], WorkingDirectory: cwd));
                    _ = session;
                }
                """, SubId),
            // The refactor's own comments name the forbidden call. A guard that fails on
            // its own documentation is a guard nobody keeps.
            new ScanControl("Prose.cs", """
                // #672: this used to call PtyProcess.Start(new PtyStartSpec(...)) directly.
                /// <summary>Launches through the seam; never PtyProcess.Start here.</summary>
                public void Open() => _ = _launcher.Launch(request);
                """, null),
        ],
        MinHits = 50,
    };

    [Test]
    public async Task AvaloniaShell_DoesNot_ForkATerminalSession()
    {
        List<string> violations = ScanRunner.Evaluate(Rule);

        await Assert.That(violations).IsEmpty()
            .Because(
                "§ARCH (#672). A Presentation assembly may not fork a process: docs/ARCHITECTURE_LAYERS.md "
                + "§3 assigns subprocess to Infrastructure, and a UI that shells out on its own bypasses the "
                + "ITool/PermissionRuleset seam, so the launch is never gated, never cancellable and never "
                + "appears in a transcript. This is worse than the git case #537 fixed — the pane ran "
                + "`$SHELL -i` with the FULL inherited environment, not three read-only git arguments. "
                + "Go through ITerminalPaneLauncher (Harbor.Abstractions.Terminal), which the container "
                + "wires to a permission-gated, cancellable Infrastructure implementation. "
                + string.Join("\n", violations));
    }

    // ── non-vacuity ───────────────────────────────────────────────────────

    [Test]
    public async Task Scanner_FindsTheGuardedProject()
    {
        List<string> discovery = ScanRunner.CheckDiscovery(Rule);

        await Assert.That(discovery).IsEmpty()
            .Because(
                "The walk needs a repository root and must really find the guarded Avalonia shell — "
                + "well over 50 source files. "
                + "A near-zero count means the path is stale and the rule enforces nothing. "
                + string.Join("; ", discovery));
    }

    [Test]
    public async Task Detector_FiresOnAKnownFork_AndStaysQuietOnTheSeamCall()
    {
        // The pre-#672 spawn shape must be detected, the seam call is the shape the
        // issue converts TO (flagging it would forbid the fix), and a plain `_ =`
        // discard is not a process spawn. The snippets live on Rule.Controls, so the
        // control drives the REAL matcher rather than a second implementation of it.
        List<string> failures = ScanRunner.CheckControls(Rule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "the pre-#672 spawn shape must be detected, or the rule guards nothing; the seam call "
                + "must stay quiet, or the rule forbids the fix; a plain `_ =` discard is not a spawn. "
                + string.Join("; ", failures));
    }

    [Test]
    public async Task Detector_IsNotDefeatedByRenamingTheLocal()
    {
        // The rule keys on the spawn call, not on a variable name — otherwise a
        // rename silently un-guards the very line it was written for. Proved by the
        // Renamed.cs control on the rule, driven here through the same verdict.
        List<string> failures = ScanRunner.CheckControls(Rule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "the rule keys on the spawn call, not on a variable name — otherwise a rename silently "
                + "un-guards the very line it was written for. "
                + string.Join("; ", failures));
    }

    [Test]
    public async Task Detector_IgnoresTheExplanationInProse()
    {
        // Line comments are stripped before matching, so documenting the old shape
        // does not reintroduce it — proved by the Prose.cs control on the rule.
        List<string> failures = ScanRunner.CheckControls(Rule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "line comments are stripped before matching, so documenting the old shape does not "
                + "reintroduce it. "
                + string.Join("; ", failures));
    }

    /// <summary>
    ///     Every baseline row states why it is tolerated, in the row itself. Vacuous
    ///     while the table is empty, and deliberately so: it is wired from the first
    ///     row so the first row cannot skip the argument.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_AllHaveReasons()
    {
        List<string> failures = ScanRunner.CheckReasons(Rule);

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     Every baseline row must still correspond to a real hit, so the table
    ///     cannot rot into a blanket permission: fix the code without deleting the
    ///     row and this fails.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_Are_Not_Stale()
    {
        List<string> stale = ScanRunner.StaleBaselineKeys(
            Rule, ScanRunner.ReadSources(ScanRunner.ScopeFiles(Rule)));

        await Assert.That(stale).IsEmpty()
            .Because("a baseline row with no violation behind it is a permission for a "
                + "problem that no longer exists: " + string.Join(", ", stale));
    }
}
