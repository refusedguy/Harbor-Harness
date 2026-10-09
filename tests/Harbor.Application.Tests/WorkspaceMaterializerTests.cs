using System.Diagnostics;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Application.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Application.Tests;

/// <summary>
///     S2 (#376): the S1 contract becomes a real isolated working copy plus a
///     run manifest under <c>~/.harbor/runs/&lt;RunId&gt;/</c>. Worktree only
///     (<c>git worktree add --detach</c>); <c>Copy</c> fails closed; tools run
///     with the worktree as their working directory.
/// </summary>
[SkipWhenGitMissing]
[NotInParallel("harbor-home")]
public class WorkspaceMaterializerTests
{
    [Test]
    public async Task Materialize_WorktreeAtBaseRev()
    {
        await WithIsolatedHome(async home =>
        {
            string dir = InitRepo();
            try
            {
                var contract = await InspectWorktreeAsync(dir);
                string expectedSha = GitOut(dir, "rev-parse HEAD").Trim();

                var result = await WorkspaceMaterializer.MaterializeAsync(contract);

                await Assert.That(result.IsSuccess).IsTrue();
                var ws = result.Value;
                await Assert.That(ws.BaseRevision).IsEqualTo(expectedSha);
                await Assert.That(ws.Path).IsEqualTo(Path.Combine(home, "runs", ws.RunId.Value, "worktree"));
                await Assert.That(Directory.Exists(ws.Path)).IsTrue();
                await Assert.That(File.Exists(Path.Combine(home, "runs", ws.RunId.Value, "manifest.json"))).IsTrue();

                string actual = GitOut(ws.Path, "rev-parse HEAD").Trim();
                await Assert.That(actual).IsEqualTo(expectedSha);

                var release = await WorkspaceMaterializer.ReleaseAsync(ws.RunId.Value);
                await Assert.That(release.IsSuccess).IsTrue();
            }
            finally
            {
                DeleteDir(dir);
            }
        });
    }

    [Test]
    public async Task Materialize_ExistingNonEmptyPath_Refused()
    {
        await WithIsolatedHome(async home =>
        {
            string dir = InitRepo();
            try
            {
                var contract = await InspectWorktreeAsync(dir);
                var runId = RunId.New();
                string worktree = Path.Combine(home, "runs", runId.Value, "worktree");
                Directory.CreateDirectory(worktree);
                await File.WriteAllTextAsync(Path.Combine(worktree, "planted.txt"), "planted\n");

                var result = await WorkspaceMaterializer.MaterializeAsync(contract, runId);

                await Assert.That(result.IsFailure).IsTrue();
                await Assert.That(result.Error).Contains("not empty");
            }
            finally
            {
                DeleteDir(dir);
            }
        });
    }

    [Test]
    public async Task Materialize_ExistingManifest_RefusedAsAlreadyExists()
    {
        await WithIsolatedHome(async _ =>
        {
            string dir = InitRepo();
            try
            {
                var contract = await InspectWorktreeAsync(dir);

                var first = await WorkspaceMaterializer.MaterializeAsync(contract);
                await Assert.That(first.IsSuccess).IsTrue();
                try
                {
                    var second = await WorkspaceMaterializer.MaterializeAsync(contract, first.Value.RunId);

                    await Assert.That(second.IsFailure).IsTrue();
                    await Assert.That(second.Error).Contains("AlreadyExists");
                }
                finally
                {
                    var release = await WorkspaceMaterializer.ReleaseAsync(first.Value.RunId.Value);
                    await Assert.That(release.IsSuccess).IsTrue();
                }
            }
            finally
            {
                DeleteDir(dir);
            }
        });
    }

    [Test]
    public async Task Materialize_CopyIsolation_Rejected()
    {
        await WithIsolatedHome(async _ =>
        {
            string dir = InitRepo();
            try
            {
                var inspected = await WorkspaceInspector.InspectAsync(dir, WorkspaceIsolation.Copy);
                await Assert.That(inspected.IsSuccess).IsTrue();

                var result = await WorkspaceMaterializer.MaterializeAsync(inspected.Value);

                await Assert.That(result.IsFailure).IsTrue();
                await Assert.That(result.Error).Contains("Copy");
            }
            finally
            {
                DeleteDir(dir);
            }
        });
    }

    [Test]
    public async Task Materialize_Failure_LeavesNoRegisteredWorktree()
    {
        await WithIsolatedHome(async _ =>
        {
            string dir = InitRepo();
            try
            {
                var contract = await InspectWorktreeAsync(dir);
                var bogus = contract with { BaseRevision = new string('0', 40) };
                var runId = RunId.New();

                var result = await WorkspaceMaterializer.MaterializeAsync(bogus, runId);

                await Assert.That(result.IsFailure).IsTrue();
                string list = GitOut(dir, "worktree list --porcelain");
                await Assert.That(list.Contains(runId.Value)).IsFalse();
            }
            finally
            {
                DeleteDir(dir);
            }
        });
    }

