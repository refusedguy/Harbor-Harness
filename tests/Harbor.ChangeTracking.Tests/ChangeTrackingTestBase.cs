using System.Diagnostics;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Application.Sessions;
using Harbor.ChangeTracking;

namespace Harbor.ChangeTracking.Tests;

/// <summary>
///     Shared fixture for the git-backed ChangeTracking suites (the S1–S9
///     chain: pin, isolate, freeze, check, report, accept).
///     <para>
///         The invariant every one of those suites asserts is the SAME: work
///         against a THROWAWAY git repo — one commit, a deterministic author
///         identity, no signing — and talk to git through a helper that
///         refuses to hang and reports the child's stderr on failure. What
///         varies is the repo's CONTENT (one file, four files, a branch) and
///         the author identity — so that is what this base parameterises.
///     </para>
///     <para>
///         The process-environment swap (<c>HARBOR_HOME</c>) is deliberately
///         NOT here. <c>ProcessEnvIsolationRule</c> derives its writer set and
///         its clean-writer floor from the test classes that WRITE the process
///         environment, and the eight ChangeTracking classes that isolate
///         <c>HARBOR_HOME</c> are eight of the thirteen writers that keep that
///         floor above its minimum. Hoisting the swap into a fixture would
///         empty them from the writer set and turn the guard red — a test
///         helper that costs an architecture guard its floor is not a helper.
///     </para>
/// </summary>
public abstract class ChangeTrackingTestBase
{
    /// <summary>The git identity this suite commits under.</summary>
    protected abstract (string Email, string Name) GitAuthor { get; }

    /// <summary>Fresh repo with one committed file.</summary>
    protected virtual string InitRepo(bool branch = false)
    {
        string dir = NewTempDir();
        Git(dir, "init");
        Git(dir, $"config user.email {GitAuthor.Email}");
        Git(dir, $"config user.name {GitAuthor.Name}");
        Git(dir, "config commit.gpgsign false");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "one\n");
        Git(dir, "add a.txt");
        Git(dir, "commit -m init");
        if (branch)
        {
            Git(dir, $"checkout -b {GitAuthor.Name}-test");
        }

        return dir;
    }

    /// <summary>A fresh temp working directory.</summary>
    protected static string NewTempDir(string prefix = "harbor-ws")
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Best-effort recursive delete.</summary>
    protected static void DeleteDir(string dir)
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

    /// <summary>Runs git and discards its output.</summary>
    protected static void Git(string dir, string arguments)
    {
        string output = GitOut(dir, arguments);
        _ = output;
    }

    /// <summary>Runs git and returns stdout; throws with the child's stderr on failure.</summary>
    protected static string GitOut(string dir, string arguments)
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

    /// <summary>Inspects the repo for an isolated worktree copy.</summary>
    protected static async Task<Result<WorkspaceContract>> InspectContractAsync(string dir) =>
        await WorkspaceInspector.InspectAsync(dir, WorkspaceIsolation.Worktree).ConfigureAwait(false);

    /// <summary>
    ///     Inspects the repo and asserts the inspection succeeded — the shape
    ///     the suites that consume the contract (rather than the result) need.
    /// </summary>
    protected static async Task<WorkspaceContract> InspectContractOrFailAsync(string dir)
    {
        Result<WorkspaceContract> inspected = await InspectContractAsync(dir).ConfigureAwait(false);
        await Assert.That(inspected.IsSuccess).IsTrue();
        return inspected.Value;
    }

    /// <summary>Materializes the isolated worktree copy of an inspected contract.</summary>
    protected static async Task<Result<IsolatedWorkspace>> MaterializeWorkspaceAsync(Result<WorkspaceContract> inspected)
    {
        if (inspected.IsFailure)
            return inspected.ConvertFailure<IsolatedWorkspace>();
        return await WorkspaceMaterializer.MaterializeAsync(inspected.Value).ConfigureAwait(false);
    }

    /// <summary>Inspects the repo and materializes the isolated worktree copy.</summary>
    protected static async Task<Result<IsolatedWorkspace>> InspectAndMaterialize(string dir) =>
        await MaterializeWorkspaceAsync(await InspectContractAsync(dir).ConfigureAwait(false)).ConfigureAwait(false);
}
