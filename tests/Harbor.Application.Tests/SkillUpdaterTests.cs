using CSharpFunctionalExtensions;
using Harbor.Application.Skills;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     Tests for <see cref="SkillUpdater" /> (KILLER_FEATURES §2.7 Feature 10,
///     issue #384): the <c>/skills update</c> re-resolve path. Every test pins a
///     report contract — updated / no-op (not git-backed) / failed (git error) —
///     through the injected <see cref="SkillGitRunner" />, so nothing here
///     shells out or touches the network.
/// </summary>
public class SkillUpdaterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("harbor-update").FullName;

    /// <summary>
    ///     Second, git-free temp root — a probe under <see cref="_dir" /> would
    ///     still resolve to its repository, so "not a work tree" needs a tree
    ///     with no <c>.git</c> anywhere above it.
    /// </summary>
    private readonly string _plainDir = Directory.CreateTempSubdirectory("harbor-plain").FullName;

    public void Dispose()
    {
        foreach (string dir in new[] { _dir, _plainDir })
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    /// <summary>Repo-shaped fixture: <c>&lt;root&gt;/.git</c> + a skills dir with one skill.</summary>
    private string GitBackedSkillsRoot()
    {
        string skills = Path.Combine(_dir, "skills");
        _ = Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        _ = Directory.CreateDirectory(Path.Combine(skills, "review"));
        return skills;
    }

    private static SkillGitRunner Git(int exitCode, string stdout = "", string stderr = "") =>
        (_, _, _) => Task.FromResult(Result.Success(new SkillGitResult(exitCode, stdout, stderr)));

    [Test]
    public async Task Update_GitBacked_PullsAndReportsUpdated()
    {
        string skills = GitBackedSkillsRoot();

        var report = await SkillUpdater.UpdateAsync(
            ["review"], skills, globalSkillsRoot: null, Git(0, "Already up to date."));

        await Assert.That(report.Outcome).IsEqualTo(SkillUpdateOutcome.Updated);
        await Assert.That(report.Targeted).IsEqualTo(1);
        await Assert.That(report.Message).Contains("re-resolved 1 skill(s)");
    }

    [Test]
    public async Task Update_GitBacked_PullsWithFastForwardOnly()
    {
        string skills = GitBackedSkillsRoot();
        IReadOnlyList<string>? captured = null;
        var report = await SkillUpdater.UpdateAsync(
            null, skills, globalSkillsRoot: null,
            (dir, args, _) =>
            {
                captured = args;
                return Task.FromResult(Result.Success(new SkillGitResult(0, string.Empty, string.Empty)));
            });

        await Assert.That(report.Outcome).IsEqualTo(SkillUpdateOutcome.Updated);
        await Assert.That(report.Targeted).IsEqualTo(0);
        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!).IsEquivalentTo(["pull", "--ff-only"]);
    }

    [Test]
    public async Task Update_NotGitBacked_IsNoOpWithHint()
    {
        string skills = Path.Combine(_plainDir, "skills");
        _ = Directory.CreateDirectory(skills);
        bool gitCalled = false;

        var report = await SkillUpdater.UpdateAsync(
            null, skills, globalSkillsRoot: null,
            (_, _, _) =>
            {
                gitCalled = true;
                return Task.FromResult(Result.Success(new SkillGitResult(0, string.Empty, string.Empty)));
            });

        await Assert.That(report.Outcome).IsEqualTo(SkillUpdateOutcome.NoOp);
        await Assert.That(report.Message).Contains("not git-backed");
        await Assert.That(report.Message).Contains("/skills refresh");
        await Assert.That(gitCalled).IsFalse();
    }

    [Test]
    public async Task Update_NoSource_IsNoOp()
    {
        var report = await SkillUpdater.UpdateAsync(
            null, Path.Combine(_plainDir, "absent"), globalSkillsRoot: null, Git(0));

        await Assert.That(report.Outcome).IsEqualTo(SkillUpdateOutcome.NoOp);
        await Assert.That(report.Message).Contains("no skills source");
    }

    [Test]
    public async Task Update_GitFails_IsFailedWithStderrFirstLine()
    {
        string skills = GitBackedSkillsRoot();

        var report = await SkillUpdater.UpdateAsync(
            ["review"], skills, globalSkillsRoot: null,
            Git(1, string.Empty, "fatal: could not read Username\nsecond line\n"));

        await Assert.That(report.Outcome).IsEqualTo(SkillUpdateOutcome.Failed);
        await Assert.That(report.Message).Contains("git pull failed");
        await Assert.That(report.Message).Contains("could not read Username");
        await Assert.That(report.Message).DoesNotContain("second line");
    }

    [Test]
    public async Task Update_GitMissingBinary_IsFailedNotThrow()
    {
        string skills = GitBackedSkillsRoot();

        var report = await SkillUpdater.UpdateAsync(
            ["review"], skills, globalSkillsRoot: null,
            (_, _, _) => Task.FromResult(Result.Failure<SkillGitResult>("git not found on PATH")));

        await Assert.That(report.Outcome).IsEqualTo(SkillUpdateOutcome.Failed);
        await Assert.That(report.Message).Contains("git not found on PATH");
    }

    [Test]
    public async Task Update_PrefersProjectRootOverGlobal()
    {
        string project = GitBackedSkillsRoot();
        string global = Path.Combine(_dir, "global-skills");
        _ = Directory.CreateDirectory(Path.Combine(global, "review"));
        _ = Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        string? workDir = null;

        var report = await SkillUpdater.UpdateAsync(
            ["review"], project, global,
            (dir, _, _) =>
            {
                workDir = dir;
                return Task.FromResult(Result.Success(new SkillGitResult(0, string.Empty, string.Empty)));
            });

        await Assert.That(report.Outcome).IsEqualTo(SkillUpdateOutcome.Updated);
        await Assert.That(workDir).IsEqualTo(_dir);
    }

    [Test]
    public async Task FindRepositoryRoot_WalksUpToGitMarker()
    {
        string skills = GitBackedSkillsRoot();
        string? repo = SkillUpdater.FindRepositoryRoot(Path.Combine(skills, "review"));
        await Assert.That(repo).IsEqualTo(_dir);

        string loose = Path.Combine(_plainDir, "loose");
        _ = Directory.CreateDirectory(loose);
        await Assert.That(SkillUpdater.FindRepositoryRoot(loose)).IsNull();
        await Assert.That(SkillUpdater.FindRepositoryRoot(null)).IsNull();
    }

    [Test]
    public async Task Update_FallsBackToFirstRootWhenRequestedSkillAbsent()
    {
        // A `✗ missing` skill is not on disk — pulling the source is exactly how
        // it arrives, so the update must still reach the repo.
        string project = GitBackedSkillsRoot();
        string? workDir = null;

        var report = await SkillUpdater.UpdateAsync(
            ["not-installed"], project, globalSkillsRoot: null,
            (dir, _, _) =>
            {
                workDir = dir;
                return Task.FromResult(Result.Success(new SkillGitResult(0, string.Empty, string.Empty)));
            });

        await Assert.That(report.Outcome).IsEqualTo(SkillUpdateOutcome.Updated);
        await Assert.That(workDir).IsEqualTo(_dir);
    }
}