    [Test]
    public async Task Manifest_RoundTrip_AndUnknownRunIdFails()
    {
        await WithIsolatedHome(async _ =>
        {
            string dir = InitRepo();
            try
            {
                var contract = await InspectWorktreeAsync(dir);
                var created = await WorkspaceMaterializer.MaterializeAsync(contract);
                await Assert.That(created.IsSuccess).IsTrue();
                try
                {
                    var loaded = WorkspaceMaterializer.TryLoad(created.Value.RunId.Value);

                    await Assert.That(loaded.IsSuccess).IsTrue();
                    await Assert.That(loaded.Value.RunId).IsEqualTo(created.Value.RunId.Value);
                    await Assert.That(loaded.Value.WorktreePath).IsEqualTo(created.Value.Path);
                    await Assert.That(loaded.Value.BaseRevision).IsEqualTo(contract.BaseRevision);
                    await Assert.That(loaded.Value.RepoRoot).IsEqualTo(contract.RepoRoot);
                }
                finally
                {
                    var release = await WorkspaceMaterializer.ReleaseAsync(created.Value.RunId.Value);
                    await Assert.That(release.IsSuccess).IsTrue();
                }

                var unknown = WorkspaceMaterializer.TryLoad("does-not-exist-0000");
                await Assert.That(unknown.IsFailure).IsTrue();
            }
            finally
            {
                DeleteDir(dir);
            }
        });
    }

    [Test]
    public async Task Release_IsIdempotent_AndPrunesRegistration()
    {
        await WithIsolatedHome(async _ =>
        {
            string dir = InitRepo();
            try
            {
                var contract = await InspectWorktreeAsync(dir);
                var created = await WorkspaceMaterializer.MaterializeAsync(contract);
                await Assert.That(created.IsSuccess).IsTrue();
                string runId = created.Value.RunId.Value;
                string worktree = created.Value.Path;

                var first = await WorkspaceMaterializer.ReleaseAsync(runId);
                await Assert.That(first.IsSuccess).IsTrue();

                var second = await WorkspaceMaterializer.ReleaseAsync(runId);
                await Assert.That(second.IsSuccess).IsTrue();

                string list = GitOut(dir, "worktree list --porcelain");
                await Assert.That(list.Contains(worktree)).IsFalse();

                var loaded = WorkspaceMaterializer.TryLoad(runId);
                await Assert.That(loaded.IsSuccess).IsTrue();
                await Assert.That(loaded.Value.State).IsEqualTo(RunState.Released);
            }
            finally
            {
                DeleteDir(dir);
            }
        });
    }

    [Test]
    public async Task Tools_RunWithWorktreeAsWorkingDirectory()
    {
        await WithIsolatedHome(async _ =>
        {
            string dir = InitRepo();
            try
            {
                var contract = await InspectWorktreeAsync(dir);
                var created = await WorkspaceMaterializer.MaterializeAsync(contract);
                await Assert.That(created.IsSuccess).IsTrue();
                string worktree = created.Value.Path;
                try
                {
                    var bash = new Harbor.Tools.Builtin.BashTool(NullLogger<Harbor.Tools.Builtin.BashTool>.Instance);
                    using var pwdDoc = JsonDocument.Parse("""{"command":"pwd"}""");
                    var pwd = await bash.ExecuteAsync(pwdDoc.RootElement, Ctx(worktree));
                    await Assert.That(pwd.IsError).IsFalse();
                    await Assert.That(pwd.Output).Contains(worktree);

                    var write = new Harbor.Tools.Builtin.WriteTool(NullLogger<Harbor.Tools.Builtin.WriteTool>.Instance);
                    using var writeDoc = JsonDocument.Parse("""{"path":"from-tool.txt","content":"tool-wrote\n"}""");
                    var written = await write.ExecuteAsync(writeDoc.RootElement, Ctx(worktree));
                    await Assert.That(written.IsError).IsFalse();
                    await Assert.That(File.Exists(Path.Combine(worktree, "from-tool.txt"))).IsTrue();
                    await Assert.That(File.Exists(Path.Combine(dir, "from-tool.txt"))).IsFalse();
                }
                finally
                {
                    var release = await WorkspaceMaterializer.ReleaseAsync(created.Value.RunId.Value);
                    await Assert.That(release.IsSuccess).IsTrue();
                }
            }
            finally
            {
                DeleteDir(dir);
            }
        });
    }

    private static ToolContext Ctx(string workingDirectory) => new(
        "test-session",
        "test-message",
        "test-call",
        "code",
        CancellationToken.None,
        Array.Empty<AgentMessage>(),
        (_, _) => Task.CompletedTask,
        (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)),
        WorkingDirectory: workingDirectory);

    /// <summary>Fresh repo on <c>s2-test</c> with one committed file.</summary>
    private static string InitRepo()
    {
        string dir = NewTempDir();
        Git(dir, "init");
        Git(dir, "config user.email s2@test");
        Git(dir, "config user.name s2");
        Git(dir, "config commit.gpgsign false");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "one\n");
        Git(dir, "add a.txt");
        Git(dir, "commit -m init");
        Git(dir, "checkout -b s2-test");
        return dir;
    }

    private static async Task<WorkspaceContract> InspectWorktreeAsync(string dir)
    {
        var inspected = await WorkspaceInspector.InspectAsync(dir, WorkspaceIsolation.Worktree);
        await Assert.That(inspected.IsSuccess).IsTrue();
        return inspected.Value;
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
        string dir = Path.Combine(Path.GetTempPath(), $"harbor-ws-{Guid.NewGuid():N}");
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

    private static void Git(string dir, string arguments)
    {
        string output = GitOut(dir, arguments);
        _ = output;
    }

    private static string GitOut(string dir, string arguments)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"git {arguments}: process failed to start.");
        if (!proc.WaitForExit(30_000))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _ = ex;
            }
            throw new TimeoutException($"git {arguments} timed out in '{dir}'.");
        }

        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {arguments} failed ({proc.ExitCode}): {stderr}");
        return stdout;
    }
}
