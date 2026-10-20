using System.Diagnostics;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Application.Sessions;
using Harbor.ChangeTracking;
using Harbor.TestKit;
using TUnit.Core.Enums;

namespace Harbor.ChangeTracking.Tests;

/// <summary>
///     S3 (#377): the agent's work in the isolated copy freezes into an
///     immutable artifact — <c>change.patch</c> plus <c>changeset.json</c> —
///     and the manifest moves <c>Isolated -&gt; Changed</c>. Real git
///     worktrees under an isolated <c>HARBOR_HOME</c>; the class skips when
///     git is missing.
/// </summary>
[NotInParallel("harbor-home")]
public class ChangeSetFreezerTests : ChangeTrackingTestBase
{
    /// <inheritdoc />
    protected override (string Email, string Name) GitAuthor => ("s3@test", "s3");
    [Test]
    public async Task Freeze_ModifyAddDelete_ProduceRightStatusesWithCounts()
    {
        await WithIsolatedHome(async home =>
        {
            string dir = InitRepo();
            try
            {
                var created = await MaterializeAsync(dir);
                await Assert.That(created.IsSuccess).IsTrue();
                string worktree = created.Value.Path;
                try
                {
                    await File.AppendAllTextAsync(Path.Combine(worktree, "a.txt"), "two\n");
                    File.Delete(Path.Combine(worktree, "del.txt"));
                    await File.WriteAllTextAsync(Path.Combine(worktree, "new.txt"), "new\n");

                    Result<FrozenChangeSet> result = await ChangeSetFreezer.FreezeAsync(created.Value);

                    await Assert.That(result.IsSuccess).IsTrue();
                    FrozenChangeSet set = result.Value;
                    await Assert.That(set.IsEmpty).IsFalse();
                    await Assert.That(set.Entries.Count).IsEqualTo(3);
                    FrozenChangeEntry modified = Find(set, "a.txt");
                    await Assert.That(modified.Status).IsEqualTo("M");
                    await Assert.That(modified.Insertions).IsEqualTo(1);
                    await Assert.That(modified.Deletions).IsEqualTo(0);
                    await Assert.That(modified.Binary).IsFalse();
                    FrozenChangeEntry added = Find(set, "new.txt");
                    await Assert.That(added.Status).IsEqualTo("A");
                    FrozenChangeEntry deleted = Find(set, "del.txt");
                    await Assert.That(deleted.Status).IsEqualTo("D");

                    string patch = await File.ReadAllTextAsync(set.PatchPath);
                    await Assert.That(patch.Contains("a.txt")).IsTrue();
                    await Assert.That(patch.Contains("new.txt")).IsTrue();
                    await Assert.That(patch.Contains("del.txt")).IsTrue();

                    var loaded = WorkspaceMaterializer.TryLoad(created.Value.RunId.Value);
                    await Assert.That(loaded.IsSuccess).IsTrue();
                    await Assert.That(loaded.Value.State).IsEqualTo(RunState.Changed);

                    // The operator tree is untouched: the freeze commits only
                    // inside the isolated copy, and the run directory holds
                    // only the frozen artifacts plus the manifest.
                    await Assert.That(File.Exists(Path.Combine(dir, "new.txt"))).IsFalse();
                    await Assert.That(File.Exists(
                        Path.Combine(home, "runs", created.Value.RunId.Value, "new.txt"))).IsFalse();
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

    [Test]
    public async Task Freeze_Rename_KeepsPorcelainR()
    {
        await WithIsolatedHome(async _ =>
        {
            string dir = InitRepo();
            try
            {
                var created = await MaterializeAsync(dir);
                await Assert.That(created.IsSuccess).IsTrue();
                try
                {
                    File.Move(
                        Path.Combine(created.Value.Path, "mov.txt"),
                        Path.Combine(created.Value.Path, "mov2.txt"));

                    Result<FrozenChangeSet> result = await ChangeSetFreezer.FreezeAsync(created.Value);

                    await Assert.That(result.IsSuccess).IsTrue();
                    FrozenChangeEntry renamed = Find(result.Value, "mov2.txt");
                    await Assert.That(renamed.Status).IsEqualTo("R");
                    await Assert.That(renamed.OldPath).IsEqualTo("mov.txt");
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

    [Test]
    public async Task Freeze_BinaryChange_ReportedAsBinary()
    {
        await WithIsolatedHome(async _ =>
        {
            string dir = InitRepo();
            try
            {
                var created = await MaterializeAsync(dir);
                await Assert.That(created.IsSuccess).IsTrue();
                try
                {
                    string bin = Path.Combine(created.Value.Path, "bin.dat");
                    byte[] before = await File.ReadAllBytesAsync(bin);
                    var after = new byte[before.Length + 4];
                    Array.Copy(before, after, before.Length);
                    after[before.Length] = 0xDE;
                    after[before.Length + 1] = 0xAD;
                    after[before.Length + 2] = 0xBE;
                    after[before.Length + 3] = 0xEF;
                    await File.WriteAllBytesAsync(bin, after);

                    Result<FrozenChangeSet> result = await ChangeSetFreezer.FreezeAsync(created.Value);

                    await Assert.That(result.IsSuccess).IsTrue();
                    FrozenChangeEntry binary = Find(result.Value, "bin.dat");
                    await Assert.That(binary.Status).IsEqualTo("M");
                    await Assert.That(binary.Binary).IsTrue();
                    await Assert.That(binary.Insertions is null).IsTrue();
                    await Assert.That(binary.Deletions is null).IsTrue();

                    string patch = await File.ReadAllTextAsync(result.Value.PatchPath);
                    await Assert.That(patch.Contains("GIT binary patch")).IsTrue();
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

    [Test]
    public async Task Freeze_IgnoredPaths_ExcludedAndCounted()
    {
        await WithIsolatedHome(async home =>
        {
            string dir = InitRepo();
            try
            {
                var created = await MaterializeAsync(dir);
                await Assert.That(created.IsSuccess).IsTrue();
                try
                {
                    await File.WriteAllTextAsync(Path.Combine(created.Value.Path, ".gitignore"), "*.log\n");
                    await File.WriteAllTextAsync(Path.Combine(created.Value.Path, "ignored.log"), "dropped\n");
                    await File.WriteAllTextAsync(Path.Combine(created.Value.Path, "kept.txt"), "kept\n");

                    Result<FrozenChangeSet> result = await ChangeSetFreezer.FreezeAsync(created.Value);

                    await Assert.That(result.IsSuccess).IsTrue();
                    FrozenChangeSet set = result.Value;
                    await Assert.That(set.IgnoredPathCount).IsEqualTo(1);
                    await Assert.That(HasPath(set, "ignored.log")).IsFalse();
                    await Assert.That(HasPath(set, "kept.txt")).IsTrue();

                    using JsonDocument doc = JsonDocument.Parse(
                        await File.ReadAllTextAsync(Path.Combine(home, "runs", set.RunId, "changeset.json")));
                    await Assert.That(doc.RootElement.GetProperty("ignoredPathCount").GetInt32()).IsEqualTo(1);
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

    [Test]
    public async Task Freeze_EmptyChangeSet_SucceedsWithIsEmpty()
    {
        await WithIsolatedHome(async home =>
        {
            string dir = InitRepo();
            try
            {
                var created = await MaterializeAsync(dir);
                await Assert.That(created.IsSuccess).IsTrue();
                try
                {
                    Result<FrozenChangeSet> result = await ChangeSetFreezer.FreezeAsync(created.Value);

                    await Assert.That(result.IsSuccess).IsTrue();
                    FrozenChangeSet set = result.Value;
                    await Assert.That(set.IsEmpty).IsTrue();
                    await Assert.That(set.Entries.Count).IsEqualTo(0);
                    await Assert.That(set.HeadRevision).IsEqualTo(set.BaseRevision);
                    await Assert.That(File.Exists(set.PatchPath)).IsTrue();

                    var loaded = WorkspaceMaterializer.TryLoad(created.Value.RunId.Value);
                    await Assert.That(loaded.IsSuccess).IsTrue();
                    await Assert.That(loaded.Value.State).IsEqualTo(RunState.Changed);

                    using JsonDocument doc = JsonDocument.Parse(
                        await File.ReadAllTextAsync(Path.Combine(home, "runs", set.RunId, "changeset.json")));
                    await Assert.That(doc.RootElement.GetProperty("isEmpty").GetBoolean()).IsTrue();
                    await Assert.That(doc.RootElement.GetProperty("entries").GetArrayLength()).IsEqualTo(0);
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

    [Test]
    public async Task Freeze_Twice_ByteIdenticalPatch_NoSecondCommit()
    {
        await WithIsolatedHome(async _ =>
        {
            string dir = InitRepo();
            try
            {
                var created = await MaterializeAsync(dir);
                await Assert.That(created.IsSuccess).IsTrue();
                try
                {
                    await File.AppendAllTextAsync(Path.Combine(created.Value.Path, "a.txt"), "two\n");

                    Result<FrozenChangeSet> first = await ChangeSetFreezer.FreezeAsync(created.Value);
                    await Assert.That(first.IsSuccess).IsTrue();
                    byte[] firstPatch = await File.ReadAllBytesAsync(first.Value.PatchPath);
                    string commitsAfterFirst = GitOut(
                        created.Value.Path,
                        $"rev-list --count {first.Value.BaseRevision}..{first.Value.HeadRevision}").Trim();
                    await Assert.That(commitsAfterFirst).IsEqualTo("1");

                    Result<FrozenChangeSet> second = await ChangeSetFreezer.FreezeAsync(created.Value);
                    await Assert.That(second.IsSuccess).IsTrue();
                    byte[] secondPatch = await File.ReadAllBytesAsync(second.Value.PatchPath);

                    await Assert.That(secondPatch.SequenceEqual(firstPatch)).IsTrue();
                    await Assert.That(second.Value.HeadRevision).IsEqualTo(first.Value.HeadRevision);
                    string commitsAfterSecond = GitOut(
                        created.Value.Path,
                        $"rev-list --count {first.Value.BaseRevision}..{second.Value.HeadRevision}").Trim();
                    await Assert.That(commitsAfterSecond).IsEqualTo("1");
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

    [Test]
    public async Task Freeze_OverEntryCap_FailsNamingCap_LeavesRunIsolated()
    {
        await WithIsolatedHome(async home =>
        {
            string dir = InitRepo();
            try
            {
                var created = await MaterializeAsync(dir);
                await Assert.That(created.IsSuccess).IsTrue();
                try
                {
                    await File.AppendAllTextAsync(Path.Combine(created.Value.Path, "a.txt"), "two\n");
                    await File.WriteAllTextAsync(Path.Combine(created.Value.Path, "new.txt"), "new\n");

                    Result<FrozenChangeSet> result = await ChangeSetFreezer.FreezeAsync(
                        created.Value, ChangeSetFreezer.DefaultPatchByteCapBytes, maxEntries: 1);

                    await Assert.That(result.IsFailure).IsTrue();
                    await Assert.That(result.Error).Contains("cap");
                    await Assert.That(File.Exists(
                        Path.Combine(home, "runs", created.Value.RunId.Value, "change.patch"))).IsFalse();

                    var loaded = WorkspaceMaterializer.TryLoad(created.Value.RunId.Value);
                    await Assert.That(loaded.IsSuccess).IsTrue();
                    await Assert.That(loaded.Value.State).IsEqualTo(RunState.Isolated);
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

    [Test]
    public async Task Freeze_OverPatchCap_FailsNamingCap_NeverTruncated()
    {
        await WithIsolatedHome(async home =>
        {
            string dir = InitRepo();
            try
            {
                var created = await MaterializeAsync(dir);
                await Assert.That(created.IsSuccess).IsTrue();
                try
                {
                    await File.AppendAllTextAsync(Path.Combine(created.Value.Path, "a.txt"), "two\n");

                    Result<FrozenChangeSet> result = await ChangeSetFreezer.FreezeAsync(
                        created.Value, patchByteCapBytes: 10, maxEntries: ChangeSetFreezer.DefaultMaxEntries);

                    await Assert.That(result.IsFailure).IsTrue();
                    await Assert.That(result.Error).Contains("cap");
                    await Assert.That(File.Exists(
                        Path.Combine(home, "runs", created.Value.RunId.Value, "change.patch"))).IsFalse();
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

    [Test]
    public async Task Freeze_IdentityPinned_EvenUnderOperatorGitconfig()
    {
        await WithIsolatedHome(async _ =>
        {
            string dir = InitRepo();
            try
            {
                var created = await MaterializeAsync(dir);
                await Assert.That(created.IsSuccess).IsTrue();
                try
                {
                    // The operator's identity (repo-local beats global; the
                    // -c flags must beat both) must not leak into the freeze.
                    Git(created.Value.Path, "config user.name evil-operator");
                    Git(created.Value.Path, "config user.email evil@operator");
                    await File.AppendAllTextAsync(Path.Combine(created.Value.Path, "a.txt"), "two\n");

                    Result<FrozenChangeSet> result = await ChangeSetFreezer.FreezeAsync(created.Value);

                    await Assert.That(result.IsSuccess).IsTrue();
                    string author = GitOut(created.Value.Path, "log -1 --format=%an/%ae").Trim();
                    await Assert.That(author).IsEqualTo(
                        $"{ChangeSetFreezer.FreezeAuthorName}/{ChangeSetFreezer.FreezeAuthorEmail}");
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

    [Test]
    [RunOn(OS.Linux)]
    public async Task Freeze_HooksBypassed_PreCommitRejectorCannotInject()
    {
        await WithIsolatedHome(async _ =>
        {
            string dir = InitRepo();
            try
            {
                var created = await MaterializeAsync(dir);
                await Assert.That(created.IsSuccess).IsTrue();
                try
                {
                    // A linked worktree's `.git` is a pointer file: hooks live
                    // in the real git dir (`rev-parse --absolute-git-dir`).
                    string gitDir = GitOut(created.Value.Path, "rev-parse --absolute-git-dir").Trim();
                    Directory.CreateDirectory(Path.Combine(gitDir, "hooks"));
                    string hook = Path.Combine(gitDir, "hooks", "pre-commit");
                    await File.WriteAllTextAsync(hook, "#!/bin/sh\nexit 1\n");
                    ChmodPlusX(hook);
                    await File.AppendAllTextAsync(Path.Combine(created.Value.Path, "a.txt"), "two\n");

                    Result<FrozenChangeSet> result = await ChangeSetFreezer.FreezeAsync(created.Value);

                    await Assert.That(result.IsSuccess).IsTrue();
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

    [Test]
    public async Task Freeze_UnknownRun_Fails()
    {
        await WithIsolatedHome(async _ =>
        {
            string dir = InitRepo();
            try
            {
                var ghost = new IsolatedWorkspace(RunId.New(), dir, new string('0', 40));

                Result<FrozenChangeSet> result = await ChangeSetFreezer.FreezeAsync(ghost);

                await Assert.That(result.IsFailure).IsTrue();
            }
            finally
            {
                DeleteDir(dir);
            }
        });
    }

    private static FrozenChangeEntry Find(FrozenChangeSet set, string path)
    {
        for (int i = 0; i < set.Entries.Count; i++)
        {
            if (set.Entries[i].Path == path)
                return set.Entries[i];
        }
        throw new InvalidOperationException($"No frozen entry for '{path}'.");
    }

    private static bool HasPath(FrozenChangeSet set, string path)
    {
        for (int i = 0; i < set.Entries.Count; i++)
        {
            if (set.Entries[i].Path == path)
                return true;
        }
        return false;
    }

    private static async Task<Result<IsolatedWorkspace>> MaterializeAsync(string dir)
    {
        Result<WorkspaceContract> inspected =
            await WorkspaceInspector.InspectAsync(dir, WorkspaceIsolation.Worktree);
        await Assert.That(inspected.IsSuccess).IsTrue();
        return await WorkspaceMaterializer.MaterializeAsync(inspected.Value);
    }

    /// <summary>Fresh repo with a text file, a deletable file, a movable file and a binary file.</summary>
    protected override string InitRepo(bool branch = false)
    {
        string dir = NewTempDir("harbor-fz");
        Git(dir, "init");
        Git(dir, $"config user.email {GitAuthor.Email}");
        Git(dir, $"config user.name {GitAuthor.Name}");
        Git(dir, "config commit.gpgsign false");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "one\n");
        File.WriteAllText(Path.Combine(dir, "del.txt"), "del\n");
        File.WriteAllText(Path.Combine(dir, "mov.txt"), "movable\n");
        File.WriteAllBytes(Path.Combine(dir, "bin.dat"), [0x00, 0x01, 0x02, 0xFF, 0xFE]);
        Git(dir, "add -A");
        Git(dir, "commit -m init");
        Git(dir, $"checkout -b {GitAuthor.Name}-test");
        return dir;
    }

    // The HARBOR_HOME swap stays HERE, in the test class, on purpose: see
    // ChangeTrackingTestBase — ProcessEnvIsolationRule derives its writer set
    // and its clean-writer floor from the classes that write the process
    // environment, and this class is one of them.
    private static async Task WithIsolatedHome(Func<string, Task> body)
    {
        using var home = new TempDirScope("harbor-home");
        await EnvScope.WithAsync("HARBOR_HOME", home.Path, () => body(home.Path));    }


    /// <summary>Linux-only helper: make a hook file executable (the hooks test returns early off Linux).</summary>
    private static void ChmodPlusX(string path)
    {
        var psi = new ProcessStartInfo("chmod", $"+x \"{path}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"chmod +x {path}: process failed to start.");
        if (!proc.WaitForExit(30_000))
            throw new TimeoutException($"chmod +x {path} timed out.");
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"chmod +x {path} failed ({proc.ExitCode}).");
    }
}
