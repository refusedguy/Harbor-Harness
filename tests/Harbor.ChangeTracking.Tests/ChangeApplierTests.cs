using System.Diagnostics;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Application.Sessions;
using Harbor.ChangeTracking;

namespace Harbor.ChangeTracking.Tests;

/// <summary>
///     S6 slice 1 (#382): the base-unchanged gate plus apply / conflict /
///     dry-run-cancel on top of <see cref="RunChangeTransitions" />. Pure
///     product seams only — the frozen <c>change.patch</c> is planted by hand
///     because S3 (#377) has not landed, and the manifest is walked
///     <c>Isolated -&gt; Changed -&gt; Checked -&gt; Reported</c> through the
///     public <c>TryAdvanceState</c> map instead of faking S3–S5.
/// </summary>
[SkipWhenGitMissing]
[NotInParallel("harbor-home")]
public class ChangeApplierTests : ChangeTrackingTestBase
{
    /// <inheritdoc />
    protected override (string Email, string Name) GitAuthor => ("s6@test", "s6");

    [Test]
    public async Task Applied_CleanBase_AppliesPatchAndMarksAccepted()
    {
        await WithIsolatedHome(async home =>
        {
            string dir = InitRepo();
            try
            {
                string runId = await MakeReportedRun(dir, home, "two\n");
                PlantPatch(home, runId, dir, "two\n");

                AcceptResult result = await ChangeApplier.AcceptAsync(runId);

                await Assert.That(result.Outcome).IsEqualTo(AcceptOutcome.Applied);
                await Assert.That(result.ExitCode).IsEqualTo(0);
                await Assert.That(File.ReadAllText(Path.Combine(dir, "a.txt"))).IsEqualTo("one\ntwo\n");

                // No commit was created: HEAD is the pinned base, the change is staged.
                string head = GitOut(dir, "rev-parse HEAD").Trim();
                Result<RunManifest> loaded = WorkspaceMaterializer.TryLoad(runId);
                await Assert.That(loaded.IsSuccess).IsTrue();
                await Assert.That(loaded.Value.State).IsEqualTo(RunState.Accepted);
                await Assert.That(loaded.Value.BaseRevision).IsEqualTo(head);
                string cached = GitOut(dir, "diff --cached --stat").Trim();
                await Assert.That(cached.Length > 0).IsTrue();

                // git diff in the operator's tree equals the patch intent.
                string diff = GitOut(dir, "diff HEAD -- a.txt");
                await Assert.That(diff).Contains("+two");

                string[] log = await File.ReadAllLinesAsync(AcceptLog(home, runId));
                await Assert.That(log.Length).IsEqualTo(1);
                await Assert.That(log[0]).Contains("outcome=Applied");
            }
            finally
            {
                DeleteDir(dir);
            }
        });
    }

    [Test]
    public async Task MovedBase_Conflict_NothingWritten()
    {
        await WithIsolatedHome(async home =>
        {
            string dir = InitRepo();
            try
            {
                string runId = await MakeReportedRun(dir, home, "two\n");
                PlantPatch(home, runId, dir, "two\n");
                string pinned = GitOut(dir, "rev-parse HEAD").Trim();

                File.WriteAllText(Path.Combine(dir, "a.txt"), "one\noperator\n");
                Git(dir, "add a.txt");
                Git(dir, "commit -m operator-moved-base");
                string moved = GitOut(dir, "rev-parse HEAD").Trim();

                AcceptResult result = await ChangeApplier.AcceptAsync(runId);

                await Assert.That(result.Outcome).IsEqualTo(AcceptOutcome.Conflict);
                await Assert.That(result.ExitCode).IsEqualTo(3);
                await Assert.That(result.Message).Contains("base moved");
                await Assert.That(result.Message).Contains(pinned);
                await Assert.That(result.Message).Contains(moved);
                await Assert.That(File.ReadAllText(Path.Combine(dir, "a.txt"))).IsEqualTo("one\noperator\n");

                Result<RunManifest> loaded = WorkspaceMaterializer.TryLoad(runId);
                await Assert.That(loaded.IsSuccess).IsTrue();
                await Assert.That(loaded.Value.State).IsEqualTo(RunState.Reported);
            }
            finally
            {
                DeleteDir(dir);
            }
        });
    }

