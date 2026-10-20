using System.Diagnostics;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Application.Sessions;
using Harbor.ChangeTracking;
using Harbor.TestKit;
using TUnit.Core.Enums;

namespace Harbor.ChangeTracking.Tests;

/// <summary>
///     S9 (#397): one end-to-end test driving the whole verified-change chain
///     against a throwaway repo — pin, isolate, a scripted agent write (no
///     LLM), freeze, checks, report, accept — plus the fail-closed edges.
///     Real git worktrees under an isolated <c>HARBOR_HOME</c>; the family
///     skips when git is missing (see <see cref="ChangeTrackingTestBase" />),
///     and the chain itself is Linux-only because the check step executes
///     POSIX binaries.
/// </summary>
[NotInParallel("harbor-home")]
[RunOn(OS.Linux)]
public class RunChangeChainTests : ChangeTrackingTestBase
{
    /// <inheritdoc />
    protected override (string Email, string Name) GitAuthor => ("s9@test", "s9");

    [Test]
    public async Task EndToEnd_PassChain_ReportThenAccept_OperatorTreeHasChange()
    {
        await WithIsolatedHome(async _ =>
        {
            string dir = InitRepo();
            try
            {
                string pinned = GitOut(dir, "rev-parse HEAD").Trim();

                // Pin (S1) + isolate (S2).
                Result<WorkspaceContract> inspected = await InspectContractAsync(dir);
                await Assert.That(inspected.IsSuccess).IsTrue();
                Result<IsolatedWorkspace> created = await MaterializeWorkspaceAsync(inspected);
                await Assert.That(created.IsSuccess).IsTrue();
                string runId = created.Value.RunId.Value;

                // Scripted agent (no LLM) writes one file in the isolated copy.
                await File.WriteAllTextAsync(Path.Combine(created.Value.Path, "a.txt"), "one\ntwo\n");

                // Freeze (S3).
                Result<FrozenChangeSet> frozen = await ChangeSetFreezer.FreezeAsync(created.Value);
                await Assert.That(frozen.IsSuccess).IsTrue();
                await Assert.That(frozen.Value.IsEmpty).IsFalse();
                await Assert.That(WorkspaceMaterializer.TryLoad(runId).Value.State).IsEqualTo(RunState.Changed);

                // Checks (S4): one trivial operator-declared check passes.
                Result<CheckReport> checks = await CheckRunner.RunAsync(
                    created.Value, frozen.Value.BaseRevision, frozen.Value.HeadRevision,
                    [new CheckSpec("true-check", "/usr/bin/true", [])]);
                await Assert.That(checks.IsSuccess).IsTrue();
                await Assert.That(checks.Value.AllPassed).IsTrue();
                await Assert.That(WorkspaceMaterializer.TryAdvanceState(runId, RunState.Checked).IsSuccess).IsTrue();

                // Report (S5): exit 0, manifest Reported, artifacts on disk.
                ReportResult reported = await RunReporter.ReportAsync(runId);
                await Assert.That(reported.Outcome).IsEqualTo(ReportOutcome.Reported);
                await Assert.That(reported.ExitCode).IsEqualTo(ChangeReport.ExitAllPassed);
                await Assert.That(WorkspaceMaterializer.TryLoad(runId).Value.State).IsEqualTo(RunState.Reported);

                // Accept (S6): the operator's tree contains the change.
                AcceptResult accepted = await ChangeApplier.AcceptAsync(runId);
                await Assert.That(accepted.Outcome).IsEqualTo(AcceptOutcome.Applied);
                await Assert.That(accepted.ExitCode).IsEqualTo(0);
                await Assert.That(File.ReadAllText(Path.Combine(dir, "a.txt"))).IsEqualTo("one\ntwo\n");
                await Assert.That(GitOut(dir, "rev-parse HEAD").Trim()).IsEqualTo(pinned);
                await Assert.That(GitOut(dir, "diff --cached --stat").Trim().Length > 0).IsTrue();
                await Assert.That(WorkspaceMaterializer.TryLoad(runId).Value.State).IsEqualTo(RunState.Accepted);

                // run list shows the run honestly at its terminal state.
                bool listed = false;
                IReadOnlyList<RunManifest> runs = WorkspaceMaterializer.ListRuns();
                for (int i = 0; i < runs.Count; i++)
                {
                    if (runs[i].RunId == runId && runs[i].State == RunState.Accepted)
                        listed = true;
                }
                await Assert.That(listed).IsTrue();

                // Illegal transitions are rejected with named reasons, not
                // silently ignored: accept is at-most-once ...
                AcceptResult second = await ChangeApplier.AcceptAsync(runId);
                await Assert.That(second.Outcome).IsEqualTo(AcceptOutcome.Refused);
                await Assert.That(second.ExitCode).IsEqualTo(4);
                await Assert.That(second.Message).Contains("terminal");

                // ... and reject --undo-apply (S7 remainder) still fails
                // closed: nothing is reversed, the tree keeps the change.
                RejectResult undo = RunReject.Decide(runId, RunReject.UndoApplyFlag);
                await Assert.That(undo.Outcome).IsEqualTo(RejectOutcome.NotImplemented);
                await Assert.That(undo.ExitCode).IsEqualTo(4);
                await Assert.That(File.ReadAllText(Path.Combine(dir, "a.txt"))).IsEqualTo("one\ntwo\n");
            }
            finally
            {
                DeleteDir(dir);
            }
        });
    }

