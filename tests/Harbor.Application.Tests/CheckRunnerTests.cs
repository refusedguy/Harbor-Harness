using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Application.Sessions;

namespace Harbor.Application.Tests;

/// <summary>
///     S4 (#378): declared checks run against the isolated copy and record
///     exactly what was proven. Process-execution tests need POSIX tools
///     (<c>/bin/sh</c>, coreutils) and return early off Linux — the same
///     honest-skip shape as the <c>HARBOR_E2E</c>-guarded tests. Validation,
///     revision-mismatch and zero-checks tests are portable and run on every OS.
/// </summary>
[NotInParallel("harbor-home")]
public class CheckRunnerTests
{
    private const string BaseRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HeadRevision = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string CanaryKey = "HARBOR_ENV_CANARY";
    private const string CanaryValue = "canary-9f3c1e-value";

    [Test]
    public async Task Pass_RecordsEvidenceAndArtifact()
    {
        if (!OperatingSystem.IsLinux())
            return;
        await WithIsolatedHome(async home =>
        {
            string worktree = NewTempDir();
            try
            {
                var ws = new IsolatedWorkspace(RunId.New(), worktree, BaseRevision);
                var spec = new CheckSpec("echo-hi", "/usr/bin/echo", ["hello-from-check"]);

                Result<CheckReport> result = await CheckRunner.RunAsync(ws, BaseRevision, HeadRevision, [spec]);

                await Assert.That(result.IsSuccess).IsTrue();
                CheckReport report = result.Value;
                await Assert.That(report.HasChecks).IsTrue();
                await Assert.That(report.AllPassed).IsTrue();
                await Assert.That(report.Checks.Count).IsEqualTo(1);
                CheckResult check = report.Checks[0];
                await Assert.That(check.Outcome).IsEqualTo(CheckOutcome.Passed);
                await Assert.That(check.ExitCode is 0).IsTrue();
                await Assert.That(check.WorkingDirectory).IsEqualTo(worktree);
                await Assert.That(check.BaseRevision).IsEqualTo(BaseRevision);
                await Assert.That(check.HeadRevision).IsEqualTo(HeadRevision);
                await Assert.That(check.Output.Contains("hello-from-check")).IsTrue();
                await Assert.That(check.Truncated).IsFalse();
                await Assert.That(check.ViaShell).IsFalse();
                await Assert.That(HasKey(check.EnvKeys, "PATH")).IsTrue();

                string artifact = Path.Combine(home, "runs", ws.RunId.Value, "checks.json");
                await Assert.That(File.Exists(artifact)).IsTrue();
                using JsonDocument doc = JsonDocument.Parse(await File.ReadAllTextAsync(artifact));
                JsonElement root = doc.RootElement;
                await Assert.That(root.GetProperty("baseRevision").GetString()).IsEqualTo(BaseRevision);
                await Assert.That(root.GetProperty("headRevision").GetString()).IsEqualTo(HeadRevision);
                JsonElement checks = root.GetProperty("checks");
                await Assert.That(checks.ValueKind).IsEqualTo(JsonValueKind.Array);
                await Assert.That(checks.GetArrayLength()).IsEqualTo(1);
                JsonElement first = checks[0];
                await Assert.That(first.GetProperty("outcome").GetString()).IsEqualTo("Passed");
                await Assert.That(first.GetProperty("workingDirectory").GetString()).IsEqualTo(worktree);
                await Assert.That(first.GetProperty("viaShell").GetBoolean()).IsFalse();
            }
            finally
            {
                DeleteDir(worktree);
            }
        });
    }

    [Test]
    public async Task RelativeFileWrite_LandsInWorktree()
    {
        if (!OperatingSystem.IsLinux())
            return;
        await WithIsolatedHome(async _ =>
        {
            string worktree = NewTempDir();
            try
            {
                var ws = new IsolatedWorkspace(RunId.New(), worktree, BaseRevision);
                var spec = new CheckSpec("touch-relative", "/usr/bin/touch", ["rel-probe.txt"]);

                Result<CheckReport> result = await CheckRunner.RunAsync(ws, BaseRevision, HeadRevision, [spec]);

                await Assert.That(result.IsSuccess).IsTrue();
                await Assert.That(result.Value.AllPassed).IsTrue();
                await Assert.That(File.Exists(Path.Combine(worktree, "rel-probe.txt"))).IsTrue();
            }
            finally
            {
                DeleteDir(worktree);
            }
        });
    }