    [Test]
    public async Task DirtyTree_Refused_NothingApplied()
    {
        await WithIsolatedHome(async home =>
        {
            string dir = InitRepo();
            try
            {
                string runId = await MakeReportedRun(dir, home, "two\n");
                PlantPatch(home, runId, dir, "two\n");

                File.WriteAllText(Path.Combine(dir, "a.txt"), "one\ndirty-edit\n");

                AcceptResult result = await ChangeApplier.AcceptAsync(runId);

                await Assert.That(result.Outcome).IsEqualTo(AcceptOutcome.Conflict);
                await Assert.That(result.ExitCode).IsEqualTo(3);
                await Assert.That(result.Message).Contains("a.txt");
                // The operator's own dirty edit is intact; the patch did not land.
                await Assert.That(File.ReadAllText(Path.Combine(dir, "a.txt"))).IsEqualTo("one\ndirty-edit\n");
            }
            finally
            {
                DeleteDir(dir);
            }
        });
    }

    [Test]
    public async Task DoubleAccept_SecondRefusedAtMostOnce()
    {
        await WithIsolatedHome(async home =>
        {
            string dir = InitRepo();
            try
            {
                string runId = await MakeReportedRun(dir, home, "two\n");
                PlantPatch(home, runId, dir, "two\n");

                AcceptResult first = await ChangeApplier.AcceptAsync(runId);
                await Assert.That(first.Outcome).IsEqualTo(AcceptOutcome.Applied);

                AcceptResult second = await ChangeApplier.AcceptAsync(runId);

                await Assert.That(second.Outcome).IsEqualTo(AcceptOutcome.Refused);
                await Assert.That(second.ExitCode).IsEqualTo(4);
                await Assert.That(second.Message).Contains("terminal");
                await Assert.That(File.ReadAllText(Path.Combine(dir, "a.txt"))).IsEqualTo("one\ntwo\n");
            }
            finally
            {
                DeleteDir(dir);
            }
        });
    }

    [Test]
    public async Task DryRun_PrintsCommandsAndWritesNothing()
    {
        await WithIsolatedHome(async home =>
        {
            string dir = InitRepo();
            try
            {
                string runId = await MakeReportedRun(dir, home, "two\n");
                PlantPatch(home, runId, dir, "two\n");

                AcceptResult result = await ChangeApplier.AcceptAsync(runId, dryRun: true);

                await Assert.That(result.Outcome).IsEqualTo(AcceptOutcome.Cancelled);
                await Assert.That(result.ExitCode).IsEqualTo(0);
                await Assert.That(result.Message).Contains("apply --index");
                await Assert.That(result.Message).Contains("Nothing written");
                await Assert.That(File.ReadAllText(Path.Combine(dir, "a.txt"))).IsEqualTo("one\n");
                await Assert.That(GitOut(dir, "status --porcelain").Trim().Length).IsEqualTo(0);

                Result<RunManifest> loaded = WorkspaceMaterializer.TryLoad(runId);
                await Assert.That(loaded.IsSuccess).IsTrue();
                await Assert.That(loaded.Value.State).IsEqualTo(RunState.Reported);

                string[] log = await File.ReadAllLinesAsync(AcceptLog(home, runId));
                await Assert.That(log.Length).IsEqualTo(1);
                await Assert.That(log[0]).Contains("outcome=Cancelled");
            }
            finally
            {
                DeleteDir(dir);
            }
        });
    }

