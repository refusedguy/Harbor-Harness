using System.Diagnostics;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Application.Sessions;
using Harbor.ChangeTracking;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.ChangeTracking.Tests;

/// <summary>
///     S2 (#376): the S1 contract becomes a real isolated working copy plus a
///     run manifest under <c>~/.harbor/runs/&lt;RunId&gt;/</c>. Worktree only
///     (<c>git worktree add --detach</c>); <c>Copy</c> fails closed; tools run
///     with the worktree as their working directory.
/// </summary>
[NotInParallel("harbor-home")]
public class WorkspaceMaterializerTests : ChangeTrackingTestBase
{
    /// <inheritdoc />
    protected override (string Email, string Name) GitAuthor => ("s2@test", "s2");

    [Test]
    public async Task Materialize_WorktreeAtBaseRev()
    {
        await WithIsolatedHome(async home =>
        {
            string dir = InitRepo(branch: true);
            try
            {
                var contract = await InspectContractOrFailAsync(dir);
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
            string dir = InitRepo(branch: true);
            try
            {
                var contract = await InspectContractOrFailAsync(dir);
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
            string dir = InitRepo(branch: true);
            try
            {
                var contract = await InspectContractOrFailAsync(dir);

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
            string dir = InitRepo(branch: true);
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
            string dir = InitRepo(branch: true);
            try
            {
                var contract = await InspectContractOrFailAsync(dir);
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
            string dir = InitRepo(branch: true);
            try
            {
                var contract = await InspectContractOrFailAsync(dir);
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
            string dir = InitRepo(branch: true);
            try
            {
                var contract = await InspectContractOrFailAsync(dir);
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
            string dir = InitRepo(branch: true);
            try
            {
                var contract = await InspectContractOrFailAsync(dir);
                var created = await WorkspaceMaterializer.MaterializeAsync(contract);
                await Assert.That(created.IsSuccess).IsTrue();
                string worktree = created.Value.Path;
                try
                {
                    var bash = new Harbor.Tools.Builtin.BashTool(
                        new Harbor.Tools.Builtin.SystemProcessProxy(), NullLogger<Harbor.Tools.Builtin.BashTool>.Instance);
                    using var pwdDoc = JsonDocument.Parse("""{"command":"pwd"}""");
                    var pwd = await bash.ExecuteAsync(pwdDoc.RootElement, TestToolContext.Create(workingDirectory: worktree));
                    await Assert.That(pwd.IsError).IsFalse();
                    // Windows CI shells (git-bash) print MSYS paths
                    // (`/c/Users/...`) for a `C:\Users\...` cwd — normalize
                    // both sides before comparing.
                    await Assert.That(NormaliseTestPath(pwd.Output)).Contains(NormaliseTestPath(worktree));

                    var write = new Harbor.Tools.Builtin.WriteTool(NullLogger<Harbor.Tools.Builtin.WriteTool>.Instance);
                    using var writeDoc = JsonDocument.Parse("""{"path":"from-tool.txt","content":"tool-wrote\n"}""");
                    var written = await write.ExecuteAsync(writeDoc.RootElement, TestToolContext.Create(workingDirectory: worktree));
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

    /// <summary>
    ///     Compare shell-printed paths across platforms: backslashes become
    ///     forward slashes and a leading MSYS drive prefix (`/c/...`) becomes
    ///     `c:/...`, so git-bash `pwd` output matches a Windows worktree path.
    /// </summary>
    private static string NormaliseTestPath(string path)
    {
        string n = path.Replace('\\', '/').Trim().TrimEnd('/');
        if (n.Length >= 3 && n[0] == '/' && char.IsLetter(n[1]) && n[2] == '/')
            n = $"{n[1]}:/{n.Substring(3)}";
        // Drive letters compare case-insensitively (`C:` vs MSYS `c:`).
        if (n.Length >= 2 && n[1] == ':')
            n = char.ToLowerInvariant(n[0]) + n.Substring(1);
        return n;
    }

    /// <summary>Fresh repo on <c>s2-test</c> with one committed file.</summary>
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
