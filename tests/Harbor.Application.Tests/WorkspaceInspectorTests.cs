using System.Diagnostics;
using Harbor.Abstractions.Models;
using Harbor.Application.Sessions;
using Harbor.TestKit;

namespace Harbor.Application.Tests;

/// <summary>
///     WorkspaceInspector pins the S1 workspace contract via git probes: a clean
///     HEAD succeeds with the pinned revision, tracked dirt or any git failure
///     rejects the run, and untracked-only state succeeds with the presence flag.
/// </summary>
public class WorkspaceInspectorTests
{
    /// <summary>
    ///     The suite drives a real git repository, so it needs git on PATH. The
    ///     decision is TUnit's: <c>Skip.Unless</c> marks every test in the
    ///     class skipped, with the reason, before the class is instantiated.
    /// </summary>
    [Before(HookType.Class)]
    public static void SkipWithoutGit() => Skip.Unless(GitProbe.Available, GitProbe.SkipReason);

    [Test]
    public async Task Inspect_CleanHead_SuccessWithPinnedRevision()
    {
        string dir = InitRepo();
        try
        {
            string expectedSha = GitOut(dir, "rev-parse HEAD").Trim();

            var result = await WorkspaceInspector.InspectAsync(
                dir, WorkspaceIsolation.Copy, new WorkspaceLimits(TimeoutSeconds: 600, MaxSteps: 50));

            await Assert.That(result).IsSuccessful();
            var contract = result.Value;
            await Assert.That(contract.RepoRoot).IsEqualTo(dir);
            await Assert.That(contract.BaseRevision).IsEqualTo(expectedSha);
            await Assert.That(contract.BaseBranch).IsEqualTo("s1-test");
            await Assert.That(contract.TrackedDirt.Count).IsEqualTo(0);
            await Assert.That(contract.UntrackedPresent).IsFalse();
            await Assert.That(contract.Isolation).IsEqualTo(WorkspaceIsolation.Copy);
            await Assert.That(contract.Limits.TimeoutSeconds).IsEqualTo(600);
            await Assert.That(contract.Limits.MaxSteps).IsEqualTo(50);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Test]
    public async Task Inspect_TrackedModification_FailsWithReason()
    {
        string dir = InitRepo();
        try
        {
            await File.AppendAllTextAsync(Path.Combine(dir, "a.txt"), "two\n");

            var result = await WorkspaceInspector.InspectAsync(dir);

            await Assert.That(result).IsFailed();
            await Assert.That(result.Error).Contains("Dirty");
            await Assert.That(result.Error).Contains("a.txt");
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Test]
    public async Task Inspect_NotARepository_FailsWithReason()
    {
        string dir = NewTempDir();
        try
        {
            var result = await WorkspaceInspector.InspectAsync(dir);

            await Assert.That(result).IsFailed();
            await Assert.That(result.Error).Contains("Not a git repository");
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Test]
    public async Task Inspect_UntrackedOnly_SuccessWithFlag()
    {
        string dir = InitRepo();
        try
        {
            string expectedSha = GitOut(dir, "rev-parse HEAD").Trim();
            await File.WriteAllTextAsync(Path.Combine(dir, "new.txt"), "untracked\n");

            var result = await WorkspaceInspector.InspectAsync(dir);

            await Assert.That(result).IsSuccessful();
            await Assert.That(result.Value.BaseRevision).IsEqualTo(expectedSha);
            await Assert.That(result.Value.TrackedDirt.Count).IsEqualTo(0);
            await Assert.That(result.Value.UntrackedPresent).IsTrue();
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Test]
    public async Task Inspect_ManyTrackedModifications_TruncatesDirtListWithMoreSuffix()
    {
        // #1097: pins the JoinDirt truncation branch (over MaxDirtPathsInError
        // paths) that the pooled rewrite must preserve byte-for-byte.
        string dir = InitRepo();
        try
        {
            for (int i = 0; i < 25; i++)
                await File.WriteAllTextAsync(Path.Combine(dir, $"f{i:00}.txt"), "v1\n");
            Git(dir, "add .");
            Git(dir, "commit -m bulk");
            for (int i = 0; i < 25; i++)
                await File.AppendAllTextAsync(Path.Combine(dir, $"f{i:00}.txt"), "v2\n");

            var result = await WorkspaceInspector.InspectAsync(dir);

            await Assert.That(result).IsFailed();
            await Assert.That(result.Error).Contains("Dirty");
            await Assert.That(result.Error).Contains("more)");
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Test]
    public async Task WorkspaceInspector_UsesPooledBuilder_RelativeGate()
    {
        // #1097 (relative gate, one site): WorkspaceInspector.cs must build its
        // dirt list from StringBuilderPool — zero `new StringBuilder` in this
        // file. Scoped to this file on purpose: the tree still has cold-path
        // builders (TableBlock, OnboardingFlow, ...), each pooled on its own
        // issue slice rather than under one absolute ban.
        string path = FindRepoFile(Path.Combine("src", "Harbor.SessionContext", "Sessions", "WorkspaceInspector.cs"));
        string source = await File.ReadAllTextAsync(path);
        await Assert.That(source.Contains("StringBuilderPool.Rent")).IsTrue();
        await Assert.That(source.Contains("new StringBuilder")).IsFalse();
    }

    /// <summary>Fresh repo on <c>s1-test</c> with one committed file.</summary>
    private static string InitRepo()
    {
        string dir = NewTempDir();
        Git(dir, "init");
        Git(dir, "config user.email s1@test");
        Git(dir, "config user.name s1");
        Git(dir, "config commit.gpgsign false");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "one\n");
        Git(dir, "add a.txt");
        Git(dir, "commit -m init");
        Git(dir, "checkout -b s1-test");
        return dir;
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

    /// <summary>Locate a repo-relative source file by walking up from the test assembly directory.</summary>
    private static string FindRepoFile(string relativePath)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, relativePath);
            if (File.Exists(candidate))
                return candidate;
            string? parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            if (parent is null || parent == dir)
                break;
            dir = parent;
        }
        throw new InvalidOperationException(
            $"Cannot locate '{relativePath}' above '{AppContext.BaseDirectory}'.");
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