    [Test]
    public async Task NonApplyingPatch_NoFalseSuccess_TreeUnchanged()
    {
        await WithIsolatedHome(async home =>
        {
            string dir = InitRepo();
            try
            {
                string runId = await MakeReportedRun(dir, home, "two\n");
                await File.WriteAllTextAsync(
                    Path.Combine(home, "runs", runId, ChangeApplier.ChangePatchFileName),
                    "not a patch at all\n");

                AcceptResult result = await ChangeApplier.AcceptAsync(runId);

                await Assert.That(result.Outcome == AcceptOutcome.Applied).IsFalse();
                await Assert.That(result.ExitCode).IsEqualTo(4);
                await Assert.That(File.ReadAllText(Path.Combine(dir, "a.txt"))).IsEqualTo("one\n");
                await Assert.That(GitOut(dir, "status --porcelain").Trim().Length).IsEqualTo(0);

                Result<RunManifest> loaded = WorkspaceMaterializer.TryLoad(runId);
                await Assert.That(loaded.IsSuccess).IsTrue();
                await Assert.That(loaded.Value.State).IsEqualTo(RunState.Reported);
            }
            finally
            {
                DeleteDir(dir);
            }
        });
    }

    [Test]
    public async Task Reverify_WithoutChecksSuite_RefusedWithoutApply()
    {
        await WithIsolatedHome(async home =>
        {
            string dir = InitRepo();
            try
            {
                string runId = await MakeReportedRun(dir, home, "two\n");
                PlantPatch(home, runId, dir, "two\n");

                AcceptResult result = await ChangeApplier.AcceptAsync(runId, reverify: true);

                await Assert.That(result.Outcome).IsEqualTo(AcceptOutcome.Refused);
                await Assert.That(result.ExitCode).IsEqualTo(4);
                await Assert.That(result.Message).Contains("S4");
                await Assert.That(File.ReadAllText(Path.Combine(dir, "a.txt"))).IsEqualTo("one\n");
                await Assert.That(GitOut(dir, "status --porcelain").Trim().Length).IsEqualTo(0);
            }
            finally
            {
                DeleteDir(dir);
            }
        });
    }

    [Test]
    public async Task MissingPatch_RefusedNamingFreezeStage()
    {
        await WithIsolatedHome(async home =>
        {
            string dir = InitRepo();
            try
            {
                string runId = await AdvanceToReported(dir);

                AcceptResult result = await ChangeApplier.AcceptAsync(runId);

                await Assert.That(result.Outcome).IsEqualTo(AcceptOutcome.Refused);
                await Assert.That(result.ExitCode).IsEqualTo(4);
                await Assert.That(result.Message).Contains("change.patch");
            }
            finally
            {
                DeleteDir(dir);
            }
        });
    }

    [Test]
    public async Task AcceptBeforeReported_RefusedWithNamedReason()
    {
        await WithIsolatedHome(async home =>
        {
            string dir = InitRepo();
            try
            {
                Result<WorkspaceContract> inspected =
                    await WorkspaceInspector.InspectAsync(dir, WorkspaceIsolation.Worktree);
                await Assert.That(inspected.IsSuccess).IsTrue();
                Result<IsolatedWorkspace> created =
                    await WorkspaceMaterializer.MaterializeAsync(inspected.Value);
                await Assert.That(created.IsSuccess).IsTrue();

                AcceptResult result = await ChangeApplier.AcceptAsync(created.Value.RunId.Value);

                await Assert.That(result.Outcome).IsEqualTo(AcceptOutcome.Refused);
                await Assert.That(result.ExitCode).IsEqualTo(4);
                await Assert.That(result.Message).Contains("cannot apply a run that is not Reported");
            }
            finally
            {
                DeleteDir(dir);
            }
        });
    }

    [Test]
    public async Task UnknownRun_RefusedAsBadUsage()
    {
        await WithIsolatedHome(async _ =>
        {
            AcceptResult result = await ChangeApplier.AcceptAsync("does-not-exist-0000");

            await Assert.That(result.Outcome).IsEqualTo(AcceptOutcome.Refused);
            await Assert.That(result.ExitCode).IsEqualTo(2);
        });
    }