    [Test]
    public async Task NonZeroExit_RecordsFailedWithTail()
    {
        if (!OperatingSystem.IsLinux())
            return;
        await WithIsolatedHome(async _ =>
        {
            string worktree = NewTempDir();
            try
            {
                var ws = new IsolatedWorkspace(RunId.New(), worktree, BaseRevision);
                var spec = new CheckSpec("ls-missing", "/usr/bin/ls", ["/nonexistent-dir-xyz-378"]);

                Result<CheckReport> result = await CheckRunner.RunAsync(ws, BaseRevision, HeadRevision, [spec]);

                await Assert.That(result.IsSuccess).IsTrue();
                CheckReport report = result.Value;
                await Assert.That(report.AllPassed).IsFalse();
                CheckResult check = report.Checks[0];
                await Assert.That(check.Outcome).IsEqualTo(CheckOutcome.Failed);
                await Assert.That(check.ExitCode is not null && check.ExitCode.Value != 0).IsTrue();
                await Assert.That(check.ErrorOutput.Length > 0).IsTrue();
            }
            finally
            {
                DeleteDir(worktree);
            }
        });
    }

    [Test]
    public async Task MissingBinary_RecordsSpawnFailedNeverPassed()
    {
        if (!OperatingSystem.IsLinux())
            return;
        await WithIsolatedHome(async _ =>
        {
            string worktree = NewTempDir();
            try
            {
                var ws = new IsolatedWorkspace(RunId.New(), worktree, BaseRevision);
                var spec = new CheckSpec("no-such-binary", "harbor-definitely-missing-binary-xyz", []);

                Result<CheckReport> result = await CheckRunner.RunAsync(ws, BaseRevision, HeadRevision, [spec]);

                await Assert.That(result.IsSuccess).IsTrue();
                CheckResult check = result.Value.Checks[0];
                await Assert.That(check.Outcome).IsEqualTo(CheckOutcome.SpawnFailed);
                await Assert.That(check.ExitCode is null).IsTrue();
                await Assert.That(result.Value.AllPassed).IsFalse();
            }
            finally
            {
                DeleteDir(worktree);
            }
        });
    }

    [Test]
    public async Task Timeout_KillsTreeAndSkipsRest()
    {
        if (!OperatingSystem.IsLinux())
            return;
        await WithIsolatedHome(async _ =>
        {
            string worktree = NewTempDir();
            try
            {
                var ws = new IsolatedWorkspace(RunId.New(), worktree, BaseRevision);
                var slow = new CheckSpec("sleep-long", "/bin/sleep", ["30"], TimeoutSeconds: 1);
                var next = new CheckSpec("touch-after", "/usr/bin/touch", ["after-timeout.txt"]);

                Result<CheckReport> result = await CheckRunner.RunAsync(ws, BaseRevision, HeadRevision, [slow, next]);

                await Assert.That(result.IsSuccess).IsTrue();
                await Assert.That(result.Value.Checks[0].Outcome).IsEqualTo(CheckOutcome.TimedOut);
                await Assert.That(result.Value.Checks[1].Outcome).IsEqualTo(CheckOutcome.Skipped);
                await Assert.That(result.Value.AllPassed).IsFalse();
                await Assert.That(File.Exists(Path.Combine(worktree, "after-timeout.txt"))).IsFalse();
            }
            finally
            {
                DeleteDir(worktree);
            }
        });
    }

    [Test]
    public async Task ContinueOnFailure_RunsRestAfterTimeout()
    {
        if (!OperatingSystem.IsLinux())
            return;
        await WithIsolatedHome(async _ =>
        {
            string worktree = NewTempDir();
            try
            {
                var ws = new IsolatedWorkspace(RunId.New(), worktree, BaseRevision);
                var slow = new CheckSpec("sleep-long", "/bin/sleep", ["30"], TimeoutSeconds: 1);
                var next = new CheckSpec("touch-after", "/usr/bin/touch", ["after-continue.txt"]);

                Result<CheckReport> result = await CheckRunner.RunAsync(
                    ws, BaseRevision, HeadRevision, [slow, next], continueOnFailure: true);

                await Assert.That(result.IsSuccess).IsTrue();
                await Assert.That(result.Value.Checks[0].Outcome).IsEqualTo(CheckOutcome.TimedOut);
                await Assert.That(result.Value.Checks[1].Outcome).IsEqualTo(CheckOutcome.Passed);
                await Assert.That(File.Exists(Path.Combine(worktree, "after-continue.txt"))).IsTrue();
            }
            finally
            {
                DeleteDir(worktree);
            }
        });
    }

