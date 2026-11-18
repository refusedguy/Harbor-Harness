using Harbor.Tools.Builtin;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     #2680 F159 — <see cref="GitCommandClassifier.IsMutating" /> is a pure
///     classifier over the raw shell command. These tests pin the observable
///     verdicts (read-only git, mutating git, compound commands, wrappers and
///     quoting) without starting a process or touching the permission engine.
/// </summary>
public class GitCommandClassifierTests
{
    [Test]
    public async Task ReadOnlyGitVerbs_AreNotMutating()
    {
        string[] commands =
        [
            "git status",
            "git status --porcelain=v1",
            "git diff",
            "git diff --stat HEAD~1",
            "git log -5 --oneline",
            "git -C /repo status",
            "git --no-pager diff",
            "git show HEAD",
            "git rev-parse HEAD",
            "gitk --all",
            "GIT push",
            "cat README.md",
            "echo 'git push'"
        ];

        foreach (string command in commands)
        {
            await Assert.That(GitCommandClassifier.IsMutating(command)).IsFalse();
        }
    }

    [Test]
    public async Task MutatingGitVerbs_AreMutating()
    {
        string[] commands =
        [
            "git push --force origin main",
            "git commit -m x",
            "git reset --hard HEAD~1",
            "git clean -fdx",
            "git checkout .",
            "git rebase -i HEAD~3",
            "git apply patch.diff",
            "git -C /repo push origin main",
            "git -c user.email=a@b commit -m x",
            "sudo git push",
            "env FOO=1 git push",
            "/usr/bin/git push",
            "git branch feature",
            "git tag v1.0",
            "git config --global user.email a@b",
            "git stash pop",
            "git worktree add /tmp/wt",
            "git submodule update",
            "git something-new"
        ];

        foreach (string command in commands)
        {
            await Assert.That(GitCommandClassifier.IsMutating(command)).IsTrue();
        }
    }

    [Test]
    public async Task ListingAndNounForms_AreNotMutating()
    {
        string[] commands =
        [
            "git branch",
            "git tag --list",
            "git stash list",
            "git stash show",
            "git worktree list",
            "git reflog show",
            "git remote -v",
            "git config --get user.email",
            "git config --list"
        ];

        foreach (string command in commands)
        {
            await Assert.That(GitCommandClassifier.IsMutating(command)).IsFalse();
        }
    }

    [Test]
    public async Task CompoundCommands_StillExposeTheMutatingGitCall()
    {
        string[] commands =
        [
            "git status; git push",
            "cat file && git commit -m x",
            "git diff || git reset --hard",
            "git log\n git clean -fdx",
            "echo 'git push'; git commit -m x"
        ];

        foreach (string command in commands)
        {
            await Assert.That(GitCommandClassifier.IsMutating(command)).IsTrue();
        }

        await Assert.That(GitCommandClassifier.IsMutating("git status && git log")).IsFalse();
    }

    [Test]
    public async Task QuotedSeparators_DoNotSplitTheCommand()
    {
        // The semicolon is inside a quoted commit message: there is one segment.
        await Assert.That(GitCommandClassifier.IsMutating("git commit -m 'push; status'")).IsTrue();

        // The git text is an argument of echo, not an executable.
        await Assert.That(GitCommandClassifier.IsMutating("echo \"git push\"")).IsFalse();
    }
}