    [Test]
    public async Task EndToEnd_FailedCheck_StillReported_AcceptStaysOperatorChoice()
    {
        await WithIsolatedHome(async _ =>
        {
            string dir = InitRepo();
            try
            {
                Result<WorkspaceContract> inspected = await InspectContractAsync(dir);
                await Assert.That(inspected.IsSuccess).IsTrue();
                Result<IsolatedWorkspace> created = await MaterializeWorkspaceAsync(inspected);
                await Assert.That(created.IsSuccess).IsTrue();
                string runId = created.Value.RunId.Value;

                await File.WriteAllTextAsync(Path.Combine(created.Value.Path, "a.txt"), "one\ntwo\n");

                Result<FrozenChangeSet> frozen = await ChangeSetFreezer.FreezeAsync(created.Value);
                await Assert.That(frozen.IsSuccess).IsTrue();
                Result<CheckReport> checks = await CheckRunner.RunAsync(
                    created.Value, frozen.Value.BaseRevision, frozen.Value.HeadRevision,
                    [new CheckSpec("false-check", "/usr/bin/false", [])]);
                await Assert.That(checks.IsSuccess).IsTrue();
                await Assert.That(WorkspaceMaterializer.TryAdvanceState(runId, RunState.Checked).IsSuccess).IsTrue();

                // A failed check still produces a report (exit 1, never a
                // skip to "success").
                ReportResult reported = await RunReporter.ReportAsync(runId);
                await Assert.That(reported.Outcome).IsEqualTo(ReportOutcome.Reported);
                await Assert.That(reported.ExitCode).IsEqualTo(ChangeReport.ExitCheckFailed);
                await Assert.That(reported.Text!.Contains("false-check")).IsTrue();

                // The report informs; the operator still decides: accept
                // behind the base-unchanged gate applies.
                AcceptResult accepted = await ChangeApplier.AcceptAsync(runId);
                await Assert.That(accepted.Outcome).IsEqualTo(AcceptOutcome.Applied);
                await Assert.That(File.ReadAllText(Path.Combine(dir, "a.txt"))).IsEqualTo("one\ntwo\n");
            }
            finally
            {
                DeleteDir(dir);
            }
        });
    }

    // The HARBOR_HOME swap stays HERE, in the test class, on purpose: see
    // ChangeTrackingTestBase — ProcessEnvIsolationRule derives its writer set
    // and its clean-writer floor from the classes that write the process
    // environment, and this class is one of them.
    private static async Task WithIsolatedHome(Func<string, Task> body)
    {
        using var home = new TempDirScope("harbor-home");
        await EnvScope.WithAsync("HARBOR_HOME", home.Path, () => body(home.Path));
    }
}