    [Test]
    public async Task OutputCap_TruncatesWithMarkerButStaysPassed()
    {
        if (!OperatingSystem.IsLinux())
            return;
        await WithIsolatedHome(async _ =>
        {
            string worktree = NewTempDir();
            try
            {
                var ws = new IsolatedWorkspace(RunId.New(), worktree, BaseRevision);
                var spec = new CheckSpec("big-output", "/usr/bin/seq", ["1", "20000"], OutputCapBytes: 1024);

                Result<CheckReport> result = await CheckRunner.RunAsync(ws, BaseRevision, HeadRevision, [spec]);

                await Assert.That(result.IsSuccess).IsTrue();
                CheckResult check = result.Value.Checks[0];
                await Assert.That(check.Outcome).IsEqualTo(CheckOutcome.Passed);
                await Assert.That(check.ExitCode is 0).IsTrue();
                await Assert.That(check.Truncated).IsTrue();
                await Assert.That(check.Output.Length <= 1024).IsTrue();
            }
            finally
            {
                DeleteDir(worktree);
            }
        });
    }

    [Test]
    public async Task EnvAllowlist_PlantedSecretAbsentByDefault()
    {
        if (!OperatingSystem.IsLinux())
            return;
        string? saved = Environment.GetEnvironmentVariable(CanaryKey);
        Environment.SetEnvironmentVariable(CanaryKey, CanaryValue);
        try
        {
            await WithIsolatedHome(async _ =>
            {
                string worktree = NewTempDir();
                try
                {
                    var ws = new IsolatedWorkspace(RunId.New(), worktree, BaseRevision);
                    var spec = new CheckSpec("dump-env", "/usr/bin/env", []);

                    Result<CheckReport> result = await CheckRunner.RunAsync(ws, BaseRevision, HeadRevision, [spec]);

                    await Assert.That(result.IsSuccess).IsTrue();
                    CheckResult check = result.Value.Checks[0];
                    await Assert.That(check.Outcome).IsEqualTo(CheckOutcome.Passed);
                    await Assert.That(check.Output.Contains(CanaryValue)).IsFalse();
                    await Assert.That(HasKey(check.EnvKeys, CanaryKey)).IsFalse();
                    await Assert.That(HasKey(check.EnvKeys, "PATH")).IsTrue();
                }
                finally
                {
                    DeleteDir(worktree);
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(CanaryKey, saved);
        }
    }

    [Test]
    public async Task EnvPassthrough_DeclaredKeyReachesChild()
    {
        if (!OperatingSystem.IsLinux())
            return;
        string? saved = Environment.GetEnvironmentVariable(CanaryKey);
        Environment.SetEnvironmentVariable(CanaryKey, CanaryValue);
        try
        {
            await WithIsolatedHome(async _ =>
            {
                string worktree = NewTempDir();
                try
                {
                    var ws = new IsolatedWorkspace(RunId.New(), worktree, BaseRevision);
                    var spec = new CheckSpec("dump-env", "/usr/bin/env", []);

                    Result<CheckReport> result = await CheckRunner.RunAsync(
                        ws, BaseRevision, HeadRevision, [spec], passthroughEnvKeys: [CanaryKey]);

                    await Assert.That(result.IsSuccess).IsTrue();
                    CheckResult check = result.Value.Checks[0];
                    await Assert.That(check.Output.Contains(CanaryKey + "=" + CanaryValue)).IsTrue();
                    await Assert.That(HasKey(check.EnvKeys, CanaryKey)).IsTrue();
                }
                finally
                {
                    DeleteDir(worktree);
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(CanaryKey, saved);
        }
    }

    [Test]
    public async Task ShellOptIn_RunsViaShellAndFlagsIt()
    {
        if (!OperatingSystem.IsLinux())
            return;
        await WithIsolatedHome(async home =>
        {
            string worktree = NewTempDir();
            try
            {
                var ws = new IsolatedWorkspace(RunId.New(), worktree, BaseRevision);
                var spec = new CheckSpec("shell-echo", "echo", ["shell-probe-ok"], Shell: true);

                Result<CheckReport> result = await CheckRunner.RunAsync(ws, BaseRevision, HeadRevision, [spec]);

                await Assert.That(result.IsSuccess).IsTrue();
                CheckResult check = result.Value.Checks[0];
                await Assert.That(check.Outcome).IsEqualTo(CheckOutcome.Passed);
                await Assert.That(check.ViaShell).IsTrue();
                await Assert.That(check.Output.Contains("shell-probe-ok")).IsTrue();

                string artifact = Path.Combine(home, "runs", ws.RunId.Value, "checks.json");
                using JsonDocument doc = JsonDocument.Parse(await File.ReadAllTextAsync(artifact));
                JsonElement first = doc.RootElement.GetProperty("checks")[0];
                await Assert.That(first.GetProperty("viaShell").GetBoolean()).IsTrue();
            }
            finally
            {
                DeleteDir(worktree);
            }
        });
    }

    [Test]
    public async Task Cancellation_KillsTreeAndRecordsCancelled()
    {
        if (!OperatingSystem.IsLinux())
            return;
        await WithIsolatedHome(async _ =>
        {
            string worktree = NewTempDir();
            try
            {
                var ws = new IsolatedWorkspace(RunId.New(), worktree, BaseRevision);
                var slow = new CheckSpec("sleep-long", "/bin/sleep", ["30"], TimeoutSeconds: 600);
                var next = new CheckSpec("touch-after", "/usr/bin/touch", ["after-cancel.txt"]);
                using var cts = new CancellationTokenSource();
                cts.CancelAfter(300);

                Result<CheckReport> result = await CheckRunner.RunAsync(
                    ws, BaseRevision, HeadRevision, [slow, next], ct: cts.Token);

                await Assert.That(result.IsSuccess).IsTrue();
                await Assert.That(result.Value.Checks[0].Outcome).IsEqualTo(CheckOutcome.Cancelled);
                await Assert.That(result.Value.Checks[1].Outcome).IsEqualTo(CheckOutcome.Skipped);
                await Assert.That(result.Value.AllPassed).IsFalse();
                await Assert.That(File.Exists(Path.Combine(worktree, "after-cancel.txt"))).IsFalse();
            }
            finally
            {
                DeleteDir(worktree);
            }
        });
    }

    [Test]
    public async Task ZeroChecks_WritesChecksNone()
    {
        await WithIsolatedHome(async home =>
        {
            string worktree = NewTempDir();
            try
            {
                var ws = new IsolatedWorkspace(RunId.New(), worktree, BaseRevision);

                Result<CheckReport> result = await CheckRunner.RunAsync(
                    ws, BaseRevision, HeadRevision, Array.Empty<CheckSpec>());

                await Assert.That(result.IsSuccess).IsTrue();
                await Assert.That(result.Value.HasChecks).IsFalse();
                await Assert.That(result.Value.AllPassed).IsFalse();

                string artifact = Path.Combine(home, "runs", ws.RunId.Value, "checks.json");
                await Assert.That(File.Exists(artifact)).IsTrue();
                using JsonDocument doc = JsonDocument.Parse(await File.ReadAllTextAsync(artifact));
                JsonElement checks = doc.RootElement.GetProperty("checks");
                await Assert.That(checks.ValueKind).IsEqualTo(JsonValueKind.String);
                await Assert.That(checks.GetString()).IsEqualTo("none");
            }
            finally
            {
                DeleteDir(worktree);
            }
        });
    }

    [Test]
    public async Task RevisionMismatch_IsFailure()
    {
        await WithIsolatedHome(async _ =>
        {
            string worktree = NewTempDir();
            try
            {
                var ws = new IsolatedWorkspace(RunId.New(), worktree, BaseRevision);

                Result<CheckReport> result = await CheckRunner.RunAsync(
                    ws, "cccccccccccccccccccccccccccccccccccccccc", HeadRevision,
                    [new CheckSpec("echo-hi", "/usr/bin/echo", ["hi"])]);

                await Assert.That(result.IsFailure).IsTrue();
                await Assert.That(result.Error.Contains("does not match")).IsTrue();
            }
            finally
            {
                DeleteDir(worktree);
            }
        });
    }

    [Test]
    public async Task InvalidSpec_IsFailure()
    {
        await WithIsolatedHome(async _ =>
        {
            string worktree = NewTempDir();
            try
            {
                var ws = new IsolatedWorkspace(RunId.New(), worktree, BaseRevision);
                var bad = new CheckSpec("bad-timeout", "/usr/bin/true", [], TimeoutSeconds: 0);

                Result<CheckReport> result = await CheckRunner.RunAsync(ws, BaseRevision, HeadRevision, [bad]);

                await Assert.That(result.IsFailure).IsTrue();
                await Assert.That(result.Error.Contains("TimeoutSeconds")).IsTrue();
            }
            finally
            {
                DeleteDir(worktree);
            }
        });
    }

    private static bool HasKey(IReadOnlyList<string> keys, string name)
    {
        for (int i = 0; i < keys.Count; i++)
        {
            if (keys[i] == name)
                return true;
        }
        return false;
    }

    private static async Task WithIsolatedHome(Func<string, Task> body)
    {
        string? savedHarborHome = Environment.GetEnvironmentVariable("HARBOR_HOME");
        string home = Path.Combine(Path.GetTempPath(), $"harbor-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("HARBOR_HOME", home);
        try
        {
            await body(home).ConfigureAwait(false);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HARBOR_HOME", savedHarborHome);
            DeleteDir(home);
        }
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"harbor-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteDir(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DirectoryNotFoundException)
        {
            // Test cleanup is best-effort.
            _ = ex;
        }
    }
}