    [Test]
    public async Task DryRunPlusReverify_RefusedAsBadUsage()
    {
        await WithIsolatedHome(async home =>
        {
            string dir = InitRepo();
            try
            {
                string runId = await MakeReportedRun(dir, home, "two\n");

                AcceptResult result = await ChangeApplier.AcceptAsync(runId, dryRun: true, reverify: true);

                await Assert.That(result.Outcome).IsEqualTo(AcceptOutcome.Refused);
                await Assert.That(result.ExitCode).IsEqualTo(2);
            }
            finally
            {
                DeleteDir(dir);
            }
        });
    }

    [Test]
    public async Task AcceptLog_OneLinePerAttempt()
    {
        await WithIsolatedHome(async home =>
        {
            string dir = InitRepo();
            try
            {
                string runId = await MakeReportedRun(dir, home, "two\n");
                PlantPatch(home, runId, dir, "two\n");

                AcceptResult dry = await ChangeApplier.AcceptAsync(runId, dryRun: true);
                await Assert.That(dry.Outcome).IsEqualTo(AcceptOutcome.Cancelled);
                AcceptResult applied = await ChangeApplier.AcceptAsync(runId);
                await Assert.That(applied.Outcome).IsEqualTo(AcceptOutcome.Applied);

                string[] log = await File.ReadAllLinesAsync(AcceptLog(home, runId));
                await Assert.That(log.Length).IsEqualTo(2);
                await Assert.That(log[0]).Contains("outcome=Cancelled");
                await Assert.That(log[1]).Contains("outcome=Applied");
                await Assert.That(log[1]).Contains("exit=0");
            }
            finally
            {
                DeleteDir(dir);
            }
        });
    }

    /// <summary>
    ///     Materialize a run and walk it to <c>Reported</c> through the public
    ///     map (no S3–S5 to fake), then plant <c>change.patch</c> generated
    ///     from a scratch edit that is reverted afterwards — the operator tree
    ///     stays clean at the pinned base.
    /// </summary>
    private static async Task<string> MakeReportedRun(string dir, string home, string extraLine)
    {
        string runId = await AdvanceToReported(dir).ConfigureAwait(false);
        PlantPatch(home, runId, dir, extraLine);
        return runId;
    }

    private static async Task<string> AdvanceToReported(string dir)
    {
        Result<WorkspaceContract> inspected =
            await WorkspaceInspector.InspectAsync(dir, WorkspaceIsolation.Worktree).ConfigureAwait(false);
        if (inspected.IsFailure)
            throw new InvalidOperationException($"Inspect failed: {inspected.Error}");
        Result<IsolatedWorkspace> created =
            await WorkspaceMaterializer.MaterializeAsync(inspected.Value).ConfigureAwait(false);
        if (created.IsFailure)
            throw new InvalidOperationException($"Materialize failed: {created.Error}");
        string runId = created.Value.RunId.Value;
        foreach (RunState next in new[] { RunState.Changed, RunState.Checked, RunState.Reported })
        {
            Result<RunManifest> advanced = WorkspaceMaterializer.TryAdvanceState(runId, next);
            if (advanced.IsFailure)
                throw new InvalidOperationException($"Advance to {next} failed: {advanced.Error}");
        }
        return runId;
    }

    private static string PlantPatch(string home, string runId, string dir, string extraLine)
    {
        File.WriteAllText(Path.Combine(dir, "a.txt"), "one\n" + extraLine);
        string patch = GitOut(dir, "diff -- a.txt");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "one\n");
        if (GitOut(dir, "status --porcelain").Trim().Length != 0)
            throw new InvalidOperationException("Operator tree is not clean after reverting the scratch edit.");
        string patchPath = Path.Combine(home, "runs", runId, ChangeApplier.ChangePatchFileName);
        File.WriteAllText(patchPath, patch);
        return patchPath;
    }

    private static string AcceptLog(string home, string runId) =>
        Path.Combine(home, "runs", runId, ChangeApplier.AcceptLogFileName);

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
