using System.Diagnostics;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Application.Sessions;
using Harbor.ChangeTracking;
using Harbor.TestKit;
using TUnit.Core.Enums;

namespace Harbor.ChangeTracking.Tests;

/// <summary>
///     S5 remainder (#379), driven end to end by S9 (#397): the verification
///     report is rendered from recorded artifacts only — the frozen change
///     set (S3) plus the recorded checks (S4) — persisted as
///     <c>report.json</c> + <c>report.txt</c>, and the manifest moves
///     <c>Checked -&gt; Reported</c>. Real git worktrees under an isolated
///     <c>HARBOR_HOME</c>; the family skips when git is missing (see
///     <see cref="ChangeTrackingTestBase" />), and the process-execution tests
///     are Linux-only.
/// </summary>
[NotInParallel("harbor-home")]
[RunOn(OS.Linux)]
public class RunReportTests : ChangeTrackingTestBase
{
    /// <inheritdoc />
    protected override (string Email, string Name) GitAuthor => ("s5@test", "s5");

    [Test]
    public async Task Report_PassCheck_AdvancesToReported_WritesArtifacts()
    {
        await WithIsolatedHome(async home =>
        {
            string dir = InitRepo();
            try
            {
                Result<IsolatedWorkspace> created = await InspectAndMaterialize(dir);
                await Assert.That(created.IsSuccess).IsTrue();
                string runId = created.Value.RunId.Value;

                // Scripted agent (no LLM): one file changed in the isolated copy.
                await File.WriteAllTextAsync(Path.Combine(created.Value.Path, "a.txt"), "one\ntwo\n");

                Result<FrozenChangeSet> frozen = await ChangeSetFreezer.FreezeAsync(created.Value);
                await Assert.That(frozen.IsSuccess).IsTrue();
                Result<CheckReport> checks = await CheckRunner.RunAsync(
                    created.Value, frozen.Value.BaseRevision, frozen.Value.HeadRevision,
                    [new CheckSpec("true-check", "/usr/bin/true", [])]);
                await Assert.That(checks.IsSuccess).IsTrue();
                Result<RunManifest> advanced =
                    WorkspaceMaterializer.TryAdvanceState(runId, RunState.Checked);
                await Assert.That(advanced.IsSuccess).IsTrue();

                ReportResult result = await RunReporter.ReportAsync(runId);

                await Assert.That(result.Outcome).IsEqualTo(ReportOutcome.Reported);
                await Assert.That(result.ExitCode).IsEqualTo(ChangeReport.ExitAllPassed);
                await Assert.That(result.Text).IsNotNull();
                await Assert.That(result.Message).Contains("State Reported");

                string jsonPath = Path.Combine(home, "runs", runId, RunReporter.ReportJsonFileName);
                string textPath = Path.Combine(home, "runs", runId, RunReporter.ReportTextFileName);
                await Assert.That(File.Exists(jsonPath)).IsTrue();
                await Assert.That(File.Exists(textPath)).IsTrue();
                await Assert.That(await File.ReadAllTextAsync(textPath)).IsEqualTo(result.Text);

                string text = result.Text!;
                await Assert.That(text.Contains('\x1b')).IsFalse();
                string[] lines = text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                    await Assert.That(lines[i].Length <= ChangeReport.TextWidth).IsTrue();
                await Assert.That(text.Contains("a.txt")).IsTrue();
                await Assert.That(text.Contains("true-check")).IsTrue();

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
    public async Task Report_FailedCheck_ExitOne_StillReported()
    {
        await WithIsolatedHome(async _ =>
        {
            string dir = InitRepo();
            try
            {
                Result<IsolatedWorkspace> created = await InspectAndMaterialize(dir);
                await Assert.That(created.IsSuccess).IsTrue();
                string runId = created.Value.RunId.Value;

                await File.WriteAllTextAsync(Path.Combine(created.Value.Path, "a.txt"), "one\ntwo\n");

                Result<FrozenChangeSet> frozen = await ChangeSetFreezer.FreezeAsync(created.Value);
                await Assert.That(frozen.IsSuccess).IsTrue();
                Result<CheckReport> checks = await CheckRunner.RunAsync(
                    created.Value, frozen.Value.BaseRevision, frozen.Value.HeadRevision,
                    [new CheckSpec("false-check", "/usr/bin/false", [])]);
                await Assert.That(checks.IsSuccess).IsTrue();
                await Assert.That(checks.Value.AllPassed).IsFalse();
                Result<RunManifest> advanced =
                    WorkspaceMaterializer.TryAdvanceState(runId, RunState.Checked);
                await Assert.That(advanced.IsSuccess).IsTrue();

                ReportResult result = await RunReporter.ReportAsync(runId);

                // A failed check still produces a report (never skips to
                // "success"), and the run still advances: the report informs,
                // the operator decides.
                await Assert.That(result.Outcome).IsEqualTo(ReportOutcome.Reported);
                await Assert.That(result.ExitCode).IsEqualTo(ChangeReport.ExitCheckFailed);
                await Assert.That(result.Text).IsNotNull();
                await Assert.That(result.Text!.Contains("false-check")).IsTrue();

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
    public async Task Report_ZeroChecks_ExitZero_NamesNoChecksDeclared()
    {
        await WithIsolatedHome(async home =>
        {
            string dir = InitRepo();
            try
            {
                Result<IsolatedWorkspace> created = await InspectAndMaterialize(dir);
                await Assert.That(created.IsSuccess).IsTrue();
                string runId = created.Value.RunId.Value;

                // No agent writes: the frozen set is empty (a success, not an
                // error) and zero declared checks are legal.
                Result<FrozenChangeSet> frozen = await ChangeSetFreezer.FreezeAsync(created.Value);
                await Assert.That(frozen.IsSuccess).IsTrue();
                await Assert.That(frozen.Value.IsEmpty).IsTrue();
                Result<CheckReport> checks = await CheckRunner.RunAsync(
                    created.Value, frozen.Value.BaseRevision, frozen.Value.HeadRevision, []);
                await Assert.That(checks.IsSuccess).IsTrue();
                Result<RunManifest> advanced =
                    WorkspaceMaterializer.TryAdvanceState(runId, RunState.Checked);
                await Assert.That(advanced.IsSuccess).IsTrue();

                ReportResult result = await RunReporter.ReportAsync(runId);

                await Assert.That(result.Outcome).IsEqualTo(ReportOutcome.Reported);
                await Assert.That(result.ExitCode).IsEqualTo(ChangeReport.ExitAllPassed);
                await Assert.That(result.Text!.Contains("no checks declared")).IsTrue();
                await Assert.That(File.Exists(Path.Combine(home, "runs", runId, RunReporter.ReportJsonFileName))).IsTrue();
            }
            finally
            {
                DeleteDir(dir);
            }
        });
    }

    [Test]
    public async Task Report_UnknownRun_ExitTwo()
    {
        await WithIsolatedHome(async _ =>
        {
            ReportResult result = await RunReporter.ReportAsync("does-not-exist-0000");

            await Assert.That(result.Outcome).IsEqualTo(ReportOutcome.Refused);
            await Assert.That(result.ExitCode).IsEqualTo(ChangeReport.ExitCannotProduce);
            await Assert.That(result.Text).IsNull();
        });
    }

    [Test]
    public async Task Report_MissingArtifacts_ExitTwo_NamingFreezeStage()
    {
        await WithIsolatedHome(async _ =>
        {
            string dir = InitRepo();
            try
            {
                // Walked to Checked by hand with no freeze and no checks: the
                // report refuses naming the missing producer, it never invents
                // a change set.
                Result<IsolatedWorkspace> created = await InspectAndMaterialize(dir);
                await Assert.That(created.IsSuccess).IsTrue();
                string runId = created.Value.RunId.Value;
                await Assert.That(WorkspaceMaterializer.TryAdvanceState(runId, RunState.Changed).IsSuccess).IsTrue();
                await Assert.That(WorkspaceMaterializer.TryAdvanceState(runId, RunState.Checked).IsSuccess).IsTrue();

                ReportResult result = await RunReporter.ReportAsync(runId);

                await Assert.That(result.Outcome).IsEqualTo(ReportOutcome.Refused);
                await Assert.That(result.ExitCode).IsEqualTo(ChangeReport.ExitCannotProduce);
                await Assert.That(result.Message).Contains("freeze");

                Result<RunManifest> loaded = WorkspaceMaterializer.TryLoad(runId);
                await Assert.That(loaded.IsSuccess).IsTrue();
                await Assert.That(loaded.Value.State).IsEqualTo(RunState.Checked);
            }
            finally
            {
                DeleteDir(dir);
            }
        });
    }

    [Test]
    public async Task Report_BeforeChecked_ExitTwo_NamingIllegalTransition()
    {
        await WithIsolatedHome(async _ =>
        {
            string dir = InitRepo();
            try
            {
                Result<IsolatedWorkspace> created = await InspectAndMaterialize(dir);
                await Assert.That(created.IsSuccess).IsTrue();
                string runId = created.Value.RunId.Value;

                ReportResult result = await RunReporter.ReportAsync(runId);

                await Assert.That(result.Outcome).IsEqualTo(ReportOutcome.Refused);
                await Assert.That(result.ExitCode).IsEqualTo(ChangeReport.ExitCannotProduce);
                await Assert.That(result.Message).Contains("Isolated -> Reported");

                Result<RunManifest> loaded = WorkspaceMaterializer.TryLoad(runId);
                await Assert.That(loaded.IsSuccess).IsTrue();
                await Assert.That(loaded.Value.State).IsEqualTo(RunState.Isolated);
            }
            finally
            {
                DeleteDir(dir);
            }
        });
    }

    [Test]
    public async Task Report_Rereport_ByteIdentical()
    {
        await WithIsolatedHome(async home =>
        {
            string dir = InitRepo();
            try
            {
                Result<IsolatedWorkspace> created = await InspectAndMaterialize(dir);
                await Assert.That(created.IsSuccess).IsTrue();
                string runId = created.Value.RunId.Value;

                await File.WriteAllTextAsync(Path.Combine(created.Value.Path, "a.txt"), "one\ntwo\n");
                Result<FrozenChangeSet> frozen = await ChangeSetFreezer.FreezeAsync(created.Value);
                await Assert.That(frozen.IsSuccess).IsTrue();
                Result<CheckReport> checks = await CheckRunner.RunAsync(
                    created.Value, frozen.Value.BaseRevision, frozen.Value.HeadRevision,
                    [new CheckSpec("true-check", "/usr/bin/true", [])]);
                await Assert.That(checks.IsSuccess).IsTrue();
                await Assert.That(WorkspaceMaterializer.TryAdvanceState(runId, RunState.Checked).IsSuccess).IsTrue();

                ReportResult first = await RunReporter.ReportAsync(runId);
                await Assert.That(first.Outcome).IsEqualTo(ReportOutcome.Reported);
                byte[] firstJson = await File.ReadAllBytesAsync(
                    Path.Combine(home, "runs", runId, RunReporter.ReportJsonFileName));

                ReportResult second = await RunReporter.ReportAsync(runId);
                await Assert.That(second.Outcome).IsEqualTo(ReportOutcome.Reported);
                await Assert.That(second.ExitCode).IsEqualTo(first.ExitCode);
                byte[] secondJson = await File.ReadAllBytesAsync(
                    Path.Combine(home, "runs", runId, RunReporter.ReportJsonFileName));

                await Assert.That(secondJson.Length).IsEqualTo(firstJson.Length);
                await Assert.That(secondJson.SequenceEqual(firstJson)).IsTrue();
                await Assert.That(second.Text).IsEqualTo(first.Text);

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
    public async Task CheckRunner_TryLoad_Missing_FailsNamingChecksStage()
    {
        await WithIsolatedHome(async _ =>
        {
            Result<CheckReport> loaded = CheckRunner.TryLoad("does-not-exist-0000");

            await Assert.That(loaded.IsFailure).IsTrue();
            await Assert.That(loaded.Error).Contains("checks");
        });
    }

    [Test]
    public async Task ChangeSetFreezer_TryLoad_Missing_FailsNamingFreezeStage()
    {
        await WithIsolatedHome(async _ =>
        {
            Result<FrozenChangeSet> loaded = ChangeSetFreezer.TryLoad("does-not-exist-0000");

            await Assert.That(loaded.IsFailure).IsTrue();
            await Assert.That(loaded.Error).Contains("freeze");
        });
    }


    private static async Task WithIsolatedHome(Func<string, Task> body)
    {
        using var home = new TempDirScope("harbor-home");
        await EnvScope.WithAsync("HARBOR_HOME", home.Path, () => body(home.Path));
    }
}
